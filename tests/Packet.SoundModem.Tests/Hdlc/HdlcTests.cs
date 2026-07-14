using Packet.SoundModem.Hdlc;

namespace Packet.SoundModem.Tests.Hdlc;

public class HdlcTests
{
    private static byte[] SampleFrame(Random random, int infoLength)
    {
        var frame = new byte[15 + infoLength];
        // Two plausible AX.25 addresses + control; info is random (worst case for stuffing).
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x81];
        header.CopyTo(frame, 0);
        random.NextBytes(frame.AsSpan(15));
        return frame;
    }

    private static List<byte[]> RunThroughDeframer(IEnumerable<int> bits)
    {
        var received = new List<byte[]>();
        var deframer = new HdlcDeframer(received.Add);
        foreach (int bit in bits)
        {
            deframer.PushBit(bit);
        }

        return received;
    }

    [Fact]
    public void Framer_To_Deframer_Roundtrips()
    {
        byte[] frame = SampleFrame(new Random(1), 56);

        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 4, closingFlags: 2);
        var received = RunThroughDeframer(bits.Select(b => (int)b));

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void All_Ones_Content_Exercises_Stuffing_And_Roundtrips()
    {
        var frame = new byte[40];
        Array.Fill(frame, (byte)0xFF);

        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 2);
        var received = RunThroughDeframer(bits.Select(b => (int)b));

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Back_To_Back_Frames_Sharing_Flags_Both_Decode()
    {
        byte[] first = SampleFrame(new Random(2), 10);
        byte[] second = SampleFrame(new Random(3), 25);

        var bits = new List<int>();
        bits.AddRange(HdlcFramer.FrameBits(first, openingFlags: 3, closingFlags: 1).Select(b => (int)b));
        // Second frame reuses the first's closing flag as its opener — legal HDLC.
        bits.AddRange(HdlcFramer.FrameBits(second, openingFlags: 1, closingFlags: 1).Select(b => (int)b).Skip(8 - 8));
        var received = RunThroughDeframer(bits);

        received.Should().HaveCount(2);
        received[0].Should().Equal(first);
        received[1].Should().Equal(second);
    }

    [Fact]
    public void A_Flipped_Bit_Fails_The_Fcs_And_The_Frame_Is_Dropped()
    {
        byte[] frame = SampleFrame(new Random(4), 30);
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 2);
        bits[40] ^= 1; // inside the frame content

        var received = new List<byte[]>();
        var deframer = new HdlcDeframer(received.Add);
        foreach (byte bit in bits)
        {
            deframer.PushBit(bit);
        }

        received.Should().BeEmpty();
        deframer.CrcFailures.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void An_Abort_Sequence_Discards_The_Frame_In_Progress()
    {
        byte[] frame = SampleFrame(new Random(5), 20);
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 2);

        var received = new List<byte[]>();
        var deframer = new HdlcDeframer(received.Add);
        // Half the frame, then eight 1s (abort), then idle zeros.
        foreach (byte bit in bits.AsSpan(0, bits.Length / 2))
        {
            deframer.PushBit(bit);
        }

        for (int i = 0; i < 8; i++)
        {
            deframer.PushBit(1);
        }

        for (int i = 0; i < 64; i++)
        {
            deframer.PushBit(0);
        }

        received.Should().BeEmpty();
        deframer.CrcFailures.Should().Be(0); // aborted, not CRC-failed
    }

    [Fact]
    public void Noise_Before_The_Frame_Does_Not_Prevent_Decoding()
    {
        byte[] frame = SampleFrame(new Random(6), 12);
        var random = new Random(7);
        var bits = new List<int>();
        for (int i = 0; i < 500; i++)
        {
            bits.Add(random.Next(2));
        }

        bits.AddRange(HdlcFramer.FrameBits(frame, openingFlags: 8).Select(b => (int)b));
        var received = RunThroughDeframer(bits);

        received.Should().Contain(f => f.SequenceEqual(frame));
    }

    [Fact]
    public void Nrzi_Roundtrips_Through_Encoder_And_Decoder()
    {
        var random = new Random(8);
        var encoder = new NrziEncoder();
        var decoder = new NrziDecoder();

        // The decoder's first output depends on the arbitrary initial line level, so prime
        // it with one bit before comparing (real receivers sync during the preamble).
        decoder.Decode(encoder.Encode(1));

        for (int i = 0; i < 1000; i++)
        {
            int bit = random.Next(2);
            decoder.Decode(encoder.Encode(bit)).Should().Be(bit, $"bit {i}");
        }
    }

    [Fact]
    public void Framed_Bits_Survive_Nrzi_Transmission()
    {
        byte[] frame = SampleFrame(new Random(9), 33);
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 4);

        var encoder = new NrziEncoder();
        var decoder = new NrziDecoder();
        var received = new List<byte[]>();
        var deframer = new HdlcDeframer(received.Add);
        foreach (byte bit in bits)
        {
            deframer.PushBit(decoder.Decode(encoder.Encode(bit)));
        }

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }
}
