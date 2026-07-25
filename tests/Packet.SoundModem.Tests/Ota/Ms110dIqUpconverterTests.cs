using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ota;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The transmit-side upconverter: MS110D audio → complex baseband for the Flex waveform path.
/// </summary>
/// <remarks>
/// The gate here is deliberately three-part, and only one part is a round trip. Recovering the
/// payload through the receive-side converter proves the pair are consistent, <b>not</b> that
/// either is correct — a sign or scaling error made at both ends cancels perfectly and the
/// bits still arrive. So the spectral assertions below are anchored to analytic expectations
/// instead: an ideal SSB signal has its energy in one band, essentially nothing on the image
/// side, and nothing at the suppressed carrier.
/// </remarks>
public class Ms110dIqUpconverterTests
{
    private const int WaveformRate = 24000;

    private static (float[] I, float[] Q) Split(float[] interleaved)
    {
        int n = interleaved.Length / 2;
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = interleaved[2 * k];
            q[k] = interleaved[(2 * k) + 1];
        }

        return (i, q);
    }

    private static float[] ModulateBurst(int wn, int payloadBits, int seed)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Short,
            ConstraintLength = 7,
            PreambleSuperframes = 3,
        });

        var random = new Random(seed);
        var payload = new byte[payloadBits];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        return tx.Modulate(payload);
    }

    [Fact]
    public void The_occupied_band_lands_where_the_offset_puts_it()
    {
        float[] audio = ModulateBurst(wn: 2, payloadBits: 192, seed: 1);
        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions { OffsetHz = 2000 })
            .Convert(audio);
        (float[] i, float[] q) = Split(iq);

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, WaveformRate, 4096);

        // MS110D occupies roughly 180–3420 Hz about its 1800 Hz sub-carrier, so with a
        // +2000 Hz offset the energy belongs between about 2.2 and 5.4 kHz.
        double inBand = 0;
        double outOfBand = 0;
        for (int k = 0; k < spectrum.Length; k++)
        {
            double f = spectrum.FrequencyOfBin(k);
            if (f is > 2200 and < 5400)
            {
                inBand += spectrum.BinPower(k);
            }
            else if (Math.Abs(f) > 6500)
            {
                outOfBand += spectrum.BinPower(k);
            }
        }

        IqAnalysis.Db(outOfBand / inBand).Should().BeLessThan(-40,
            "essentially all the energy must be inside the intended band");
    }

    [Fact]
    public void The_image_sideband_is_suppressed()
    {
        // The defining property of single sideband, and the one a shared filter kernel could
        // never be trusted to prove: measure the negative-frequency mirror directly.
        float[] audio = ModulateBurst(wn: 2, payloadBits: 192, seed: 2);
        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions { OffsetHz = 2000 })
            .Convert(audio);
        (float[] i, float[] q) = Split(iq);

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, WaveformRate, 4096);

        double wanted = 0, image = 0;
        for (int k = 0; k < spectrum.Length; k++)
        {
            double f = spectrum.FrequencyOfBin(k);
            if (f is > 2200 and < 5400)
            {
                wanted += spectrum.BinPower(k);
            }
            else if (f is < -2200 and > -5400)
            {
                image += spectrum.BinPower(k);
            }
        }

        IqAnalysis.Db(image / wanted).Should().BeLessThan(-45,
            "our own image rejection must be far better than the radio's measured −43.7 dBc, "
            + "or we would be measuring ourselves");
    }

    [Fact]
    public void Nothing_is_emitted_at_the_waveform_centre()
    {
        // The waveform centre is where a real 6500 puts its LO/carrier leakage (−27 dBc
        // measured). We must not add to it, or the two become inseparable.
        float[] audio = ModulateBurst(wn: 2, payloadBits: 192, seed: 3);
        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions { OffsetHz = 2000 })
            .Convert(audio);
        (float[] i, float[] q) = Split(iq);

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, WaveformRate, 4096);
        double centre = spectrum.TonePower(0);
        double signal = spectrum.TonePower(3800); // mid-band

        IqAnalysis.Db(centre / signal).Should().BeLessThan(-40);
    }

    [Fact]
    public void The_output_is_scaled_to_the_requested_peak_without_clipping()
    {
        float[] audio = ModulateBurst(wn: 6, payloadBits: 384, seed: 4);
        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions { Amplitude = 0.9 })
            .Convert(audio);

        ToneGenerator.PeakMagnitude(iq).Should().BeApproximately(0.9, 1e-3);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(2, 2000.0)]
    [InlineData(6, 2000.0)]
    [InlineData(13, -1500.0)]
    public void A_burst_survives_the_round_trip_and_decodes_bit_exact(int wn, double offsetHz)
    {
        // The consistency half of the gate: modulate → upconvert → downconvert → demodulate.
        // Passing this alone would not prove either converter correct, which is why the
        // spectral tests above exist; failing it proves something is wrong.
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Short,
            ConstraintLength = 7,
            PreambleSuperframes = 3,
        };
        var tx = new Ms110dModulator(settings);
        var random = new Random(4242 + wn);
        var payload = new byte[Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Short).InputBits - 32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);

        float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions { OffsetHz = offsetHz })
            .Convert(audio);
        (float[] i24, float[] q24) = Split(iq);

        // The receiver captures at 48 kHz, so model that: linearly interpolate the 24 kHz
        // transmit IQ up to the capture rate. (48000/9600 = 5 is the integer path the
        // receive-side converter needs; 24000/9600 = 2.5 is not.)
        var iD = new double[i24.Length * 2];
        var qD = new double[q24.Length * 2];
        for (int k = 0; k < i24.Length; k++)
        {
            iD[2 * k] = i24[k];
            qD[2 * k] = q24[k];
            iD[(2 * k) + 1] = k + 1 < i24.Length ? 0.5 * (i24[k] + i24[k + 1]) : i24[k];
            qD[(2 * k) + 1] = k + 1 < q24.Length ? 0.5 * (q24[k] + q24[k + 1]) : q24[k];
        }

        float[] recovered = new IqToAudioConverter(new IqToAudioOptions
        {
            InputRate = 2 * WaveformRate,
            OutputRate = 9600,
            DialHz = offsetHz,
        }).Convert(iD, qD);

        var decoded = new List<byte>();
        var demod = new Ms110dDemodulator();
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        // Lead-in noise-free silence, then the burst, then a tail so the last block flushes.
        var stream = new float[recovered.Length + 9600];
        recovered.CopyTo(stream, 0);
        demod.Process(stream);

        decoded.Count.Should().BeGreaterThanOrEqualTo(payload.Length);
        decoded.Take(payload.Length).Should().Equal(payload,
            $"WN{wn} must survive the transmit upconversion and receive downconversion intact");
    }
}
