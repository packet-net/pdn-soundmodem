using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The instrument PR #330 learned to build before believing any ladder number: proof that the
/// timing phases actually decide differently. A static-initialiser ordering slip once left the
/// PSK phase set at [0, -0, 0] and the ladder read a clean null, because three phases deciding
/// the same instant are one phase run three times; per-phase scoring of a known frame is what
/// catches that, and the ladder is not.
/// </summary>
public class C4fskTimingDiversityTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_Timing_Phase_Sits_At_Its_Own_Offset_From_The_Clock_Instant()
    {
        double[] fractions = C4fskModem.TimingPhaseFractions.ToArray();

        fractions.Should().HaveCount(C4fskModem.TimingPhaseCount);
        fractions[0].Should().Be(0, "phase 0 is the recovered clock's own instant");
        fractions.Distinct().Should().HaveCount(
            fractions.Length,
            "phases that share an offset are one phase counted several times - the [0, -0, 0] "
            + "slip that made PR #330's first cut measure a null");
        fractions.Select(Math.Abs).Distinct().Should().HaveCount(
            (fractions.Length + 1) / 2, "the phases are early/late pairs about the instant");
    }

    /// <summary>
    /// A burst noisy enough to make the decision stage work: the phases do not all decide the
    /// same bits, and their wrong-byte sets against the transmitted wire are not all the same
    /// set. That difference is the whole mechanism - a frame beyond one phase's Reed-Solomon
    /// budget can be inside another's - so it is asserted rather than assumed.
    /// </summary>
    /// <remarks>
    /// The reading it prints is worth looking at when this file is touched: the late phases are
    /// measurably worse than the early ones at the same offset (at +15 % the sync word itself
    /// loses a symbol, where -15 % is byte-perfect), because the 4-PAM clock's wrap can only
    /// land on a sample, so the decision instant is already up to a tenth of a symbol late and
    /// the phase set is not centred on the eye.
    /// </remarks>
    [Theory]
    [InlineData("c4fsk9600", 0.24f)]
    [InlineData("c4fsk19200", 0.20f)]
    public void The_Timing_Phases_Make_Different_Errors_On_A_Noisy_Burst(string mode, float sigma)
    {
        const int rate = 48000;
        byte[] frame =
        [
            0xA0, 0xA4, 0xA6, 0x40, 0x40, 0x40, 0xE0,       // "PRS" (command)
            0x9A, 0x60, 0x98, 0xA8, 0x8A, 0x40, 0x63,       // "M0LTE"-1
            0x03, 0xF0,
            .. System.Text.Encoding.ASCII.GetBytes(
                "seven decision phases per symbol, one clock, one front end"),
        ];
        byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);

        C4fskModem tx = Make(mode, _ => { });
        float[] burst = tx.Modulate(frame, 120);

        // Deterministic in the seed, as the sim rig is: the same audio every run, so a
        // difference between phases is the phases and not the noise.
        var random = new Random(20260821);
        var audio = new float[(rate / 4) + burst.Length + (rate / 4)];
        for (int i = 0; i < audio.Length; i++)
        {
            float signal = i >= rate / 4 && i - (rate / 4) < burst.Length ? burst[i - (rate / 4)] : 0;
            audio[i] = signal + (sigma * (float)((random.NextDouble() + random.NextDouble()
                + random.NextDouble() - 1.5) / 1.5));
        }

        var bits = new List<int>[C4fskModem.TimingPhaseCount];
        for (int phase = 0; phase < bits.Length; phase++)
        {
            bits[phase] = [];
        }

        C4fskModem rx = Make(mode, _ => { });
        rx.PhaseDibitObserver = (phase, first, second) =>
        {
            bits[phase].Add(first);
            bits[phase].Add(second);
        };
        rx.Process(audio);

        bits.Select(stream => string.Concat(stream)).Distinct().Should().HaveCountGreaterThan(
            1,
            "phases that decide the same instant produce the same bits: this is the cheap, "
            + "unambiguous form of the [0, -0, 0] check");

        // The phases decide in lockstep - one decision each per symbol - so the clock instant's
        // own sync position indexes every phase's stream, and a phase whose sync word itself is
        // damaged is still scored rather than dropped.
        int sync = SyncBitIndex(bits[0], wire.Length);
        sync.Should().BeGreaterThanOrEqualTo(
            0, "the clock instant's own reading must find the sync word, or this test is "
            + "measuring acquisition rather than the decision stage");

        var errorSets = new List<string>();
        for (int phase = 0; phase < bits.Length; phase++)
        {
            int[] wrong = WrongBytes(bits[phase], sync, wire);
            int syncBits = 0;
            for (int k = 0; k < 24; k++)
            {
                syncBits = (syncBits << 1) | bits[phase][sync - 24 + k];
            }

            output.WriteLine(
                $"{mode} phase {phase} ({C4fskModem.TimingPhaseFractions[phase] * 100:+0.0;-0.0;0.0} %): "
                + $"sync {syncBits:X6} {wrong.Length} wrong bytes of {wire.Length} "
                + $"at [{string.Join(',', wrong)}]");
            errorSets.Add(string.Join(',', wrong));
        }

        errorSets.Distinct().Should().HaveCountGreaterThan(
            1,
            "the timing phases must genuinely decide differently: identical error sets mean one "
            + "phase run seven times, which is what the [0, -0, 0] slip looked like");
    }

    private static C4fskModem Make(string mode, Action<byte[]> received) =>
        mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(48000, received)
            : C4fskModem.C4fsk19200(48000, received);

    /// <summary>Where the wire's first bit sits in a decided bit stream: the bit after the
    /// 24-bit sync word the deframer hunts, or -1 if this stream never carried one.</summary>
    private static int SyncBitIndex(List<int> decided, int wireBytes)
    {
        const int syncWord = 0x57DF7F;
        int hunt = 0;
        for (int i = 0; i < decided.Count; i++)
        {
            hunt = ((hunt << 1) | decided[i]) & 0xFFFFFF;
            if (hunt == syncWord && i + 1 + (8 * wireBytes) <= decided.Count)
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>Indices of the wire bytes this phase read wrongly, scored the way the off-air
    /// fixture is: the bytes it decided from <paramref name="from"/> against what was
    /// transmitted.</summary>
    private static int[] WrongBytes(List<int> decided, int from, byte[] wire)
    {
        var wrong = new List<int>();
        for (int b = 0; b < wire.Length; b++)
        {
            int value = 0;
            for (int k = 0; k < 8; k++)
            {
                value = (value << 1) | decided[from + (8 * b) + k];
            }

            if (value != wire[b])
            {
                wrong.Add(b);
            }
        }

        return [.. wrong];
    }
}
