using M0LTE.Radio.Audio;
using M0LTE.Dsp;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Modems;
using M0LTE.Ofdm;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// The multi-channel RX front end: one wideband complex-IQ stream (as a Flex DAX-IQ slice would
/// deliver — docs/flex-integration.md §9.1) fanned into several narrowband channels by
/// per-channel <see cref="DigitalDownconverter"/>s, each surfaced as an <c>IAudioInput</c> so the
/// existing demodulators attach unchanged. Everything here runs on synthetic in-memory IQ — no
/// hardware, no network.
/// </summary>
public sealed class MultiChannelReceiverTests
{
    [Fact]
    public void Downconverter_recovers_its_own_tone_and_rejects_the_neighbours()
    {
        const int iqRate = 96000;
        const int decimation = 8; // -> 12 kHz DSP
        const int taps = 256;
        const float amp = 0.5f;
        const int samples = 24000;
        double[] offsets = [20000, 0, -30000];

        for (int i = 0; i < offsets.Length; i++)
        {
            // Selection: tune to a channel carrying only its own tone -> recovered at full amplitude.
            float[] own = ComplexTone(iqRate, offsets[i], amp, samples);
            (float dc, float rms) = MeasureBaseband(
                new DigitalDownconverter(iqRate, offsets[i], decimation, taps), own);
            dc.Should().BeApproximately(amp, amp * 0.05f);
            rms.Should().BeApproximately(amp, amp * 0.05f);

            // Rejection: the same tuning must strongly suppress a tone in any other channel.
            for (int j = 0; j < offsets.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                float[] other = ComplexTone(iqRate, offsets[j], amp, samples);
                (_, float leakRms) = MeasureBaseband(
                    new DigitalDownconverter(iqRate, offsets[i], decimation, taps), other);
                leakRms.Should().BeLessThan(amp * 0.02f); // > ~34 dB isolation
            }
        }
    }

    [Fact]
    public void Two_afsk1200_frames_decode_concurrently_from_one_iq_stream()
    {
        const int dspRate = 12000;
        const int iqRate = 48000;
        const int decimation = 4;
        const double offsetA = 6000;
        const double offsetB = -6000;

        byte[] frameA = SampleFrame(20, seed: 11);
        byte[] frameB = SampleFrame(26, seed: 29);

        // Modulate each frame to real audio at the DSP rate (1700 Hz AFSK centre).
        float[] audioA = new Afsk1200Modem(dspRate, _ => { }).Modulate(frameA, txDelayMilliseconds: 250);
        float[] audioB = new Afsk1200Modem(dspRate, _ => { }).Modulate(frameB, txDelayMilliseconds: 250);

        // Common DSP-rate length with a trailing tail, upsampled to the IQ rate.
        int dspLen = Math.Max(audioA.Length, audioB.Length) + (dspRate / 2);
        float[] up48A = Upsample(audioA, dspLen, iqRate, decimation);
        float[] up48B = Upsample(audioB, dspLen, iqRate, decimation);

        // Place each real channel at its RF offset in one complex-IQ stream.
        var iq = new float[up48A.Length * 2];
        Embed(iq, up48A, offsetA, iqRate);
        Embed(iq, up48B, offsetB, iqRate);

        // The unit under test: one IQ source, two channels, each an IAudioInput.
        var source = new BufferIqSource(iq, iqRate);
        var receiver = new MultiChannelReceiver(
            source,
            [
                new MultiChannelReceiver.ChannelSpec(offsetA, decimation),
                new MultiChannelReceiver.ChannelSpec(offsetB, decimation),
            ]);
        receiver.PumpToEnd();

        var decodedA = new List<byte[]>();
        var decodedB = new List<byte[]>();
        DrainThroughDemod(receiver.Channels[0], new Afsk1200Modem(dspRate, decodedA.Add), dspRate);
        DrainThroughDemod(receiver.Channels[1], new Afsk1200Modem(dspRate, decodedB.Add), dspRate);

        decodedA.Should().ContainSingle();
        decodedA[0].Should().Equal(frameA);
        decodedB.Should().ContainSingle();
        decodedB[0].Should().Equal(frameB);
    }

    // ---- helpers ----

    private static float[] ComplexTone(int rate, double offsetHz, float amp, int samples)
    {
        var iq = new float[samples * 2];
        double w = 2.0 * Math.PI * offsetHz / rate;
        for (int k = 0; k < samples; k++)
        {
            iq[2 * k] = amp * (float)Math.Cos(w * k);
            iq[(2 * k) + 1] = amp * (float)Math.Sin(w * k);
        }

        return iq;
    }

    /// <summary>Runs the DDC and returns (DC-component magnitude, per-sample RMS magnitude) over
    /// the settled region — DC ≈ amplitude when a tone lands at baseband, RMS ≈ residual level
    /// otherwise.</summary>
    private static (float Dc, float Rms) MeasureBaseband(DigitalDownconverter ddc, float[] iq)
    {
        var outp = new Cf[ddc.MaxOutput(iq.Length)];
        int n = ddc.Process(iq, outp);
        int skip = Math.Min(200, n / 4); // let the FIR fill
        double sumRe = 0, sumIm = 0, sumSq = 0;
        int count = 0;
        for (int i = skip; i < n; i++)
        {
            sumRe += outp[i].Re;
            sumIm += outp[i].Im;
            sumSq += outp[i].Cnorm();
            count++;
        }

        if (count == 0)
        {
            return (0, 0);
        }

        double dc = Math.Sqrt(((sumRe / count) * (sumRe / count)) + ((sumIm / count) * (sumIm / count)));
        double rms = Math.Sqrt(sumSq / count);
        return ((float)dc, (float)rms);
    }

    private static float[] Upsample(float[] dspAudio, int dspLen, int iqRate, int factor)
    {
        var padded = new float[dspLen];
        Array.Copy(dspAudio, padded, Math.Min(dspAudio.Length, dspLen));
        var up = new Upsampler(iqRate, factor);
        var outp = new float[up.OutputLength(dspLen)];
        up.Process(padded, outp);
        return outp;
    }

    private static void Embed(float[] iq, float[] realAudio, double offsetHz, int iqRate)
    {
        double w = 2.0 * Math.PI * offsetHz / iqRate;
        for (int k = 0; k < realAudio.Length; k++)
        {
            float r = realAudio[k];
            iq[2 * k] += r * (float)Math.Cos(w * k);
            iq[(2 * k) + 1] += r * (float)Math.Sin(w * k);
        }
    }

    private static void DrainThroughDemod(IAudioInput input, IModem demod, int dspRate)
    {
        var buffer = new float[4096];
        int got;
        while ((got = input.Read(buffer)) > 0)
        {
            demod.Process(buffer.AsSpan(0, got));
        }

        demod.Process(new float[dspRate / 2]); // trailing silence closes the final frame
    }

    private static byte[] SampleFrame(int length, int seed)
    {
        var frame = new byte[length];
        byte[] header =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0,
        ];
        Array.Copy(header, frame, Math.Min(header.Length, length));
        if (length > header.Length)
        {
            new Random(seed).NextBytes(frame.AsSpan(header.Length));
        }

        return frame;
    }
}
