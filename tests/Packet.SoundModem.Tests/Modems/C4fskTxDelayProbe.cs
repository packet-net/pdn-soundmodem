using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Bench probe (set <c>C4FSK_TXDELAY_PROBE=1</c>) for issue #336: what state the C4FSK receiver
/// is in when the sync word arrives, after a short run-in and a long one, at a couple of SNRs.
/// It reads <see cref="C4fskModem.DecisionObserver"/> on the sim rig's own burst (the same
/// frame, the same AWGN calibration, the same seeds as <c>sm-ota sim</c>) and reports, per
/// (mode, SNR, TXDELAY) over a run of seeds: the envelope tracker's half-swing at the sync
/// against the clean burst's, where the sync word's own outer symbols sit in normalised units,
/// the equalizer's taps and whether they were frozen, where the first data symbols' outer and
/// inner levels sit, and the symbol error rate at the front of the frame and over the whole of
/// it. The row that moves with TXDELAY is the mechanism.
/// </summary>
public class C4fskTxDelayProbe(ITestOutputHelper output)
{
    private const int Seeds = 40;

    [Fact]
    public void What_The_Receiver_Looks_Like_When_The_Sync_Word_Arrives()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_TXDELAY_PROBE") is null, "bench probe only");

        foreach ((string mode, double[] snrs) in new (string, double[])[]
                 {
                     ("c4fsk9600", [18, 22, 60]),
                     ("c4fsk19200", [21, 25, 60]),
                 })
        {
            foreach (double snr in snrs)
            {
                foreach (int txd in new[] { 0, 150, 300 })
                {
                    output.WriteLine(Summarise(mode, snr, txd));
                }
            }
        }
    }

    private static string Summarise(string mode, double snr, int txd)
    {
        int decoded = 0;
        int syncFound = 0;
        int frozenAtSync = 0;
        double halfRatio = 0;
        double halfRatioStart = 0;
        double syncNorm = 0;
        double syncEq = 0;
        double tapDeviation = 0;
        var taps = new double[C4fskModem.EqualizerLength];
        double frontOuter = 0;
        double frontInner = 0;
        double frontErrors = 0;
        int frontDemotions = 0;
        double wholeErrors = 0;
        var byQuarter = new int[4];
        var trajectory = new double[4];
        var phaseErrors = new int[C4fskModem.TimingPhaseCount];
        var tapsLate = new double[C4fskModem.EqualizerLength];
        int scored = 0;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            C4fskTxDelayRig.Burst b = C4fskTxDelayRig.Run(mode, snr, txd, seed);
            decoded += b.Decoded ? 1 : 0;
            if (b.SyncSymbol < 0)
            {
                continue;
            }

            syncFound++;
            scored++;
            frozenAtSync += b.FrozenAtSync ? 1 : 0;
            halfRatio += b.HalfAtSync / b.CleanHalf;
            halfRatioStart += b.HalfAtStart / b.CleanHalf;
            syncNorm += b.SyncNormalised;
            syncEq += b.SyncEqualized;
            tapDeviation += b.TapDeviation;
            for (int t = 0; t < taps.Length; t++)
            {
                taps[t] += b.Taps[t];
            }

            frontOuter += b.FrontOuter;
            frontInner += b.FrontInner;
            frontErrors += b.FrontErrors;
            frontDemotions += b.FrontDemotions;
            wholeErrors += b.WholeErrors;
            for (int q = 0; q < 4; q++)
            {
                byQuarter[q] += b.ErrorsByQuarter[q];
                trajectory[q] += b.HalfTrajectory[q];
            }

            for (int phase = 0; phase < phaseErrors.Length; phase++)
            {
                phaseErrors[phase] += b.PhaseErrors[phase];
            }

            for (int t = 0; t < tapsLate.Length; t++)
            {
                tapsLate[t] += b.TapsLate[t];
            }
        }

        if (scored == 0)
        {
            return $"{mode} {snr,4:0} dB txd {txd,3}: decoded {decoded}/{Seeds}, sync never found";
        }

        string tapText = string.Join(" ", taps.Select(t => (t / scored).ToString("+0.000;-0.000")));
        return $"{mode} {snr,4:0} dB txd {txd,3}: decoded {decoded,2}/{Seeds} sync {syncFound,2}/{Seeds} | "
            + $"env half/clean {halfRatioStart / scored:0.000} at start -> {halfRatio / scored:0.000} at sync | "
            + $"sync outer |norm| {syncNorm / scored:0.000} |eq| {syncEq / scored:0.000} | "
            + $"taps [{tapText}] dev {tapDeviation / scored:0.000} frozen {frozenAtSync}/{scored} | "
            + $"front outer {frontOuter / scored:0.000} inner {frontInner / scored:0.000} "
            + $"sym err front {100 * frontErrors / scored:0.0} % ({frontDemotions} outer->inner) "
            + $"whole {100 * wholeErrors / scored:0.0} % by quarter [{string.Join(' ', byQuarter)}] | "
            + $"env at sync +0/100/200/300 [{string.Join(' ', trajectory.Select(t => (t / scored).ToString("0.000")))}] | "
            + $"errors by phase [{string.Join(' ', phaseErrors)}] | "
            + $"taps at +200 [{string.Join(" ", tapsLate.Select(t => (t / scored).ToString("+0.000;-0.000")))}]";
    }
}

/// <summary>Bench probe (same gate): the rendered preamble's mean power against the frame's,
/// which is what the sim channel's SNR calibration sees when it sets the noise against the
/// whole active burst.</summary>
public class C4fskPreamblePowerProbe(ITestOutputHelper output)
{
    [Fact]
    public void Preamble_Power_Against_Frame_Power()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_TXDELAY_PROBE") is null, "bench probe only");
        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
        {
            int symbolRate = mode == "c4fsk9600" ? 4800 : 9600;
            C4fskModem tx = mode == "c4fsk9600"
                ? C4fskModem.C4fsk9600(48000, _ => { })
                : C4fskModem.C4fsk19200(48000, _ => { });
            byte[] frame = new byte[60];
            new Random(1).NextBytes(frame);
            float[] audio = tx.Modulate(frame, 300);
            int preambleSamples = 300 * 48000 / 1000;
            int symbolSamples = 48000 / symbolRate;
            double preamble = 0;
            for (int i = 48 * symbolSamples; i < preambleSamples - (48 * symbolSamples); i++)
            {
                preamble += audio[i] * audio[i];
            }

            preamble /= preambleSamples - (96 * symbolSamples);
            int frameStart = preambleSamples + (16 * symbolSamples) + 48;
            int frameSamples = (M0LTE.Il2p.Il2pCodec.Encode(frame, appendCrc: true).Length * 4 * symbolSamples) - 96;
            double body = 0;
            for (int i = frameStart; i < frameStart + frameSamples; i++)
            {
                body += audio[i] * audio[i];
            }

            body /= frameSamples;
            output.WriteLine(
                $"{mode}: preamble mean square {preamble:0.000}, frame {body:0.000}, "
                + $"ratio {preamble / body:0.00} = {10 * Math.Log10(preamble / body):0.00} dB");
        }
    }
}

/// <summary>Bench probe (same gate): a row-by-row dump of phase 0 around the sync word on one
/// clean burst after a long run-in.</summary>
public class C4fskSyncRowsProbe(ITestOutputHelper output)
{
    [Fact]
    public void Rows_Around_The_Sync_Word()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("C4FSK_TXDELAY_PROBE") is null, "bench probe only");
        foreach (string line in C4fskTxDelayRig.RowsAroundSync("c4fsk9600", 60, 300, 1))
        {
            output.WriteLine(line);
        }
    }
}
