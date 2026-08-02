using AwesomeAssertions;
using M0LTE.Dsp;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// End-to-end proof of the SSB demodulator against a real waveform: MS110D modulate (9600 Hz) →
/// synthesise a 48 kHz USB IQ capture (the modem placed at a chosen dial offset, quantised to
/// int16 like a real capture, plus AWGN) → <see cref="SsbDemodulator"/> → the MS110D demodulator
/// must recover the exact payload. This exercises the NCO dial correction, the complex SSB
/// bandpass (which must reject the negative-frequency image the real-into-I synthesis
/// deliberately injects), and the decimation back to 9600 Hz. Noise performance is out of scope
/// here (that is the mask suite); these run at high SNR to isolate the conversion chain.
/// </summary>
/// <remarks>
/// The demodulators themselves live in <c>M0LTE.Dsp</c>, and the tests that pin <em>them</em> —
/// streaming-versus-reference equivalence, block-boundary independence, sideband selection at
/// absolute frequencies, decimation by one — live there with them, because they are properties of
/// the filter and need no modem to state. What stays here is the question only this repo can
/// ask: whether a real burst survives the round trip and still decodes to the bit.
/// </remarks>
public class SsbDemodulatorMs110dLoopbackTests
{
    private const int Fs = 48000;

    private static byte[] RandomBits(int count, int seed)
    {
        var random = new Random(seed);
        var bits = new byte[count];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }
        return bits;
    }

    /// <summary>Anti-imaging upsample 9600 → 48000 (zero-stuff ×5 + low-pass, amplitude preserved).</summary>
    private static float[] Upsample5(float[] x)
    {
        const int factor = 5;
        var lp = new FirFilter(FilterDesign.LowPass(4200, Fs, 8 * factor + 1));
        var y = new float[x.Length * factor];
        for (int i = 0; i < x.Length; i++)
        {
            for (int j = 0; j < factor; j++)
            {
                float s = j == 0 ? x[i] * factor : 0f;
                y[i * factor + j] = lp.Next(s);
            }
        }
        return y;
    }

    /// <summary>Synthesise an int16 stereo IQ capture: real audio in I (so a negative-frequency
    /// image is present for the SSB filter to reject), shifted up by <paramref name="dialHz"/> to
    /// simulate the RX being tuned off our dial, plus complex AWGN at <paramref name="snrDb"/>.</summary>
    private static short[] SynthIq(float[] audio48, double dialHz, double snrDb, int seed)
    {
        int n = audio48.Length;

        float peak = 0f;
        foreach (float v in audio48)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }
        float norm = peak > 1e-9f ? 0.4f / peak : 1f; // leave headroom for noise + int16 scaling

        double sigPow = 0;
        for (int k = 0; k < n; k++)
        {
            double a = audio48[k] * norm;
            sigPow += a * a;
        }
        sigPow /= n;
        double nStd = Math.Sqrt(sigPow / Math.Pow(10, snrDb / 10) / 2); // per complex component

        var rng = new Random(seed);
        double dPhi = 2.0 * Math.PI * dialHz / Fs;
        double wr = Math.Cos(dPhi), wi = Math.Sin(dPhi);
        double pr = 1.0, pi = 0.0;
        var iq = new short[n * 2];
        for (int k = 0; k < n; k++)
        {
            double re = audio48[k] * norm; // signal in I only
            double reS = re * pr;          // (re + j0) * (pr + j pi)
            double imS = re * pi;
            reS += nStd * Gaussian(rng);
            imS += nStd * Gaussian(rng);
            iq[2 * k] = ToShort(reS);
            iq[2 * k + 1] = ToShort(imS);

            double npr = pr * wr - pi * wi;
            pi = pr * wi + pi * wr;
            pr = npr;
        }
        return iq;
    }

    private static short ToShort(double v) => (short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue);

    private static double Gaussian(Random r)
    {
        double u1 = 1.0 - r.NextDouble();
        double u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Theory]
    [InlineData(2, 0.0)]      // BPSK, RX tuned on dial
    [InlineData(2, 1200.0)]   // BPSK, RX tuned 1.2 kHz off — NCO must correct
    [InlineData(6, 0.0)]      // QPSK
    [InlineData(6, -900.0)]   // QPSK, negative dial offset
    [InlineData(0, 750.0)]    // Walsh/BPSK WN0
    public void Converts_Synthetic_Usb_Iq_Back_To_Decodable_Audio(int wn, double dialHz)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
        byte[] payload = RandomBits(400, 4200 + wn);
        float[] audio9600 = tx.Modulate(payload);

        // Pad with silence at each end (like a real burst inside a capture), upsample, synth IQ.
        var padded = new float[9600 / 4 + audio9600.Length + 9600 / 2];
        audio9600.CopyTo(padded, 9600 / 4);
        float[] audio48 = Upsample5(padded);
        short[] iq = SynthIq(audio48, dialHz, snrDb: 35, seed: 99 + wn);

        var converter = new SsbDemodulator(new SsbDemodulatorOptions { DialHz = dialHz });
        float[] recovered = converter.Convert(iq);

        // Decode the recovered audio.
        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(new float[1500]);
        demod.Process(recovered);
        demod.Process(new float[6000]);

        burst.Should().NotBeNull("the converted audio should decode a burst");
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Fact]
    public void Output_Is_At_The_Requested_Rate_And_Length()
    {
        var iq = new short[48000 * 2]; // 1 s of silence IQ
        float[] outp = new SsbDemodulator(new SsbDemodulatorOptions()).Convert(iq);
        outp.Length.Should().Be(9600, "48000 Hz decimated by 5 over 1 s → 9600 samples");
    }

    /// <summary>Runs the streaming converter over a capture in blocks of the given size.</summary>
    private static float[] Stream(short[] interleaved, SsbDemodulatorOptions options, int blockFrames)
    {
        var converter = new StreamingSsbDemodulator(options);
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

        float[] recovered = Stream(iq, new SsbDemodulatorOptions { NormalisePeak = 0f }, blockFrames: 4096);

        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(recovered);
        demod.Process(new float[6000]);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }
}
