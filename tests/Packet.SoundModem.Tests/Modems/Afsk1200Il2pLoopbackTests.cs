using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class Afsk1200Il2pLoopbackTests
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Clean_Frame_Roundtrips_Through_Audio(bool crc)
    {
        byte[] frame = SampleFrame(1, 64);
        var frames = new List<byte[]>();
        var tx = new Afsk1200Il2pModem(SampleRate, _ => { }, crc);
        var rx = new Afsk1200Il2pModem(SampleRate, frames.Add, crc);

        float[] audio = tx.Modulate(frame, txDelayMilliseconds: 200);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        rx.Process(padded);

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void A_Large_Multi_Block_Frame_Roundtrips()
    {
        byte[] frame = SampleFrame(2, 600);
        var frames = new List<byte[]>();
        var tx = new Afsk1200Il2pModem(SampleRate, _ => { });
        var rx = new Afsk1200Il2pModem(SampleRate, frames.Add);

        float[] audio = tx.Modulate(frame, txDelayMilliseconds: 200);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        rx.Process(padded);

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }
}
