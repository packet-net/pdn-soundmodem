using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// Is a higher host sample rate good for us, bad for us, or neither?
/// </summary>
/// <remarks>
/// It matters practically: the CM108 sound chips in every cheap USB radio interface run natively
/// at 48 kHz, so capturing there means no resampling anywhere in the path - no driver resampler,
/// no group delay, no filter of somebody else's design between the radio and the modem. The
/// question is whether the modem pays for it. It does not, so 48 kHz is the only rate this modem
/// offers; the sweep stays because that is a claim worth being able to re-check, not because
/// anything else is a candidate.
///
/// <b>The link model has a thumb on this scale, so read the numbers below with it in mind.</b> Its
/// band-limit filter uses a FIXED 127 taps and its IF filter a fixed 129, so their transition
/// widths in Hz double every time the sample rate does - which is not how a radio behaves. A
/// radio's audio filters are analogue and do not get sloppier because a sound card samples faster.
///
/// Measured both ways on 2026-08-09, frames of 8, conv 1/2 QPSK, mic and speaker, +28 down to +12:
///
///   as the model is written    24k: 8 8 8 8 2   48k: 8 8 8 8 3   96k: 8 8 8 6 0
///   same filter in Hz at each  24k: 8 8 8 8 2   48k: 8 8 8 8 1   96k: 8 8 8 8 1
///
/// So the rate is NEUTRAL: once every rate sees the same filter, the modem performs identically at
/// 24, 48 and 96 kHz, and the +12 column is 1 or 2 of 8 either way, which at eight seeds is noise.
/// The 96 kHz collapse in the first row is the instrument, not the modem.
///
/// That fixed tap count is a defect in a model pdn-soundmodem's whole FM mask suite shares across
/// modes at 12 kHz and 48 kHz, so it currently biases every cross-rate comparison there. Fixing it
/// moves mask numbers and wants its own leg; it is not this repository's to change unilaterally,
/// which is why the experiment above was run and reverted rather than kept.
/// </remarks>
public class HostRateTests
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    private static readonly int[] CnrDb = [28, 24, 20, 16, 14, 12, 10];
    private const int Seeds = 16;

    [Fact]
    public void What_Does_A_Higher_Host_Rate_Cost_Or_Buy()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 - a measurement, not a check");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        Console.WriteLine("frames of 8, genie timing, conv 1/2 QPSK, mic and speaker");
        Console.WriteLine(
            "| rate | " + string.Join(" | ", CnrDb.Select(c => $"+{c}")) + " |");

        // The host rate and twice it. The preset is written at the rate a channel runs it at, so
        // the first row is what a station actually hears and the second is the question.
        foreach (int multiple in (ReadOnlySpan<int>)[1, 2])
        {
            OfdmFmParameters? geometry = profile.Rescaled(profile.SampleRate * multiple);
            if (geometry is null)
            {
                continue;
            }

            var coded = geometry with
            {
                Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
            };
            var codec = new OfdmFmBurstCodec(coded);
            var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz);
            var row = new int[CnrDb.Length];

            for (int c = 0; c < CnrDb.Length; c++)
            {
                for (int seed = 0; seed < Seeds; seed++)
                {
                    var payload = new byte[64];
                    new Random(1000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);
                    float[] heard = new FmChannel(link, coded.SampleRate, seed)
                        .Apply(clean, CnrDb[c]);

                    byte[]? got = codec.Demodulate(heard)?.Payload;
                    if (got is not null && got.AsSpan().SequenceEqual(payload))
                    {
                        row[c]++;
                    }
                }
            }

            Console.WriteLine(
                $"| {coded.SampleRate} | " + string.Join(" | ", row) + " |");
        }
    }
}
