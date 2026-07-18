using Packet.SoundModem.Channel;
using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Dsp;

public class UpsamplerTests
{
    [Fact]
    public void A_Tone_Survives_With_Images_Rejected()
    {
        var upsampler = new Upsampler(48000, 4);
        var input = new float[12000];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 1700 * i / 12000);
        }

        var output = new float[upsampler.OutputLength(input.Length)];
        upsampler.Process(input, output);

        // Peak lands at 1700 Hz in the 48 kHz spectrum; the first image (10300 Hz) is gone.
        int peakBin = -1;
        byte level10300 = 255;
        var source = new SpectrumSource(48000, line =>
        {
            var copy = line.ToArray();
            peakBin = Array.IndexOf(copy, copy.Max());
            level10300 = copy[(int)(10300 / (48000.0 / 4096))];
        }, fftSize: 4096);
        source.Process(output.AsSpan(8192, 4096));

        (peakBin * 48000.0 / 4096).Should().BeApproximately(1700, 24);
        level10300.Should().BeLessThan(60, "the zero-stuffing image must be filtered out");
    }

    [Fact]
    public void The_Full_Simulated_Card_Path_Roundtrips_A_Frame()
    {
        // Modulate at 12 kHz → upsample ×4 (playback path) → decimate ÷4 (capture path)
        // → demodulate: the whole soundcard loop minus the actual card.
        var frame = new byte[30];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(1).NextBytes(frame.AsSpan(16));

        var tx = new Afsk1200Modem(12000, _ => { });
        float[] at12k = tx.Modulate(frame, txDelayMilliseconds: 200);

        var upsampler = new Upsampler(48000, 4);
        var at48k = new float[upsampler.OutputLength(at12k.Length)];
        upsampler.Process(at12k, at48k);

        var decimator = new Decimator(48000, 4);
        var back = new float[decimator.MaxOutput(at48k.Length)];
        int produced = decimator.Process(at48k, back);

        var frames = new List<byte[]>();
        var rx = new Afsk1200Modem(12000, frames.Add);
        rx.Process([.. back[..produced], .. new float[6000]]);

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }
}
