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
    public void A_Frame_Over_The_Cap_Is_Dropped_Said_Once_And_The_Next_Frame_Still_Decodes()
    {
        // The cap is a memory bound on what is buffered before a closing delimiter arrives, and
        // a frame that hits it has to be REPORTED: to the host that sent it a vanished frame is
        // indistinguishable from a bad link, which is how a 2 KiB cap went unnoticed through a
        // throughput campaign that could not get a 4 kB frame through (issue 503).
        var frames = new List<KissFrame>();
        var oversize = new List<int>();
        var decoder = new KissDecoder(frames.Add, maxFrame: 100, oversize.Add);

        byte[] big = KissCodec.Encode(new KissFrame(0, KissCommand.Data, new byte[300]));
        byte[] small = KissCodec.Encode(new KissFrame(0, KissCommand.Data, [1, 2, 3]));

        decoder.Push(big[..150]);
        decoder.Push(big[150..]);
        decoder.Push(small);

        oversize.Should().Equal([100], "once per dropped frame, naming the cap it hit");
        frames.Should().ContainSingle().Which.Payload.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public void The_Default_Cap_Admits_The_Longest_Frame_A_Burst_Modem_Carries()
    {
        // 4095 bytes of payload is what the OFDM-FM burst header can describe, and a frame that
        // long plus its AX.25 header has to pass this layer, or the modem's biggest throughput
        // lever is capped somewhere it cannot be seen. The old default of 2048 failed this.
        var frames = new List<KissFrame>();
        var oversize = new List<int>();
        var decoder = new KissDecoder(frames.Add, oversize: oversize.Add);
        var payload = new byte[4095 + 16];
        new Random(3).NextBytes(payload);

        decoder.Push(KissCodec.Encode(new KissFrame(0, KissCommand.Data, payload)));

        KissDecoder.DefaultMaxFrame.Should().BeGreaterThanOrEqualTo(payload.Length);
        oversize.Should().BeEmpty();
        frames.Should().ContainSingle().Which.Payload.Should().Equal(payload);
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
