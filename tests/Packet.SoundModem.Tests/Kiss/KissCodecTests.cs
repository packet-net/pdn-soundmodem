using Packet.SoundModem.Kiss;

namespace Packet.SoundModem.Tests.Kiss;

public class KissCodecTests
{
    private static List<KissFrame> Decode(params byte[][] chunks)
    {
        var frames = new List<KissFrame>();
        var decoder = new KissDecoder(frames.Add);
        foreach (byte[] chunk in chunks)
        {
            decoder.Push(chunk);
        }

        return frames;
    }

    [Fact]
    public void Frames_Roundtrip_Including_Escape_Bytes()
    {
        byte[] payload = [0x01, 0xC0, 0xDB, 0xC0, 0xFF, 0x00];
        byte[] wire = KissCodec.Encode(new KissFrame(3, KissCommand.Data, payload));

        var frames = Decode(wire);

        frames.Should().ContainSingle();
        frames[0].Port.Should().Be(3);
        frames[0].Command.Should().Be(KissCommand.Data);
        frames[0].Payload.Should().Equal(payload);
    }

    [Fact]
    public void Split_Delivery_Reassembles()
    {
        byte[] wire = KissCodec.Encode(new KissFrame(0, KissCommand.Data, [1, 2, 3, 4, 5]));

        var frames = Decode(wire[..3], wire[3..4], wire[4..]);

        frames.Should().ContainSingle().Which.Payload.Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public void Garbage_Before_The_First_Fend_Is_Ignored()
    {
        byte[] wire = [0x55, 0xAA, .. KissCodec.Encode(new KissFrame(1, KissCommand.Data, [9]))];

        var frames = Decode(wire);

        frames.Should().ContainSingle().Which.Port.Should().Be(1);
    }

    [Fact]
    public void Back_To_Back_Frames_Share_Delimiters()
    {
        byte[] first = KissCodec.Encode(new KissFrame(0, KissCommand.Data, [1]));
        byte[] second = KissCodec.Encode(new KissFrame(0, KissCommand.Data, [2]));

        var frames = Decode([.. first, .. second]);

        frames.Should().HaveCount(2);
    }

    [Fact]
    public void Parameter_Commands_Carry_Their_Nibble()
    {
        byte[] wire = KissCodec.Encode(new KissFrame(2, KissCommand.SlotTime, [10]));

        var frames = Decode(wire);

        frames.Should().ContainSingle();
        frames[0].Command.Should().Be(KissCommand.SlotTime);
        frames[0].Port.Should().Be(2);
        frames[0].Payload.Should().Equal([10]);
    }

    [Fact]
    public void Empty_Frames_And_Keepalive_Fends_Are_Ignored()
    {
        Decode([KissCodec.Fend, KissCodec.Fend, KissCodec.Fend]).Should().BeEmpty();
    }
}
