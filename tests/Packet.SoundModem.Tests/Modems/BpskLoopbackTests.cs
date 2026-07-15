using Packet.SoundModem.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class BpskLoopbackTests
{
    private const int SampleRate = 12000;

    private static (BpskDemodulator Demodulator, List<byte[]> Frames) BuildReceiver(
        double carrier = 1500)
    {
        var frames = new List<byte[]>();
        var deframer = new Il2pDeframer((frame, _) => frames.Add(frame), crcMode: true);
        var demodulator = new BpskDemodulator(SampleRate, deframer.PushBit, carrier);
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
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                padded[i] += noiseSigma
                    * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
        }

        return padded;
    }

    private static float[] ModulateFrame(byte[] ax25, int preambleBits = 96)
    {
        byte[] wire = Il2pCodec.Encode(ax25, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        return new BpskModulator(SampleRate).Modulate(bits);
    }

    [Fact]
    public void A_Clean_Il2p_Frame_Roundtrips_Through_Audio()
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(ModulateFrame(ax25)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    [Theory]
    [InlineData(0.10f, 21)]
    [InlineData(0.20f, 22)]
    public void Frames_Survive_Additive_Gaussian_Noise(float sigma, int seed)
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A946F81");

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(ModulateFrame(ax25, preambleBits: 160), seed, sigma));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    [Fact]
    public void A_Small_Carrier_Offset_Is_Tolerated()
    {
        // TX at 1505 Hz, RX expecting 1500: differential detection rotates 6°/symbol.
        byte[] ax25 = Convert.FromHexString("86A24040404060969668908A94FF03F0");
        byte[] wire = Il2pCodec.Encode(ax25, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, 96, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new BpskModulator(SampleRate, carrierFrequency: 1505).Modulate(bits);

        var (demodulator, frames) = BuildReceiver(carrier: 1500);
        demodulator.Process(WithPadding(audio));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    [Fact]
    public void Back_To_Back_Frames_Without_Preamble_Both_Decode()
    {
        byte[] first = Convert.FromHexString("968264888AAEE4969668908A946F81");
        byte[] second = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        var modulator = new BpskModulator(SampleRate);
        var bits = new List<byte>();
        bits.AddRange(Il2pFramer.FrameBits(
            Il2pCodec.Encode(first, appendCrc: true), 96, Il2pFramer.PreambleStyle.Zeros));
        bits.AddRange(Il2pFramer.FrameBits(
            Il2pCodec.Encode(second, appendCrc: true), 0, Il2pFramer.PreambleStyle.Zeros));
        float[] audio = modulator.Modulate([.. bits]);

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(audio));

        frames.Should().HaveCount(2);
        frames[0].Should().Equal(first);
        frames[1].Should().Equal(second);
    }

    [Fact]
    public void A_Large_Multi_Block_Frame_Roundtrips()
    {
        var random = new Random(6);
        var information = new byte[400];
        random.NextBytes(information);
        byte[] ax25 =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F,
            0x03, 0xF0, .. information,
        ];

        var (demodulator, frames) = BuildReceiver();
        demodulator.Process(WithPadding(ModulateFrame(ax25)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }
}
