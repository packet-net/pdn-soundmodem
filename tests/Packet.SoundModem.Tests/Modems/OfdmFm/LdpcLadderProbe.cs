using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The calibration an LDPC rung needs before the ladder may carry it: its cliff, its goodput,
/// how much of the code a burst at the cliff spends, and how fast that spending falls per
/// decibel.
/// </summary>
/// <remarks>
/// <para>The A/B (<see cref="LdpcAbProbe"/>) said LDPC 1/2 beats the convolutional 1/2 above the
/// FM threshold; this measures the numbers a rung on <see cref="OfdmFmRateLadder"/> actually
/// carries, on the standard grid configuration - the 8 kHz preset, R1/T13 narrow at 2500 Hz, a
/// 256-byte payload - so they are comparable with the convolutional grid in
/// docs/dev/ofdm-fm/receiver-findings.md. At 256 bytes the LDPC frames round to the SAME symbol
/// counts as convolutional 1/2 on all four constellations, so the goodput column carries over and
/// the cliffs compare directly.</para>
/// <para>A measurement, not a check: run with <c>OFDMFM_PROBE=1</c>, seeds via
/// <c>OFDMFM_SEEDS</c> (default 48).</para>
/// </remarks>
public class LdpcLadderProbe
{
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void What_An_Ldpc_Rung_Would_Carry()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null,
            "set OFDMFM_PROBE=1 for the LDPC rung calibration - a campaign, not a check");

        int seeds =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 48;

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        FmLinkProfile link = FmLinkProfile.DataPort(LegalPeakDeviationHz, audioHighHz: null);
        var payload = new byte[256];
        new Random(1000).NextBytes(payload);

        Console.WriteLine(
            $"8 kHz preset, DataPort {LegalPeakDeviationHz} Hz, 256 B, LDPC 1/2, {seeds} seeds. "
            + "Sweeps run from 2 dB below the CONVOLUTIONAL 1/2 cliff, so the LDPC cliff lands "
            + "inside them. Those cliffs were measured on a narrower span, so a window may need "
            + "moving before its 50 % point is inside it.");

        foreach ((OfdmFmConstellation constellation, double convCliff) in
            (ReadOnlySpan<(OfdmFmConstellation, double)>)
            [
                (OfdmFmConstellation.Qpsk, 6.3),
                (OfdmFmConstellation.Qam16, 10.3),
                (OfdmFmConstellation.Qam64, 15.5),
                (OfdmFmConstellation.Qam256, 19.0),
            ])
        {
            var coded = profile with { Coding = new OfdmFmCoding(OfdmFmFec.Ldpc) };
            var codec = new OfdmFmBurstCodec(coded);
            float[] clean = codec.Modulate(payload, constellation, 2);
            double seconds = clean.Length / (double)coded.SampleRate;
            double goodput = payload.Length * 8 / seconds;

            var recovered = new List<int>();
            var spendings = new List<double>();
            var cnrs = new List<double>();
            for (double cnr = convCliff - 2; cnr <= convCliff + 4.01; cnr += 1)
            {
                int copied = 0;
                var spent = new List<double>();
                for (int seed = 0; seed < seeds; seed++)
                {
                    float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, cnr);
                    OfdmFmBurst? burst = codec.Demodulate(heard);
                    if (burst?.Payload is byte[] got && got.AsSpan().SequenceEqual(payload))
                    {
                        copied++;
                        if (burst.PreFecBitErrorRate is double ber)
                        {
                            spent.Add(ber);
                        }
                    }
                }

                spent.Sort();
                cnrs.Add(cnr);
                recovered.Add(copied);
                spendings.Add(spent.Count > 0 ? spent[spent.Count / 2] : double.NaN);
            }

            Console.WriteLine(
                $"| LDPC 1/2 {constellation} | " + string.Join(" | ", cnrs.Select(c => $"+{c:0.0}"))
                + " |");
            Console.WriteLine(
                "| recovered | " + string.Join(" | ", recovered.Select(r => $"{r}/{seeds}")) + " |");
            Console.WriteLine(
                "| median spent | " + string.Join(" | ", spendings.Select(b => $"{b:0.0000}")) + " |");

            // The cliff: where half the frames land, interpolated across the 1 dB step.
            double half = seeds / 2.0;
            double cliff = double.NaN;
            for (int i = 1; i < recovered.Count; i++)
            {
                if (recovered[i - 1] < half && recovered[i] >= half)
                {
                    cliff = cnrs[i - 1]
                        + ((half - recovered[i - 1]) / (recovered[i] - recovered[i - 1]));
                    break;
                }
            }

            // The spending slope: how fast the median pre-FEC error rate falls per decibel,
            // fitted log-linearly over the points above the cliff with a usable median - the
            // same first-4-dB region the convolutional slopes were fitted over.
            var fit = new List<(double Cnr, double Log)>();
            for (int i = 0; i < cnrs.Count; i++)
            {
                if (!double.IsNaN(cliff) && cnrs[i] > cliff && cnrs[i] <= cliff + 4
                    && spendings[i] > 0 && !double.IsNaN(spendings[i]))
                {
                    fit.Add((cnrs[i], Math.Log(spendings[i])));
                }
            }

            double slope = double.NaN;
            if (fit.Count >= 2)
            {
                double n = fit.Count;
                double sx = fit.Sum(p => p.Cnr);
                double sy = fit.Sum(p => p.Log);
                double sxx = fit.Sum(p => p.Cnr * p.Cnr);
                double sxy = fit.Sum(p => p.Cnr * p.Log);
                slope = Math.Exp(((n * sxy) - (sx * sy)) / ((n * sxx) - (sx * sx)));
            }

            // The spending at the cliff itself, interpolated the same way the margin measure was.
            double cliffSpent = double.NaN;
            for (int i = 1; i < cnrs.Count; i++)
            {
                if (!double.IsNaN(cliff) && cnrs[i - 1] <= cliff && cnrs[i] > cliff
                    && !double.IsNaN(spendings[i - 1]) && !double.IsNaN(spendings[i]))
                {
                    double f = cliff - cnrs[i - 1];
                    cliffSpent = spendings[i - 1] + (f * (spendings[i] - spendings[i - 1]));
                    break;
                }
            }

            Console.WriteLine(
                $"| summary | cliff {cliff:+0.0} dB | goodput {goodput:0} bit/s | "
                + $"cliff spending {cliffSpent:0.000} | slope {slope:0.00}/dB | "
                + $"conv 1/2 cliff {convCliff:+0.0} |");
            Console.WriteLine();
        }
    }
}
