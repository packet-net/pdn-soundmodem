using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// How often the burst header survives, measured on its own.
/// </summary>
/// <remarks>
/// <para>The header is the part of the burst that has to be read before anything else can be, and
/// the flutter failure budget in <c>docs/dev/ofdm-fm/receiver-findings.md</c> puts it at the
/// largest single cause of lost frames. Measuring it through a whole decode cannot separate it
/// from acquisition or from the payload, so this measures it directly: hand the receiver the sync
/// position the transmitter actually used, read the header at it, and count.</para>
/// <para>The sync position is given rather than searched deliberately. A header ladder that also
/// measured acquisition would move whenever acquisition moved, and the whole point is to be able
/// to change the header and see only the header change. The end-to-end number is the ladder in
/// <see cref="OfdmFmFmLadderTests"/>; this is the instrument for the header alone.</para>
/// <para>A measurement, not a check, so it runs only under OFDMFM_LADDER=1 like its siblings.</para>
/// </remarks>
public class HeaderLadderTests
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    private const int LeadInSymbols = 2;
    private const int Seeds = 64;

    [Fact]
    public void The_Header_Ladder_Through_The_Fm_Link()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 for the header ladder - a measurement, not a check");

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        int repeats = int.TryParse(
            Environment.GetEnvironmentVariable("OFDMFM_HDRREPEATS"), out int r) ? r : 1;
        profile = profile with { HeaderRepeats = repeats };
        var codec = new OfdmFmBurstCodec(profile);
        Console.WriteLine($"header repeats {repeats}, header symbols {codec.HeaderSymbolCount}");

        Console.WriteLine($"header ladder, {Seeds} seeds, 8 kHz preset, {profile.SampleRate} Hz");
        Console.WriteLine("headers read correctly, of the bursts that were acquired");

        foreach (double doppler in (ReadOnlySpan<double>)[0, 5, 10, 20])
        {
            var row = new List<string>();
            foreach (int cnr in (ReadOnlySpan<int>)[28, 24, 20, 16, 12])
            {
                int ok = 0;
                int acquired = 0;
                for (int seed = 0; seed < Seeds; seed++)
                {
                    var payload = new byte[64];
                    new Random(2000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(
                        payload, OfdmFmConstellation.Qpsk, LeadInSymbols);

                    var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz) with
                    {
                        FlutterDopplerHz = doppler,
                    };

                    float[] heard = new FmChannel(link, profile.SampleRate, seed).Apply(clean, cnr);

                    // Acquisition first, and counted separately. The channel's filters delay the
                    // burst by their own group delay, so the transmitter's offset is not where the
                    // burst lands, and a header measurement that assumed it would measure nothing
                    // but that delay.
                    int sync = codec.FindSyncIn(heard);
                    if (sync < 0)
                    {
                        continue;
                    }

                    acquired++;

                    // The header carries the constellation and the payload length, so "read
                    // correctly" means both came back as sent, not merely that a CRC passed.
                    OfdmFmHeader? header = codec.ReadHeader(heard, sync);
                    if (header is { } h
                        && h.Constellation == OfdmFmConstellation.Qpsk
                        && h.PayloadLength == payload.Length)
                    {
                        ok++;
                    }
                }

                row.Add($"{ok,3}/{acquired,-3}");
            }

            Console.WriteLine($"| doppler {doppler,2} Hz | {string.Join(" | ", row)} |");
        }

        Console.WriteLine("| columns | +28 | +24 | +20 | +16 | +12 | dB CNR");
    }
}
