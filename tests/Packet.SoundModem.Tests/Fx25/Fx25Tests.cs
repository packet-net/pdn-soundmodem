using Packet.SoundModem.Fx25;

namespace Packet.SoundModem.Tests.Fx25;

public class Fx25Tests
{
    private static byte[] SampleFrame(int infoLength)
    {
        var frame = new byte[16 + infoLength];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(infoLength).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static List<(byte[] Frame, int Corrected)> Decode(byte[] bits)
    {
        var received = new List<(byte[], int)>();
        var deframer = new Fx25Deframer((frame, corrected) => received.Add((frame, corrected)));
        foreach (byte bit in bits)
        {
            deframer.PushBit(bit);
        }

        return received;
    }

    [Theory]
    [InlineData(10, 16)]
    [InlineData(60, 16)]
    [InlineData(120, 32)]
    [InlineData(150, 64)]
    public void Clean_Blocks_Roundtrip(int infoLength, int checkBytes)
    {
        byte[] frame = SampleFrame(infoLength);

        var received = Decode(Fx25Codec.EncodeBits(frame, checkBytes));

        received.Should().ContainSingle();
        received[0].Frame.Should().Equal(frame);
        received[0].Corrected.Should().Be(0);
    }

    [Fact]
    public void Byte_Errors_Beyond_Hdlc_Survivability_Are_Repaired()
    {
        byte[] frame = SampleFrame(50);
        byte[] bits = Fx25Codec.EncodeBits(frame, checkBytes: 16);

        // Corrupt 6 whole bytes inside the data region (after the 64-bit tag): plain
        // HDLC would be destroyed; RS(*, 16 check) repairs up to 8 byte errors.
        for (int i = 0; i < 6; i++)
        {
            int byteStart = (8 + 3 + i * 7) * 8;
            for (int b = 0; b < 8; b++)
            {
                bits[byteStart + b] ^= 1;
            }
        }

        var received = Decode(bits);

        received.Should().ContainSingle();
        received[0].Frame.Should().Equal(frame);
        received[0].Corrected.Should().Be(6);
    }

    [Fact]
    public void Tag_Bit_Errors_Within_Tolerance_Still_Match()
    {
        byte[] frame = SampleFrame(20);
        byte[] bits = Fx25Codec.EncodeBits(frame, checkBytes: 16);
        bits[3] ^= 1;
        bits[17] ^= 1;
        bits[40] ^= 1; // three of the 64 tag bits

        Decode(bits).Should().ContainSingle().Which.Frame.Should().Equal(frame);
    }

    [Fact]
    public void The_Smallest_Fitting_Format_Is_Picked()
    {
        // A small frame with 16 check bytes must pick tag 0x04 (32 data bytes):
        // 8 tag + 32 data + 16 check = 56 bytes.
        byte[] bits = Fx25Codec.EncodeBits(SampleFrame(5), checkBytes: 16);
        bits.Length.Should().Be((8 + 32 + 16) * 8);
    }

    [Fact]
    public void Oversized_Frames_Are_Rejected()
    {
        var act = () => Fx25Codec.EncodeBits(SampleFrame(300), checkBytes: 16);
        act.Should().Throw<ArgumentException>(); // stuffed > 255 bytes never fits 16-check formats' max
    }

    [Fact]
    public void The_Embedded_Frame_Is_Also_Plain_Hdlc_Decodable()
    {
        // FX.25's raison d'être: legacy receivers see a normal HDLC frame.
        byte[] frame = SampleFrame(40);
        byte[] bits = Fx25Codec.EncodeBits(frame, checkBytes: 32);

        var frames = new List<byte[]>();
        var deframer = new Packet.SoundModem.Hdlc.HdlcDeframer(frames.Add);
        foreach (byte bit in bits)
        {
            deframer.PushBit(bit);
        }

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }
}
