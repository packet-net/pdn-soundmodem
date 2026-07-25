using AwesomeAssertions;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// The streaming IQ→audio converter: equivalence with the whole-file reference, block-boundary
/// independence, and the absolute-frequency check neither converter previously had.
/// </summary>
public class StreamingIqToAudioConverterTests
{
    private const int Fs = 48000;

    /// <summary>A complex exponential at an exact baseband frequency, as int16 stereo IQ.</summary>
    private static short[] ComplexTone(double hz, int samples, double amplitude = 0.5)
    {
        var iq = new short[samples * 2];
        for (int k = 0; k < samples; k++)
        {
            double ph = 2.0 * Math.PI * hz * k / Fs;
            iq[2 * k] = (short)Math.Round(amplitude * Math.Cos(ph) * 32767.0);
            iq[2 * k + 1] = (short)Math.Round(amplitude * Math.Sin(ph) * 32767.0);
        }

        return iq;
    }

    private static short[] NoisyIq(int samples, int seed)
    {
        var rng = new Random(seed);
        var iq = new short[samples * 2];
        for (int k = 0; k < iq.Length; k++)
        {
            iq[k] = (short)rng.Next(-12000, 12000);
        }

        return iq;
    }

    /// <summary>Runs the streaming converter over a capture in blocks of the given size.</summary>
    private static float[] Stream(short[] interleaved, IqToAudioOptions options, int blockFrames)
    {
        var converter = new StreamingIqToAudioConverter(options);
        var output = new List<float>();
        var buffer = new float[converter.MaxOutputFor(blockFrames) + converter.MaxFlushOutput];

        int frames = interleaved.Length / 2;
        for (int start = 0; start < frames; start += blockFrames)
        {
            int n = Math.Min(blockFrames, frames - start);
            int wrote = converter.Process(interleaved.AsSpan(start * 2, n * 2), buffer);
            output.AddRange(buffer.AsSpan(0, wrote));
        }

        output.AddRange(buffer.AsSpan(0, converter.Flush(buffer)));
        return [.. output];
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += v * (double)v;
        }

        return Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    /// <summary>Amplitude of the component at <paramref name="hz"/> in real audio, by direct
    /// correlation — no FFT, no windowing convention to get wrong.</summary>
    private static double ToneAmplitude(ReadOnlySpan<float> audio, double hz, int rate, int skip)
    {
        double re = 0, im = 0;
        int n = audio.Length - skip;
        for (int k = 0; k < n; k++)
        {
            double ph = 2.0 * Math.PI * hz * k / rate;
            re += audio[skip + k] * Math.Cos(ph);
            im += audio[skip + k] * Math.Sin(ph);
        }

        return 2.0 * Math.Sqrt((re * re) + (im * im)) / n;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1200.0)]
    [InlineData(-900.0)]
    public void It_matches_the_whole_file_reference_sample_for_sample(double dialHz)
    {
        // The reference is the readable statement of what the conversion is; this one is the
        // fast, constant-memory rearrangement of the same filter. If they ever disagree, the
        // rearrangement is wrong — so the test is equivalence, not plausibility.
        short[] iq = NoisyIq(48000 + 137, seed: 3);
        var options = new IqToAudioOptions { DialHz = dialHz, NormalisePeak = 0f };

        float[] reference = new IqToAudioConverter(options).Convert(iq);
        float[] streamed = Stream(iq, options, blockFrames: 4096);

        streamed.Length.Should().Be(reference.Length);

        var error = new float[reference.Length];
        for (int k = 0; k < reference.Length; k++)
        {
            error[k] = streamed[k] - reference[k];
        }

        // Where the error lives is the diagnosis, so report it: a difference confined to the
        // first few samples is a filter-startup convention, one spread through the file is a
        // wrong kernel, and one at the end is the flush.
        int first = -1, last = -1;
        for (int k = 0; k < error.Length; k++)
        {
            if (Math.Abs(error[k]) > 1e-5)
            {
                first = first < 0 ? k : first;
                last = k;
            }
        }

        Rms(error).Should().BeLessThan(1e-6,
            "the streaming converter is the same linear filter evaluated at output instants, so "
            + "only double-versus-float rounding may separate them (of {0} samples, the first "
            + "differing is {1} and the last {2}; signal RMS {3:F4})",
            error.Length, first, last, Rms(reference));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]        // not a multiple of the decimation factor
    [InlineData(5)]
    [InlineData(997)]
    [InlineData(1 << 15)]
    public void The_result_does_not_depend_on_how_the_capture_is_blocked(int blockFrames)
    {
        // A capture arrives in whatever chunks the reader hands out, and a burst will straddle
        // them. Any filter state that reset at a block boundary would show up here.
        short[] iq = NoisyIq(20011, seed: 11);
        var options = new IqToAudioOptions { DialHz = 300, NormalisePeak = 0f };

        float[] whole = Stream(iq, options, blockFrames: iq.Length / 2);
        float[] blocked = Stream(iq, options, blockFrames);

        blocked.Should().Equal(whole);
    }

    /// <summary>
    /// The check that neither converter had: that "upper sideband" means the upper sideband of
    /// the real spectrum, not whichever one the test synthesised.
    /// </summary>
    /// <remarks>
    /// Both IQ converters in this repo carried the same reversed-kernel sideband inversion, and
    /// it survived because every test synthesised its input with the same convention it decoded
    /// with — the two errors cancelled and payloads came back bit-exact. The input here is a bare
    /// complex exponential at an exact frequency, which has no convention to share: at +2000 Hz
    /// it is inside the SSB passband and must appear in the audio at 2000 Hz; at −2000 Hz it is
    /// the image the bandpass exists to reject and must not appear at all.
    /// </remarks>
    [Theory]
    [InlineData(typeof(IqToAudioConverter))]
    [InlineData(typeof(StreamingIqToAudioConverter))]
    public void It_keeps_the_upper_sideband_and_rejects_the_lower(Type converter)
    {
        var options = new IqToAudioOptions { NormalisePeak = 0f };
        const double tone = 2000.0;
        const double amplitude = 0.5;
        int samples = Fs * 2;

        float[] upper = Convert(converter, ComplexTone(+tone, samples, amplitude), options);
        float[] lower = Convert(converter, ComplexTone(-tone, samples, amplitude), options);

        // Settle past the filter's transient before measuring.
        int skip = 9600 / 4;
        ToneAmplitude(upper, tone, options.OutputRate, skip).Should().BeApproximately(amplitude, 0.02,
            "a +2000 Hz baseband component is inside the 150–3450 Hz USB passband and must come "
            + "through at 2000 Hz in the audio");

        Rms(lower.AsSpan(skip)).Should().BeLessThan(amplitude / 100.0,
            "a −2000 Hz baseband component is the sideband the complex bandpass exists to "
            + "reject; if it survives, the filter is selecting the wrong sideband");
    }

    private static float[] Convert(Type converter, short[] iq, IqToAudioOptions options)
    {
        if (converter == typeof(IqToAudioConverter))
        {
            return new IqToAudioConverter(options).Convert(iq);
        }

        return Stream(iq, options, blockFrames: 8192);
    }

    [Fact]
    public void A_real_burst_still_decodes_through_the_streaming_path()
    {
        // End-to-end insurance: the equivalence test above compares numbers, this one compares
        // the only thing that matters downstream.
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6 });
        var random = new Random(808);
        var payload = new byte[400];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio9600 = tx.Modulate(payload);
        var padded = new float[2400 + audio9600.Length + 4800];
        audio9600.CopyTo(padded, 2400);

        // Upsample ×5 into I only (so the negative-frequency image is present to be rejected),
        // then quantise as a capture would.
        var iq = new short[padded.Length * 5 * 2];
        var lp = new M0LTE.Dsp.FirFilter(M0LTE.Dsp.FilterDesign.LowPass(4200, Fs, 41));
        for (int i = 0; i < padded.Length; i++)
        {
            for (int j = 0; j < 5; j++)
            {
                float s = lp.Next(j == 0 ? padded[i] * 5f : 0f);
                iq[((i * 5) + j) * 2] = (short)Math.Clamp(Math.Round(s * 0.4 * 32767.0), short.MinValue, short.MaxValue);
            }
        }

        float[] recovered = Stream(iq, new IqToAudioOptions { NormalisePeak = 0f }, blockFrames: 4096);

        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(recovered);
        demod.Process(new float[6000]);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Fact]
    public void Converting_an_hours_capture_does_not_grow_with_its_length()
    {
        // The whole point of this class. An hour of iq48 is 172.8 M frames; the reference
        // converter would ask for several 1.4 GB double[] and be killed. Here the steady-state
        // allocation is asserted directly: after warm-up, further blocks must allocate nothing,
        // so the cost of an hour is the cost of a second.
        var converter = new StreamingIqToAudioConverter(new IqToAudioOptions { NormalisePeak = 0f });
        const int blockFrames = 1 << 16;
        var input = NoisyIq(blockFrames, seed: 5);
        var output = new float[converter.MaxOutputFor(blockFrames) + converter.MaxFlushOutput];

        converter.Process(input, output); // warm up: JIT, first-touch
        long before = GC.GetAllocatedBytesForCurrentThread();
        int produced = 0;
        for (int block = 0; block < 16; block++)
        {
            produced += converter.Process(input, output);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        produced.Should().BeGreaterThan(0);
        allocated.Should().BeLessThan(4096,
            "the converter must be allocation-free per block, or an hour-long capture will not "
            + "fit however patient we are");
        converter.OutputCount.Should().BeGreaterThan(17L * blockFrames / 5 - 100);
    }

    [Fact]
    public void Input_after_a_flush_is_refused_rather_than_silently_mangled()
    {
        var converter = new StreamingIqToAudioConverter(new IqToAudioOptions());
        var output = new float[converter.MaxOutputFor(1000) + converter.MaxFlushOutput];
        converter.Process(NoisyIq(1000, seed: 1), output);
        converter.Flush(output);

        Action more = () => converter.Process(NoisyIq(1000, seed: 2), output);

        more.Should().Throw<InvalidOperationException>();
    }
}
