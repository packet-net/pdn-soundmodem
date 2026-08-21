using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The instrument that catches a timing-diversity set which is not actually diverse. The first
/// cut of the PSK phases measured a clean null on every ladder row because a static-initialiser
/// ordering slip had the fractions at [0, -0, 0]; nothing downstream noticed, because seven
/// identical decoders agree perfectly and the ladder reads exactly the number it read before.
/// So: score a known bit stream at every phase against what was sent, and require every phase to
/// disagree with the clock instant somewhere, at a noise level that actually costs bits. Phase 0
/// must stay exactly what the demodulator's plain bit sink has always emitted.
/// </summary>
public class AfskTimingDiversityTests
{
    private const int SampleRate = 12000;
    private const int Baud = 300;
    private const double Centre = 1700;
    private const double ToneShift = 100;

    /// <summary>Enough noise to cost bits: these modes band-pass a twentieth of the Nyquist
    /// width, so broadband sigma has to be well above unity before the eye closes at all. Under
    /// this, the clock instant reads the burst at a few per cent bit error.</summary>
    private const double NoiseSigma = 1.0;

    /// <summary>Levels sent, and what each timing phase made of them.</summary>
    private static (byte[] Sent, List<int>[] PerPhase, List<int> PlainSink) Probe(
        double noiseSigma, int seed)
    {
        var random = new Random(seed);
        // A run of alternating levels to pull the clock in, then random data with plenty of
        // transitions for the DPLL and plenty of ways for a mistimed phase to be wrong.
        var sent = new byte[64 + 1024];
        for (int i = 0; i < 64; i++)
        {
            sent[i] = (byte)(i & 1);
        }

        for (int i = 64; i < sent.Length; i++)
        {
            sent[i] = (byte)random.Next(2);
        }

        var modulator = new AfskModulator(SampleRate, Baud, Centre - ToneShift, Centre + ToneShift);
        float[] audio = modulator.ModulateLevels(sent);

        int pad = SampleRate / 5;
        var channel = new float[audio.Length + (2 * pad)];
        audio.CopyTo(channel, pad);
        for (int i = 0; i < channel.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            channel[i] += (float)(noiseSigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        int phases = AfskDemodulator.TimingPhaseCount;
        var perPhase = new List<int>[phases];
        for (int phase = 0; phase < phases; phase++)
        {
            perPhase[phase] = [];
        }

        var plain = new List<int>();
        var demodulator = new AfskDemodulator(
            SampleRate, plain.Add, Centre, Baud,
            bandPassHalfWidth: 300, lowPassCutoff: 300, toneShift: ToneShift,
            phaseBitSink: (level, phase) => perPhase[phase].Add(level));

        // Daemon-sized blocks, so the deferred decisions are exercised across block boundaries.
        int block = SampleRate / 10;
        for (int position = 0; position < channel.Length; position += block)
        {
            demodulator.Process(channel.AsSpan(position, Math.Min(block, channel.Length - position)));
        }

        return (sent, perPhase, plain);
    }

    /// <summary>Bit positions this phase got wrong, at the alignment and polarity that suit it
    /// best - the chain's mark assignment is a free choice (AX.25 rides NRZI and the IL2P
    /// receivers hunt sync both ways), and the filters delay the stream by an amount nothing
    /// downstream needs to know.</summary>
    private static HashSet<int> ErrorsOf(byte[] sent, List<int> received)
    {
        HashSet<int>? best = null;
        for (int offset = 0; offset + sent.Length <= received.Count; offset++)
        {
            for (int invert = 0; invert < 2; invert++)
            {
                var errors = new HashSet<int>();
                for (int i = 0; i < sent.Length; i++)
                {
                    if ((received[offset + i] ^ invert) != sent[i])
                    {
                        errors.Add(i);
                    }
                }

                if (best is null || errors.Count < best.Count)
                {
                    best = errors;
                }
            }
        }

        return best ?? [];
    }

    [Fact]
    public void Phase_Zero_Is_Exactly_What_The_Plain_Bit_Sink_Emits()
    {
        (byte[] _, List<int>[] perPhase, List<int> plain) = Probe(NoiseSigma, seed: 1);

        plain.Should().NotBeEmpty();
        perPhase[0].Should().Equal(plain);
    }

    [Fact]
    public void Every_Timing_Phase_Reads_A_Noisy_Burst_Differently_From_The_Clock_Instant()
    {
        (byte[] sent, List<int>[] perPhase, List<int> _) = Probe(NoiseSigma, seed: 1);

        var errors = new HashSet<int>[perPhase.Length];
        for (int phase = 0; phase < perPhase.Length; phase++)
        {
            errors[phase] = ErrorsOf(sent, perPhase[phase]);
        }

        // The clock instant must be reading the burst at all, or the rest of this proves nothing.
        errors[0].Count.Should().BeLessThan(sent.Length / 10);

        // Every early/late phase must disagree with the instant somewhere: identical error sets
        // across the set mean the offsets collapsed and the diversity is a decoration.
        for (int phase = 1; phase < perPhase.Length; phase++)
        {
            errors[phase].SetEquals(errors[0]).Should().BeFalse(
                "phase {0} decided every one of {1} bits exactly as the clock instant did, so its "
                + "offset is not reaching a different sample",
                phase,
                sent.Length);
        }
    }
}
