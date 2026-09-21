using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// Does the FM link model deliver the same in-band signal-to-noise ratio whatever audio rate it
/// runs at, for the same stated carrier-to-noise ratio?
/// </summary>
/// <remarks>
/// It has to, or no ladder run at one audio rate is comparable with one run at another - and the
/// FM mask suite measures modes at both 12 kHz and 48 kHz through this same model.
/// The carrier-to-noise ratio is stated in the receiver IF bandwidth, which is a property of the
/// radio and not of the modem's sample rate, so the discriminator output ought to carry the same
/// noise in the same audio band either way.
///
/// Measured by difference: the same burst through the same link and seed, once noiseless and once
/// at a stated CNR. The model is deterministic, so subtracting gives the noise it added, and the
/// occupied bins give the band that matters.
/// </remarks>
public class FmChannelRateTests
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void The_Same_Carrier_To_Noise_Ratio_Delivers_The_Same_In_Band_Noise_At_Either_Rate()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 - a measurement, not a check");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        Console.WriteLine("| rate | CNR | in-band SNR | full-band SNR |");
        var measured = new Dictionary<int, double>();

        // The host rate, and the same layout on a doubled grid: one waveform at two audio rates,
        // so anything that differs between the rows belongs to the model and not to the waveform.
        foreach (OfdmFmParameters geometry in
            (ReadOnlySpan<OfdmFmParameters>)[profile, profile.Rescaled(profile.SampleRate * 2)!])
        {
            var codec = new OfdmFmBurstCodec(geometry);
            var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz);
            var payload = new byte[64];
            new Random(1000).NextBytes(payload);
            float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);

            foreach (int cnr in (ReadOnlySpan<int>)[28, 20])
            {
                float[] quiet = new FmChannel(link, geometry.SampleRate, 0)
                    .Apply(clean, double.PositiveInfinity);
                float[] heard = new FmChannel(link, geometry.SampleRate, 0).Apply(clean, cnr);
                int length = Math.Min(quiet.Length, heard.Length);

                var noise = new double[length];
                var signal = new double[length];
                double fullSignal = 0;
                double fullNoise = 0;
                for (int n = 0; n < length; n++)
                {
                    signal[n] = quiet[n];
                    noise[n] = heard[n] - quiet[n];
                    fullSignal += signal[n] * signal[n];
                    fullNoise += noise[n] * noise[n];
                }

                (double InBandSignal, double InBandNoise) band = InBand(signal, noise, geometry);
                double inBandDb = 10 * Math.Log10(band.InBandSignal / band.InBandNoise);
                double fullDb = 10 * Math.Log10(fullSignal / fullNoise);
                measured[(geometry.SampleRate * 100) + cnr] = inBandDb;

                Console.WriteLine(
                    $"| {geometry.SampleRate} | +{cnr} | {inBandDb,6:F2} dB | {fullDb,6:F2} dB |");

                // The sum can hide a tilt: a handful of ruined carriers at the top of the band
                // barely move a total dominated by the rest, and a convolutional code spread over
                // the whole burst feels them all the same. So look across the band, not at it.
                if (cnr == 20)
                {
                    Console.WriteLine("    per-carrier SNR across the band: "
                        + string.Join("  ", Tilt(signal, noise, geometry)
                            .Select(d => $"{d,5:F1}")));
                }
            }
        }

        foreach (int cnr in (ReadOnlySpan<int>)[28, 20])
        {
            double low = measured[(profile.SampleRate * 100) + cnr];
            double high = measured[(profile.SampleRate * 2 * 100) + cnr];
            high.Should().BeApproximately(
                low,
                1.5,
                "a carrier-to-noise ratio is stated in the receiver's IF bandwidth, so the audio "
                + "the discriminator hands back must carry the same in-band noise whatever rate "
                + "the modem happens to run at (+{0} dB CNR: {1} Hz gave {2:F2} dB, {3} Hz gave "
                + "{4:F2} dB)",
                cnr,
                profile.SampleRate,
                low,
                profile.SampleRate * 2,
                high);
        }
    }

    /// <summary>Signal and noise power inside the occupied bins only.</summary>
    private static (double Signal, double Noise) InBand(
        double[] signal, double[] noise, OfdmFmParameters geometry)
    {
        int size = geometry.FftSize;
        int at = signal.Length / 2;
        at -= at % size;

        (double[] sRe, double[] sIm) = RealFft.ToBins(signal.AsSpan(at, size), size);
        (double[] nRe, double[] nIm) = RealFft.ToBins(noise.AsSpan(at, size), size);

        double s = 0;
        double n = 0;
        for (int c = 0; c < geometry.TotalCarriers; c++)
        {
            int bin = geometry.FirstCarrier + c;
            s += (sRe[bin] * sRe[bin]) + (sIm[bin] * sIm[bin]);
            n += (nRe[bin] * nRe[bin]) + (nIm[bin] * nIm[bin]);
        }

        return (s, n);
    }

    /// <summary>Per-carrier SNR in dB, averaged into five groups from the bottom of the occupied
    /// band to the top.</summary>
    private static double[] Tilt(double[] signal, double[] noise, OfdmFmParameters geometry)
    {
        int size = geometry.FftSize;
        int at = signal.Length / 2;
        at -= at % size;
        (double[] sRe, double[] sIm) = RealFft.ToBins(signal.AsSpan(at, size), size);
        (double[] nRe, double[] nIm) = RealFft.ToBins(noise.AsSpan(at, size), size);

        const int Groups = 5;
        var result = new double[Groups];
        int per = geometry.TotalCarriers / Groups;
        for (int g = 0; g < Groups; g++)
        {
            double s = 0;
            double n = 0;
            for (int c = g * per; c < (g + 1) * per; c++)
            {
                int bin = geometry.FirstCarrier + c;
                s += (sRe[bin] * sRe[bin]) + (sIm[bin] * sIm[bin]);
                n += (nRe[bin] * nRe[bin]) + (nIm[bin] * nIm[bin]);
            }

            result[g] = 10 * Math.Log10(s / n);
        }

        return result;
    }
}
