using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The instrument that proves the direct-FSK timing phases are real (rx-roadmap workstream 10,
/// issue #331). A ladder row can read a clean null when the phases have silently collapsed onto
/// one another - on the PSK modes a static-initialiser ordering slip once had them at
/// [0, -0, 0] and every number looked like an honest negative - so score a known burst phase by
/// phase and check the error sets differ before believing any decode figure.
/// </summary>
public class FskTimingDiversityTests(ITestOutputHelper output)
{
    private const int SampleRate = 48000;

    private static byte[] SampleFrame(int seed, int infoLength)
    {
        var frame = new byte[16 + infoLength];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(seed).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static float[] Noisy(float[] audio, int seed, float sigma)
    {
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + (2 * pad)];
        audio.CopyTo(padded, pad);
        var random = new Random(seed);
        for (int i = 0; i < padded.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            padded[i] += sigma * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return padded;
    }

    /// <summary>The transmitted wire bits for an IL2P+CRC burst, which is what every phase is
    /// trying to read: the framer's preamble and sync word, then the coded frame.</summary>
    private static int[] WireBits(byte[] frame, int txDelayMilliseconds, int baud)
    {
        byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);
        int preambleBits = Math.Max(16, txDelayMilliseconds * baud / 1000);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Alternating);
        return [.. bits.Select(b => (int)b)];
    }

    /// <summary>Wrong bits, and where they are, for one phase's decisions against the
    /// transmitted bits - aligned at the offset that reads the burst best, since the receiver's
    /// stream starts wherever the padding and the filters put it.</summary>
    private static (int Wrong, HashSet<int> Where) Score(List<int> decided, int[] wire)
    {
        int best = int.MaxValue;
        HashSet<int> bestWhere = [];
        for (int offset = 0; offset + wire.Length <= decided.Count; offset++)
        {
            int wrong = 0;
            HashSet<int> where = [];
            for (int i = 0; i < wire.Length; i++)
            {
                if (decided[offset + i] != wire[i])
                {
                    wrong++;
                    where.Add(i);
                }
            }

            if (wrong < best)
            {
                best = wrong;
                bestWhere = where;
            }
        }

        return (best, bestWhere);
    }

    [Theory]
    [InlineData(9600, 0.55f)]
    [InlineData(4800, 0.55f)]
    public void The_Timing_Phases_Read_The_Same_Burst_Differently(int baud, float sigma)
    {
        byte[] frame = SampleFrame(7, 60);
        var tx = new FskModem(SampleRate, _ => { }, FskFraming.Il2pCrc, baud);
        var rx = new FskModem(SampleRate, _ => { }, FskFraming.Il2pCrc, baud);
        var decided = new List<int>[TimingDiversity.PhaseCount];
        for (int phase = 0; phase < decided.Length; phase++)
        {
            decided[phase] = [];
        }

        rx.PhaseDecisionObserver = (phase, level) => decided[phase].Add(level);
        rx.Process(Noisy(tx.Modulate(frame, txDelayMilliseconds: 60), seed: 11, sigma));

        int[] wire = WireBits(frame, 60, baud);
        var scores = new (int Wrong, HashSet<int> Where)[decided.Length];
        for (int phase = 0; phase < decided.Length; phase++)
        {
            scores[phase] = Score(decided[phase], wire);
            output.WriteLine(
                $"fsk{baud} phase {phase} "
                + $"({TimingDiversity.FskPhaseFractions[phase] * 100:+0.0;-0.0;0.0} %): "
                + $"{scores[phase].Wrong} wrong of {wire.Length}");
        }

        decided.Should().AllSatisfy(
            stream => stream.Should().NotBeEmpty("every phase decides every symbol"));

        // The offsets themselves must be distinct - the [0, -0, 0] failure is a collapsed
        // phase set that still looks like seven phases from the outside.
        TimingDiversity.FskPhaseFractions.Distinct().Should().HaveCount(
            TimingDiversity.PhaseCount, "the phase offsets are seven distinct fractions");

        // And the decisions must actually differ: same burst, same noise, different instants.
        for (int phase = 1; phase < decided.Length; phase++)
        {
            scores[phase].Where.Should().NotEqual(
                scores[0].Where,
                "phase {0} decides at a different instant from the clock's own, so it must "
                + "get a different set of bits wrong on the same noisy burst",
                phase);
        }
    }

    /// <summary>Resamples a burst as if the transmitter's clock ran <paramref name="ppm"/>
    /// parts per million fast or slow: the receiver's own rate is fixed, so a mistuned
    /// transmitter is exactly a burst whose samples arrive at the wrong spacing.</summary>
    private static float[] Mistuned(float[] audio, double ppm)
    {
        double step = 1.0 + (ppm * 1e-6);
        var stretched = new List<float>((int)(audio.Length / step) + 2);
        for (double at = 0; at < audio.Length - 1; at += step)
        {
            int lower = (int)at;
            float fraction = (float)(at - lower);
            stretched.Add(audio[lower] + (fraction * (audio[lower + 1] - audio[lower])));
        }

        return [.. stretched];
    }

    /// <summary>
    /// The symbol clock follows a transmitter that is not on our sample rate. A soundcard pair,
    /// or a soundcard and a NinoTNC's crystal, can differ by hundreds of parts per million, and
    /// a clock that could not follow one would decode the start of every long frame and lose the
    /// end.
    /// </summary>
    /// <remarks>
    /// This is the test that rejected the DCD-gated clock hold for these modes (see
    /// <see cref="FskModem"/>): at inertia 0.995 every row here fails from +-500 ppm, because a
    /// chain that does not resolve its zero crossings cannot afford a loop that corrects half a
    /// per cent of the error per transition. Fixed at 0.74, +-2000 ppm is comfortable.
    /// </remarks>
    [Theory]
    [InlineData(9600, 500.0)]
    [InlineData(9600, -500.0)]
    [InlineData(9600, 2000.0)]
    [InlineData(9600, -2000.0)]
    [InlineData(4800, 500.0)]
    [InlineData(4800, -500.0)]
    [InlineData(4800, 2000.0)]
    [InlineData(4800, -2000.0)]
    public void The_Clock_Tracks_A_Mistuned_Transmitter(int baud, double ppm)
    {
        // A full-length frame, so a clock that merely started right and then slipped would run
        // out of eye long before the last byte.
        byte[] frame = SampleFrame(5, 200);
        var frames = new List<byte[]>();
        var tx = new FskModem(SampleRate, _ => { }, FskFraming.Il2pCrc, baud);
        var rx = new FskModem(SampleRate, frames.Add, FskFraming.Il2pCrc, baud);

        rx.Process(Mistuned(Noisy(tx.Modulate(frame, txDelayMilliseconds: 150), seed: 3, 0f), ppm));

        frames.Should().ContainSingle(
            "the clock corrects 26 % of the timing error per transition, which follows {0} ppm "
            + "with a small fraction of a symbol of lag", ppm).Which.Should().Equal(frame);
    }
}
