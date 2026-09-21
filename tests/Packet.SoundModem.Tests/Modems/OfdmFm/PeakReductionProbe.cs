using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

public class PeakReductionProbe
{
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void What_Peak_Reduction_Costs_And_Buys()
    {
        if (Environment.GetEnvironmentVariable("OFDMFM_LADDER") is null)
        {
            return;
        }

        OfdmFmParameters baseline = OfdmFmTestProfiles.EightKhz with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        };
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s)
            ? s
            : 32;

        Console.WriteLine("| limit | crest dB | +14 | +12 | +10 | +8 | +6 | noiseless QAM64 |");

        foreach (double? limit in (ReadOnlySpan<double?>)[null, 9.0, 7.0, 6.0, 5.0])
        {
            OfdmFmParameters p = baseline with { PeakToAverageLimitDb = limit };
            var codec = new OfdmFmBurstCodec(p);

            // measured crest factor of a real burst
            var probe = new byte[64];
            new Random(1).NextBytes(probe);
            float[] burst = codec.Modulate(probe, OfdmFmConstellation.Qpsk);
            double peak = 0;
            double sumSq = 0;
            int from = 2 * p.SymbolSamples;
            for (int n = from; n < burst.Length; n++)
            {
                peak = Math.Max(peak, Math.Abs(burst[n]));
                sumSq += burst[n] * (double)burst[n];
            }

            double crest = 20 * Math.Log10(peak / Math.Sqrt(sumSq / (burst.Length - from)));

            var row = new List<string>();
            foreach (int cnr in (ReadOnlySpan<int>)[14, 12, 10, 8, 6])
            {
                int ok = 0;
                for (int seed = 0; seed < seeds; seed++)
                {
                    var payload = new byte[64];
                    new Random(4000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk);
                    var link = FmLinkProfile.DataPort(LegalPeakDeviationHz, ifBandwidthHz: 7800);
                    float[] heard = new FmChannel(link, p.SampleRate, seed).Apply(clean, cnr);
                    byte[]? got = codec.Demodulate(heard)?.Payload;
                    if (got is not null && got.AsSpan().SequenceEqual(payload))
                    {
                        ok++;
                    }
                }

                row.Add($"{ok,3}");
            }

            // the distortion cost, on every constellation, noiseless
            var lost = new List<string>();
            foreach (OfdmFmConstellation c in Enum.GetValues<OfdmFmConstellation>())
            {
                var dense = new byte[64];
                new Random(9).NextBytes(dense);
                float[] loop = codec.Modulate(dense, c);
                byte[]? back = codec.Demodulate(loop)?.Payload;
                if (back is null || !back.AsSpan().SequenceEqual(dense))
                {
                    lost.Add(c.ToString());
                }
            }

            string clean64 = lost.Count == 0 ? "all ok" : string.Join(",", lost) + " LOST";

            Console.WriteLine(
                $"| {(limit is null ? "none" : $"{limit:0.0} dB"),7} | {crest,8:F1} | "
                + string.Join(" | ", row) + $" | {clean64,15} |");
        }
    }
}
