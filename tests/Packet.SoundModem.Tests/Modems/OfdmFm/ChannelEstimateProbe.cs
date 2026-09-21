using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// Is the equalised constellation noisy because the received symbols are noisy, or because the
/// channel estimate we divide them by is?
/// </summary>
/// <remarks>
/// The two look identical downstream and want completely different fixes. The estimate is taken
/// from ONE preamble symbol, so its noise is multiplicative and lands on every symbol in the
/// burst - and it hits hardest exactly where the estimate is smallest, which is the weakest
/// carriers. Substituting an estimate taken from a clean copy of the same signal separates them.
/// </remarks>
public class ChannelEstimateProbe
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void Which_Half_Of_The_Division_Is_Carrying_The_Noise()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1", "probe");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        Console.WriteLine("| rate | CNR | EVM as-is | EVM with clean estimate | estimate error |");

        foreach (OfdmFmParameters geometry in (ReadOnlySpan<OfdmFmParameters>)
            [profile, profile.Rescaled(profile.SampleRate * 2)!])
        {
            var codec = new OfdmFmBurstCodec(geometry);
            var payload = new byte[64];
            new Random(1000).NextBytes(payload);
            float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);
            var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz);

            float[] quiet = new FmChannel(link, geometry.SampleRate, 0)
                .Apply(clean, double.PositiveInfinity);
            int quietSync = codec.Demodulate(quiet)!.StartSample;
            (double Re, double Im)[] trueChannel = codec.EstimateAt(quiet, quietSync)!;

            // The channel's own magnitude across the occupied band. Equalisation divides by this,
            // so a carrier where it is small multiplies whatever noise is on the estimate there.
            double mean = 0;
            foreach ((double re, double im) in trueChannel)
            {
                mean += (re * re) + (im * im);
            }

            mean = Math.Sqrt(mean / trueChannel.Length);
            var relative = trueChannel
                .Select(h => Math.Sqrt((h.Re * h.Re) + (h.Im * h.Im)) / mean)
                .ToArray();
            Console.WriteLine(
                $"  {geometry.SampleRate} Hz channel magnitude: min {relative.Min(),6:F3} "
                + $"median {relative.Order().ElementAt(relative.Length / 2),6:F3} "
                + $"max {relative.Max(),6:F3} "
                + $"| carriers below a quarter of mean: "
                + $"{relative.Count(r => r < 0.25)} of {relative.Length}");

            int worst = Array.IndexOf(relative, relative.Min());
            double spacing = geometry.SubcarrierSpacingHz;
            Console.WriteLine(
                $"    worst carrier is index {worst} of {relative.Length} "
                + $"({(geometry.FirstCarrier + worst) * spacing:F0} Hz); "
                + $"band {geometry.Occupancy.LowHz:F0}-{geometry.Occupancy.HighHz:F0} Hz; "
                + $"lowest 6 {string.Join(" ", relative.Take(6).Select(r => r.ToString("F2")))}; "
                + $"highest 6 {string.Join(" ", relative.TakeLast(6).Select(r => r.ToString("F2")))}");

            foreach (int cnr in (ReadOnlySpan<int>)[28, 24, 20])
            {
                float[] heard = new FmChannel(link, geometry.SampleRate, 0).Apply(clean, cnr);
                (double Re, double Im)[]? noisyChannel = codec.EstimateAt(heard, quietSync);
                if (noisyChannel is null)
                {
                    continue;
                }

                // How far the estimate itself moved, relative to its own magnitude.
                double num = 0;
                double den = 0;
                for (int c = 0; c < trueChannel.Length; c++)
                {
                    double dr = noisyChannel[c].Re - trueChannel[c].Re;
                    double di = noisyChannel[c].Im - trueChannel[c].Im;
                    num += (dr * dr) + (di * di);
                    den += (trueChannel[c].Re * trueChannel[c].Re)
                        + (trueChannel[c].Im * trueChannel[c].Im);
                }

                double estimateError = Math.Sqrt(num / den);

                Console.WriteLine(
                    $"| {geometry.SampleRate} | +{cnr} | "
                    + $"{Evm(codec, geometry, heard, quietSync, noisyChannel) * 100,6:F2} % | "
                    + $"{Evm(codec, geometry, heard, quietSync, trueChannel) * 100,6:F2} % | "
                    + $"{estimateError * 100,6:F2} % |");
            }
        }
    }

    private static double Evm(
        OfdmFmBurstCodec codec,
        OfdmFmParameters geometry,
        float[] audio,
        int sync,
        (double Re, double Im)[] channel)
    {
        (float I, float Q)[] points = OfdmFmMapper.Points(OfdmFmConstellation.Qpsk);
        bool[] pilots = geometry.PilotMap();
        int perSymbol = geometry.BitsPerDataCarrier(OfdmFmConstellation.Qpsk).Sum();
        int codedBits = new OfdmFmCodec(geometry.Codes).CodedBits((64 + 2) * 8);
        int symbols = ((codedBits + perSymbol) - 1) / perSymbol;

        double total = 0;
        int counted = 0;
        for (int s = 0; s < symbols; s++)
        {
            int offset = sync + codec.HeaderEndOffset + (s * geometry.SymbolSamples);
            if (offset + geometry.SymbolSamples > audio.Length)
            {
                break;
            }

            (double Re, double Im)[]? symbol = codec.EqualisedWith(audio, offset, channel);
            if (symbol is null)
            {
                break;
            }

            for (int c = 0; c < symbol.Length; c++)
            {
                if (pilots[c])
                {
                    continue;
                }

                int nearest = OfdmFmMapper.Demap(points, (float)symbol[c].Re, (float)symbol[c].Im);
                double di = symbol[c].Re - points[nearest].I;
                double dq = symbol[c].Im - points[nearest].Q;
                total += (di * di) + (dq * dq);
                counted++;
            }
        }

        return Math.Sqrt(total / Math.Max(1, counted));
    }
}
