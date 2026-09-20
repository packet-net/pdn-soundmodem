using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// Where frames go on a fading link, counted by the stage that lost each one.
/// </summary>
/// <remarks>
/// <para>A single recovered-frame count says a burst was lost but not to what, and the three
/// stages want different fixes: acquisition is a detector problem, the header is a waveform one,
/// and the payload is a coding one. Spending effort on the wrong one is the failure mode this
/// exists to prevent, and it has already earned its keep once - it is what showed that a
/// decision-directed second pass could not have helped, because most lost frames never reached the
/// payload at all.</para>
/// <para>Every burst is counted exactly once. A burst that is never acquired cannot have its
/// header read, and one whose header fails never reaches its payload, so the buckets are ordered
/// and exclusive rather than overlapping.</para>
/// <para>A measurement, not a check, so it runs only under OFDMFM_LADDER=1.</para>
/// </remarks>
public class FlutterBudgetTests
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    private static readonly int Seeds =
        int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 16;

    [Fact]
    public void Where_Frames_Go_On_A_Fading_Link()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 for the flutter failure budget - a measurement, not a check");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        var coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true);
        int repeats = int.TryParse(
            Environment.GetEnvironmentVariable("OFDMFM_HDRREPEATS"), out int r) ? r : 1;
        profile = profile with { Coding = coding, HeaderRepeats = repeats };
        var codec = new OfdmFmBurstCodec(profile);
        Console.WriteLine($"header repeats {repeats}, header symbols {codec.HeaderSymbolCount}");

        Console.WriteLine($"flutter failure budget, rate 1/2, {Seeds} seeds, 8 kHz preset");
        Console.WriteLine("| doppler | cnr | recovered | no sync | bad header | bad payload |");

        foreach (double doppler in (ReadOnlySpan<double>)[0, 2, 5, 10, 20])
        {
            foreach (int cnr in (ReadOnlySpan<int>)[28, 24, 20])
            {
                int recovered = 0;
                int noSync = 0;
                int badHeader = 0;
                int badPayload = 0;

                for (int seed = 0; seed < Seeds; seed++)
                {
                    var payload = new byte[64];
                    new Random(1000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk);

                    var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz) with
                    {
                        FlutterDopplerHz = doppler,
                    };

                    float[] heard = new FmChannel(link, profile.SampleRate, seed).Apply(clean, cnr);

                    int sync = codec.FindSyncIn(heard);
                    if (sync < 0)
                    {
                        noSync++;
                        continue;
                    }

                    if (codec.ReadHeader(heard, sync) is null)
                    {
                        badHeader++;
                        continue;
                    }

                    byte[]? got = codec.DecodeAt(heard, sync)?.Payload;
                    if (got is not null && got.AsSpan().SequenceEqual(payload))
                    {
                        recovered++;
                    }
                    else
                    {
                        badPayload++;
                    }
                }

                Console.WriteLine(
                    $"| {doppler,2} Hz | +{cnr} | {recovered,2}/{Seeds} "
                    + $"| {noSync,2} | {badHeader,2} | {badPayload,2} |");
            }
        }
    }
}
