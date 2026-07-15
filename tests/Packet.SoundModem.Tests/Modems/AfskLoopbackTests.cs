using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class AfskLoopbackTests
{
    private const int SampleRate = 12000;

    private static byte[] SampleFrame(int seed, int infoLength)
    {
        var random = new Random(seed);
        var frame = new byte[16 + infoLength];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        random.NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static (AfskDemodulator Demodulator, List<byte[]> Frames) BuildReceiver()
    {
        var frames = new List<byte[]>();
        var deframer = new HdlcDeframer(frames.Add);
        var nrzi = new NrziDecoder();
        var demodulator = new AfskDemodulator(SampleRate, level => deframer.PushBit(nrzi.Decode(level)));
        return (demodulator, frames);
    }

    private static float[] WithPadding(float[] audio, int seed = 0, float noiseSigma = 0f)
    {
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        if (noiseSigma > 0f)
        {
            var random = new Random(seed);
            for (int i = 0; i < padded.Length; i++)
            {
                // Box-Muller Gaussian noise.
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                padded[i] += noiseSigma
                    * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
        }

        return padded;
    }

    [Fact]
    public void A_Clean_Frame_Roundtrips_Through_Audio()
    {
        byte[] frame = SampleFrame(1, 64);
        var modulator = new AfskModulator(SampleRate);
        float[] audio = modulator.Modulate(HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2));

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(audio));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void A_Quiet_Signal_Still_Decodes()
    {
        byte[] frame = SampleFrame(2, 40);
        var modulator = new AfskModulator(SampleRate);
        float[] audio = modulator.Modulate(
            HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2), amplitude: 0.05f);

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(audio));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Theory]
    [InlineData(0.10f, 11)]
    [InlineData(0.20f, 12)]
    public void Frames_Survive_Additive_Gaussian_Noise(float sigma, int seed)
    {
        byte[] frame = SampleFrame(seed, 48);
        var modulator = new AfskModulator(SampleRate);
        float[] audio = modulator.Modulate(HdlcFramer.FrameBits(frame, openingFlags: 40, closingFlags: 2));

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(audio, seed, sigma));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Back_To_Back_Frames_Both_Decode()
    {
        byte[] first = SampleFrame(3, 20);
        byte[] second = SampleFrame(4, 35);
        var modulator = new AfskModulator(SampleRate);
        var bits = new List<byte>();
        bits.AddRange(HdlcFramer.FrameBits(first, openingFlags: 30, closingFlags: 1));
        bits.AddRange(HdlcFramer.FrameBits(second, openingFlags: 1, closingFlags: 2));
        float[] audio = modulator.Modulate([.. bits]);

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(audio));

        frames.Should().HaveCount(2);
        frames[0].Should().Equal(first);
        frames[1].Should().Equal(second);
    }

    [Fact]
    public void Audio_Split_Into_Small_Blocks_Decodes_Identically()
    {
        byte[] frame = SampleFrame(5, 30);
        var modulator = new AfskModulator(SampleRate);
        float[] audio = WithPadding(
            modulator.Modulate(HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2)));

        var (demodulator, frames) = BuildReceiver();
        for (int position = 0; position < audio.Length; position += 512)
        {
            demodulator.Process(audio.AsSpan(position, Math.Min(512, audio.Length - position)));
        }

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }
}
