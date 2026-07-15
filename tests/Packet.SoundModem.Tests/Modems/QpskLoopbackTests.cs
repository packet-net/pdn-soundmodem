using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class QpskLoopbackTests
{
    private const int SampleRate = 12000;

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

    private static (QpskModem Tx, QpskModem Rx, List<byte[]> Frames) MakePair(int bitRate)
    {
        var frames = new List<byte[]>();
        QpskModem tx = bitRate == 2400
            ? QpskModem.Qpsk2400(SampleRate, _ => { })
            : QpskModem.Qpsk3600(SampleRate, _ => { });
        QpskModem rx = bitRate == 2400
            ? QpskModem.Qpsk2400(SampleRate, frames.Add)
            : QpskModem.Qpsk3600(SampleRate, frames.Add);
        return (tx, rx, frames);
    }

    [Theory]
    [InlineData(2400)]
    [InlineData(3600)]
    public void A_Clean_Frame_Roundtrips(int bitRate)
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        var (tx, rx, frames) = MakePair(bitRate);

        rx.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    [Theory]
    [InlineData(2400, 0.10f, 31)]
    [InlineData(3600, 0.08f, 32)]
    public void Frames_Survive_Additive_Gaussian_Noise(int bitRate, float sigma, int seed)
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A946F81");
        var (tx, rx, frames) = MakePair(bitRate);

        rx.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 250), seed, sigma));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    [Fact]
    public void A_Small_Carrier_Offset_Is_Tolerated_At_2400()
    {
        byte[] ax25 = Convert.FromHexString("86A24040404060969668908A94FF03F0");
        var modulator = new QpskModulator(SampleRate, 1200, carrierFrequency: 1504);
        byte[] wire = Packet.SoundModem.Il2p.Il2pCodec.Encode(ax25, appendCrc: true);
        byte[] bits = Packet.SoundModem.Il2p.Il2pFramer.FrameBits(
            wire, 240, Packet.SoundModem.Il2p.Il2pFramer.PreambleStyle.Zeros);

        var frames = new List<byte[]>();
        QpskModem rx = QpskModem.Qpsk2400(SampleRate, frames.Add);
        rx.Process(WithPadding(modulator.Modulate(bits)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    [Theory]
    [InlineData(2400)]
    [InlineData(3600)]
    public void A_Large_Multi_Block_Frame_Roundtrips(int bitRate)
    {
        var random = new Random(bitRate);
        var information = new byte[300];
        random.NextBytes(information);
        byte[] ax25 =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F,
            0x03, 0xF0, .. information,
        ];
        var (tx, rx, frames) = MakePair(bitRate);

        rx.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }
}
