using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class Fsk9600LoopbackTests
{
    private const int SampleRate = 48000;

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
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                padded[i] += noiseSigma
                    * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
        }

        return padded;
    }

    private static byte[] SampleFrame(int seed, int infoLength)
    {
        var frame = new byte[16 + infoLength];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(seed).NextBytes(frame.AsSpan(16));
        return frame;
    }

    [Theory]
    [InlineData(FskFraming.ClassicHdlc)]
    [InlineData(FskFraming.Il2pCrc)]
    [InlineData(FskFraming.Il2p)]
    public void A_Clean_Frame_Roundtrips(FskFraming framing)
    {
        byte[] frame = SampleFrame(1, 100);
        var frames = new List<byte[]>();
        var tx = new FskModem(SampleRate, _ => { }, framing);
        var rx = new FskModem(SampleRate, frames.Add, framing);

        rx.Process(WithPadding(tx.Modulate(frame, txDelayMilliseconds: 50)));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Theory]
    [InlineData(FskFraming.ClassicHdlc, 0.08f, 41)]
    [InlineData(FskFraming.Il2pCrc, 0.10f, 42)]
    public void Frames_Survive_Additive_Gaussian_Noise(FskFraming framing, float sigma, int seed)
    {
        byte[] frame = SampleFrame(seed, 60);
        var frames = new List<byte[]>();
        var tx = new FskModem(SampleRate, _ => { }, framing);
        var rx = new FskModem(SampleRate, frames.Add, framing);

        rx.Process(WithPadding(tx.Modulate(frame, txDelayMilliseconds: 60), seed, sigma));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Back_To_Back_Il2p_Frames_Both_Decode()
    {
        byte[] first = SampleFrame(2, 30);
        byte[] second = SampleFrame(3, 45);
        var frames = new List<byte[]>();
        var tx = new FskModem(SampleRate, _ => { }, FskFraming.Il2pCrc);
        var rx = new FskModem(SampleRate, frames.Add, FskFraming.Il2pCrc);

        float[] a = tx.Modulate(first, txDelayMilliseconds: 50);
        float[] b = tx.Modulate(second, txDelayMilliseconds: 10);
        rx.Process(WithPadding([.. a, .. b]));

        frames.Should().HaveCount(2);
        frames[0].Should().Equal(first);
        frames[1].Should().Equal(second);
    }

    [Fact]
    public void The_Scrambler_Self_Synchronises_Mid_Stream()
    {
        var tx = new G3ruhScrambler();
        var rx = new G3ruhScrambler();
        var random = new Random(4);

        // Desynchronise: RX misses the first 100 bits entirely.
        var bits = Enumerable.Range(0, 500).Select(_ => random.Next(2)).ToArray();
        var scrambled = bits.Select(tx.Scramble).ToArray();
        var decoded = scrambled.Skip(100).Select(rx.Descramble).ToArray();

        // After 17 bits of history the descrambler output must track the input.
        decoded.Skip(17).Should().Equal(bits.Skip(117), "self-sync recovers after the register fills");
    }
}
