using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// Every rate a header can name, measured on one axis so that "faster" and "slower" mean something.
/// </summary>
/// <remarks>
/// <para><see cref="CodeRateProbe"/> measures two lines through the constellation-by-coding grid and
/// shows they are not equivalent: loosening the code buys throughput more cheaply than densifying
/// the constellation. That is enough to know a two-dimensional ladder is the wrong shape and not
/// enough to build the one-dimensional one, because the cells off those two lines are unmeasured.
/// This fills the grid.</para>
/// <para>The output is one row per rate: how long a burst takes, what it delivers if every frame
/// lands, and the carrier-to-noise ratio at which half of them do. Sorted by throughput, a rate is
/// <b>dominated</b> if some faster rate also needs less signal, and a dominated rate should never
/// be on the ladder at all - there is no link on which it is the right answer.</para>
/// <para>Coarse then fine, rather than a full sweep at full seeds. A cell whose cliff is at +7 dB
/// learns nothing from 64 seeds at +24, and the saved time is what makes the grid affordable. The
/// coarse pass is 16 seeds on a 2 dB grid to bracket the cliff; the fine pass is 64 seeds on 1 dB
/// steps across the bracket, which is the seed count a claim gets quoted from.</para>
/// <para>Not paired across cells, and must not be read as if it were: different rates mean
/// different burst lengths, and the channel a seed produces depends on the length. Independent
/// samples at 64 seeds, which is why the threshold is quoted to a decibel and not to a tenth.</para>
/// </remarks>
public class RateLadderGridProbe
{
    private static readonly OfdmFmConstellation[] Constellations =
    [
        OfdmFmConstellation.Bpsk,
        OfdmFmConstellation.Qpsk,
        OfdmFmConstellation.Psk8,
        OfdmFmConstellation.Qam16,
        OfdmFmConstellation.Qam32,
        OfdmFmConstellation.Qam64,
        OfdmFmConstellation.Qam128,
        OfdmFmConstellation.Qam256,
    ];

    private static readonly (string Label, int Numerator, int Denominator)[] Rates =
    [
        ("1/2", 1, 2),
        ("2/3", 2, 3),
        ("3/4", 3, 4),

        // Signalled for K=7 only (coding ids 8 and 9); the grid's default K=9 can still be swept
        // at them to see what the stronger mother code would have bought, but no header can name
        // that and the ladder cannot carry it.
        ("5/6", 5, 6),
        ("7/8", 7, 8),
    ];

    [Fact]
    public void What_Does_The_Whole_Rate_Ladder_Look_Like()
    {
        // OFDMFM_PROBE, not OFDMFM_LADDER. These are campaign instruments that sweep a grid at 64
        // seeds a cell and run for several minutes each; the ladder gate is meant to stay something
        // a person will actually wait for.
        if (Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null)
        {
            return;
        }

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        int payloadBytes =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_PAYLOAD"), out int p) ? p : 256;
        int constraint =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_K"), out int k) ? k : 9;
        var link = TaitTm8100.Link(TaitBandwidth.Narrow, 2500);

        // A worker takes a slice of the grid so that the whole thing can be run as several
        // processes of one already-built binary. Splitting the WORK rather than the INSTRUMENT: the
        // alternative, several copies of the harness each rebuilt and re-rigged, is how a
        // measurement campaign ends up comparing numbers that were never comparable.
        var cells = new List<(OfdmFmConstellation C, string Label, int Num, int Den)>();
        foreach (OfdmFmConstellation constellation in Constellations)
        {
            foreach ((string label, int num, int den) in Rates)
            {
                cells.Add((constellation, label, num, den));
            }
        }

        (int From, int Count) slice = ParseSlice(cells.Count);

        Console.WriteLine(
            $"# K={constraint}, {payloadBytes}-byte payload, R1/T13 narrow 2500 Hz, cells "
            + $"{slice.From} to {slice.From + slice.Count - 1} of {cells.Count}");

        for (int i = slice.From; i < slice.From + slice.Count; i++)
        {
            (OfdmFmConstellation constellation, string label, int num, int den) = cells[i];
            var coded = profile with
            {
                Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, constraint, num, den, true),
            };
            var codec = new OfdmFmBurstCodec(coded);
            double burstMs = codec.Modulate(new byte[payloadBytes], constellation).Length
                * 1000.0 / coded.SampleRate;

            // Coarse: the lowest 2 dB rung that still holds half its frames. Walking DOWN from a
            // ratio nothing fails at, so a cell whose curve is not monotonic in the tail cannot
            // report a threshold below its real one.
            int bracket = 26;
            for (int cnr = 26; cnr >= 0; cnr -= 2)
            {
                if (Copied(codec, coded, link, constellation, payloadBytes, cnr, 16) * 2 < 16)
                {
                    break;
                }

                bracket = cnr;
            }

            // Fine: 64 seeds either side of the bracket, and the threshold interpolated between the
            // last rung that holds half and the first that does not.
            double threshold = double.NaN;
            int above = 0;
            for (int cnr = bracket + 2; cnr >= bracket - 3; cnr--)
            {
                int ok = Copied(codec, coded, link, constellation, payloadBytes, cnr, 64);
                if (ok * 2 >= 64)
                {
                    above = ok;
                    threshold = cnr;
                    continue;
                }

                if (!double.IsNaN(threshold))
                {
                    // Linear between the two rungs, which is good to a few tenths over 1 dB and is
                    // not claimed to be better than that.
                    threshold -= (above - 32.0) / Math.Max(above - ok, 1);
                    break;
                }
            }

            double goodput = payloadBytes * 8 / (burstMs / 1000);
            Console.WriteLine(
                $"| {constellation,-8} | {label} | {burstMs,5:0} | {goodput,5:0} | "
                + (double.IsNaN(threshold) ? "  n/a" : $"{threshold,5:0.0}") + " |");
        }
    }

    private static (int From, int Count) ParseSlice(int total)
    {
        string? spec = Environment.GetEnvironmentVariable("OFDMFM_CELLS");
        if (spec is null)
        {
            return (0, total);
        }

        string[] parts = spec.Split(',');
        int from = Math.Clamp(int.Parse(parts[0]), 0, total);
        int count = parts.Length > 1 ? int.Parse(parts[1]) : total - from;
        return (from, Math.Clamp(count, 0, total - from));
    }

    private static int Copied(
        OfdmFmBurstCodec codec,
        OfdmFmParameters coded,
        FmLinkProfile link,
        OfdmFmConstellation constellation,
        int payloadBytes,
        int cnrDb,
        int seeds)
    {
        int ok = 0;
        for (int seed = 0; seed < seeds; seed++)
        {
            var payload = new byte[payloadBytes];
            new Random(7000 + seed).NextBytes(payload);
            float[] clean = codec.Modulate(payload, constellation);
            float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, cnrDb);
            byte[]? got = codec.Demodulate(heard)?.Payload;
            if (got is not null && got.AsSpan().SequenceEqual(payload))
            {
                ok++;
            }
        }

        return ok;
    }
}
