using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// Is the free noise measurement actually a noise measurement?
/// </summary>
/// <remarks>
/// The sync symbol modulates only even bins, so its odd occupied bins should hold nothing but
/// noise. That is the theory. If those bins also pick up leakage from the loud even ones - through
/// a timing offset, a channel that spreads energy, or anything else that breaks the transform's
/// orthogonality - the estimate is biased high, and an MMSE equaliser handed a noise figure that
/// is too large suppresses carriers it should have kept. Which would look exactly like MMSE not
/// working, and would be the estimator's fault rather than the theory's.
///
/// Checked against the truth, which is available here and nowhere else: the model is
/// deterministic, so a noiseless pass subtracted from a noisy one is the noise it added.
/// </remarks>
public class NoiseEstimateProbe
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void Does_The_Sync_Symbols_Idle_Bins_Measure_Noise_Or_Leakage()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1", "probe");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        Console.WriteLine("| rate | CNR | true noise/bin | sync-bin estimate | ratio |");

        foreach (OfdmFmParameters geometry in (ReadOnlySpan<OfdmFmParameters>)
            [profile, profile.Rescaled(profile.SampleRate * 2)!])
        {
            var codec = new OfdmFmBurstCodec(geometry);
            var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz);
            var payload = new byte[64];
            new Random(1000).NextBytes(payload);
            float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);

            float[] quiet = new FmChannel(link, geometry.SampleRate, 0)
                .Apply(clean, double.PositiveInfinity);
            int sync = codec.Demodulate(quiet)!.StartSample;

            // Noiseless first: whatever the estimator reports HERE is pure leakage, since there is
            // no noise at all for it to be measuring.
            Report(geometry, codec, quiet, quiet, sync, "none");

            foreach (int cnr in (ReadOnlySpan<int>)[28, 20, 14])
            {
                float[] heard = new FmChannel(link, geometry.SampleRate, 0).Apply(clean, cnr);
                Report(geometry, codec, heard, quiet, sync, $"+{cnr}");
            }
        }
    }

    private static void Report(
        OfdmFmParameters geometry,
        OfdmFmBurstCodec codec,
        float[] heard,
        float[] quiet,
        int sync,
        string label)
    {
        int length = Math.Min(heard.Length, quiet.Length);
        var noiseOnly = new double[length];
        for (int n = 0; n < length; n++)
        {
            noiseOnly[n] = heard[n] - quiet[n];
        }

        // The truth: the noise the model added, measured in the payload's own bins, over a symbol
        // aligned exactly as the demodulator aligns one.
        int size = geometry.FftSize;
        int at = sync + (geometry.SymbolSamples * 3) + geometry.CyclicPrefix;
        if (at + size > length)
        {
            return;
        }

        (double[] re, double[] im) = RealFft.ToBins(noiseOnly.AsSpan(at, size), size);
        double truth = 0;
        for (int c = 0; c < geometry.TotalCarriers; c++)
        {
            int bin = geometry.FirstCarrier + c;
            truth += (re[bin] * re[bin]) + (im[bin] * im[bin]);
        }

        truth /= geometry.TotalCarriers;
        double estimate = codec.NoiseVarianceAt(heard, sync);

        Console.WriteLine(
            $"| {geometry.SampleRate} | {label} | {truth:E3} | {estimate:E3} | "
            + $"{(truth > 0 ? estimate / truth : double.PositiveInfinity),8:F2} |");
    }
}
