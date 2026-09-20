using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// The calibration a punctured rung needs before the ladder may carry it: its cliff, its goodput,
/// how much of the code a burst at the cliff spends, and how fast that spending falls per decibel.
/// </summary>
/// <remarks>
/// <para>Rates 5/6 and 7/8 on the K=7 code took coding ids 8 and 9 (packet-net/pdn-ofdm-fm#10), and
/// a rung on <see cref="OfdmFmRateLadder"/> needs four measured numbers before the rate controller
/// can reason about it. This is <see cref="RateLadderGridProbe"/>'s cliff method and
/// <see cref="MarginMeasureProbe"/>'s spending method in one instrument, the way
/// <see cref="LdpcLadderProbe"/> combined them for the LDPC family, on the grid's own link: R1/T13
/// narrow at 2500 Hz, a 256-byte payload, a fresh payload per seed. Coarse then fine as the grid
/// does it - 16 seeds on a 2 dB grid walking down to bracket the cliff, then the full seed count
/// at 1 dB across the bracket and on up through the 4 dB above it that the spending slope is
/// fitted over.</para>
/// <para>The neighbours on the ladder are measured in the same run, on the same profile, by the
/// same code. The ladder's own figures were taken on a narrower span than the one measured here,
/// so a number from this probe means something next to the ladder's only through a neighbour
/// measured beside it.</para>
/// <para>A measurement, not a check: <c>OFDMFM_PROBE=1</c>; seeds via <c>OFDMFM_SEEDS</c>
/// (default 64, the grid's fine count); <c>OFDMFM_PROFILE</c> names a profile in a station's own
/// geometry file to measure on instead of the preset, or <c>synthetic</c> for the small built-in
/// one.</para>
/// </remarks>
public class PuncturedRungProbe
{
    /// <summary>The ladder as it stood before the punctured rungs, so the whole of it is on one
    /// instrument on one day, then the candidates and the cells that put them in context.</summary>
    private static readonly (OfdmFmConstellation C, int K, int Num, int Den, string Note)[] Cells =
    [
        (OfdmFmConstellation.Bpsk, 9, 3, 4, "ladder rung 0"),
        (OfdmFmConstellation.Qpsk, 9, 2, 3, "ladder rung 1"),
        (OfdmFmConstellation.Qpsk, 9, 3, 4, "ladder rung 2"),
        (OfdmFmConstellation.Qam16, 9, 1, 2, "ladder rung 3"),
        (OfdmFmConstellation.Qam16, 9, 3, 4, "ladder rung 4"),
        (OfdmFmConstellation.Qam64, 9, 2, 3, "ladder rung 5"),
        (OfdmFmConstellation.Qam256, 9, 2, 3, "ladder rung 6"),
        (OfdmFmConstellation.Qam16, 7, 5, 6, "candidate"),
        (OfdmFmConstellation.Qam64, 9, 3, 4, "dominated on the measured grid"),
        (OfdmFmConstellation.Qam64, 7, 2, 3, "K=7 against K=9 at one rate"),
        (OfdmFmConstellation.Qam64, 7, 3, 4, "candidate, coding id 3"),
        (OfdmFmConstellation.Qam64, 7, 5, 6, "candidate, coding id 8"),
        (OfdmFmConstellation.Qam64, 7, 7, 8, "candidate, coding id 9"),
    ];

    [Fact]
    public void What_A_Punctured_Rung_Would_Carry()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null,
            "set OFDMFM_PROBE=1 for the punctured rung calibration - a campaign, not a check");

        int seeds =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 64;
        const int payloadBytes = 256;
        // The 8 kHz preset's layout unless OFDMFM_PROFILE names something else, and a name a
        // local geometry file does not hold stops the probe rather than quietly measuring
        // another waveform under the name in the heading.
        string? wanted = Environment.GetEnvironmentVariable("OFDMFM_PROFILE");
        OfdmFmParameters profile = wanted == "synthetic"
            ? OfdmFmParameters.Synthetic
            : OfdmFmTestProfiles.Named(wanted, OfdmFmTestProfiles.EightKhz);
        string profileName = string.IsNullOrWhiteSpace(wanted) ? "8 kHz preset" : wanted;
        var link = TaitTm8100.Link(TaitBandwidth.Narrow, 2500);

        Console.WriteLine(
            $"# {profileName}, R1/T13 narrow 2500 Hz, {payloadBytes}-byte payload, "
            + $"{seeds} seeds fine after 16 coarse");
        Console.WriteLine(
            "# recovered frames and the median pre-FEC error rate of the bursts that decoded");

        foreach ((OfdmFmConstellation constellation, int k, int num, int den, string note) in Cells)
        {
            var coded = profile with
            {
                Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, k, num, den, true),
            };
            var codec = new OfdmFmBurstCodec(coded);
            string label = $"{constellation} K{k} {num}/{den}";
            double burstMs = codec.Modulate(new byte[payloadBytes], constellation).Length
                * 1000.0 / coded.SampleRate;
            double goodput = payloadBytes * 8 / (burstMs / 1000);

            // Coarse: the lowest 2 dB rung that still holds half its frames, walking DOWN from a
            // ratio nothing fails at, so a cell whose curve is not monotonic in the tail cannot
            // report a threshold below its real one.
            int bracket = 26;
            for (int cnr = 26; cnr >= 0; cnr -= 2)
            {
                if (Sweep(codec, coded, link, constellation, payloadBytes, cnr, 16).Copied * 2 < 16)
                {
                    break;
                }

                bracket = cnr;
            }

            // Fine: from 3 dB under the bracket, where the grid's fine pass stops, up through the
            // 4 dB above the cliff that the slope is fitted over.
            var cnrs = new List<int>();
            var recovered = new List<int>();
            var spendings = new List<double>();
            for (int cnr = bracket - 3; cnr <= bracket + 6; cnr++)
            {
                (int copied, double spent) =
                    Sweep(codec, coded, link, constellation, payloadBytes, cnr, seeds);
                cnrs.Add(cnr);
                recovered.Add(copied);
                spendings.Add(spent);
            }

            // The cliff: walking down, the first step that drops below half, interpolated across
            // it - the grid probe's rule, so the figure is the same kind of figure.
            double half = seeds / 2.0;
            double cliff = double.NaN;
            for (int i = cnrs.Count - 1; i >= 1; i--)
            {
                if (recovered[i] >= half && recovered[i - 1] < half)
                {
                    cliff = cnrs[i] - ((recovered[i] - half) / (recovered[i] - recovered[i - 1]));
                    break;
                }
            }

            // The spending slope: how fast the median pre-FEC error rate falls per decibel,
            // fitted log-linearly over the points above the cliff with a usable median, the
            // first-4-dB region the ladder's slopes were fitted over.
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

            // The spending at the cliff itself, interpolated the way the margin measure was.
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

            Console.WriteLine();
            Console.WriteLine(
                $"| {label} ({note}) | " + string.Join(" | ", cnrs.Select(c => $"+{c}")) + " |");
            Console.WriteLine(
                "| recovered | " + string.Join(" | ", recovered.Select(r => $"{r}/{seeds}")) + " |");
            Console.WriteLine(
                "| median spent | " + string.Join(" | ", spendings.Select(b => $"{b:0.0000}")) + " |");
            Console.WriteLine(
                $"| summary | burst {burstMs:0} ms | goodput {goodput:0} bit/s | cliff {cliff:+0.0} dB | "
                + $"cliff spending {cliffSpent:0.000} | slope {slope:0.000}/dB |");
        }
    }

    private static (int Copied, double MedianSpent) Sweep(
        OfdmFmBurstCodec codec,
        OfdmFmParameters coded,
        FmLinkProfile link,
        OfdmFmConstellation constellation,
        int payloadBytes,
        int cnrDb,
        int seeds)
    {
        int copied = 0;
        var spent = new List<double>();
        for (int seed = 0; seed < seeds; seed++)
        {
            var payload = new byte[payloadBytes];
            new Random(7000 + seed).NextBytes(payload);
            float[] clean = codec.Modulate(payload, constellation);
            float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, cnrDb);
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
        return (copied, spent.Count > 0 ? spent[spent.Count / 2] : double.NaN);
    }
}
