using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// What this waveform actually delivers, per profile, per rate, per frame size.
/// </summary>
/// <remarks>
/// <para>"How fast is it" has three answers and quoting the wrong one flatters it badly.</para>
/// <list type="bullet">
/// <item><b>Raw</b> is data carriers times bits per carrier per symbol, with no code and no
/// overhead. It is the number a specification sheet prints and nothing ever achieves.</item>
/// <item><b>Coded</b> applies the code rate. Still no overhead, so it is what a burst approaches
/// as the frame grows without limit.</item>
/// <item><b>Delivered</b> is payload bits divided by the whole transmission, sync symbol, preamble,
/// header, lead-in and all. It is what a link does, and at short frames it is roughly half the
/// coded figure, because a burst is a whole number of symbols and the fixed part does not
/// shrink.</item>
/// </list>
/// <para>Nothing here is a channel measurement: it is arithmetic over real modulated bursts, so it
/// says what the waveform carries and not what a link will bear. The carrier-to-noise a rate needs
/// is <see cref="OfdmFmRateLadder"/>'s business, and the ladder's own figures were measured on one
/// span only.</para>
/// </remarks>
public class ThroughputProbe
{
    [Fact]
    public void How_Fast_Is_This_Thing()
    {
        if (Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null)
        {
            return;
        }

        // Every shipped span, named, rather than whatever a local geometry file happened to hold:
        // this table is quoted, so every row has to say which layout it describes.
        IReadOnlyDictionary<string, OfdmFmParameters> profiles =
            new Dictionary<string, OfdmFmParameters>(StringComparer.Ordinal)
            {
                ["narrow"] = OfdmFmTestProfiles.Narrow,
                ["6k"] = OfdmFmTestProfiles.SixKhz,
                ["8k"] = OfdmFmTestProfiles.EightKhz,
            };

        int[] payloads = [64, 256, 1024, 4096];

        Console.WriteLine(
            "# payload bits per second of transmission. raw = no code, no overhead; coded = code "
            + "applied; the rest are whole bursts of that many payload bytes");
        Console.WriteLine(
            "| profile | rate | raw | coded | "
            + string.Join(" | ", payloads.Select(p => $"{p} B")) + " |");

        foreach ((string name, OfdmFmParameters profile) in profiles.OrderBy(p => p.Key))
        {
            // The two ends of the ladder, plus the rung that tops it, so the table shows the
            // trade rather than only the best case.
            foreach ((string label, OfdmFmConstellation constellation, OfdmFmCoding coding) in
                (ReadOnlySpan<(string, OfdmFmConstellation, OfdmFmCoding)>)
                [
                    ("QPSK 2/3", OfdmFmConstellation.Qpsk,
                        new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true)),
                    ("QAM-16 3/4", OfdmFmConstellation.Qam16,
                        new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 3, 4, true)),
                    ("QAM-256 2/3", OfdmFmConstellation.Qam256,
                        new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true)),
                    ("QAM-256 none", OfdmFmConstellation.Qam256, new OfdmFmCoding()),
                ])
            {
                var coded = profile with { Coding = coding };
                var codec = new OfdmFmBurstCodec(coded);
                double symbolSeconds = coded.SymbolSamples / (double)coded.SampleRate;
                double codedBitsPerSymbol =
                    coded.BitsPerDataCarrier(constellation).Sum();
                double rate = coding.Scheme == OfdmFmFec.None
                    ? 1.0
                    : (double)coding.RateNumerator / coding.RateDenominator;

                var delivered = new List<string>();
                foreach (int bytes in payloads)
                {
                    double seconds =
                        codec.Modulate(new byte[bytes], constellation).Length / (double)coded.SampleRate;
                    delivered.Add($"{bytes * 8 / seconds,6:0}");
                }

                Console.WriteLine(
                    $"| {name,-4} | {label,-12} | {codedBitsPerSymbol / symbolSeconds,6:0} | "
                    + $"{codedBitsPerSymbol * rate / symbolSeconds,6:0} | "
                    + string.Join(" | ", delivered) + " |");
            }
        }
    }
}
