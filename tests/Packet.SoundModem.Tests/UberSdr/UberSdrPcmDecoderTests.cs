using System.Buffers.Binary;
using AwesomeAssertions;
using Packet.SoundModem.UberSdr;
using ZstdSharp;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// Pins the ka9q_ubersdr binary PCM/IQ wire format against <see cref="PcmBinaryDecoder"/>:
/// the PC/PM hybrid header, the offsets the GPS timestamp / sample rate / channels sit at, and
/// the big-endian → little-endian int16 swap. Synthetic frames, so these are deterministic and
/// need no network.
/// </summary>
public class UberSdrPcmDecoderTests
{
    // The wire carries big-endian int16; after decode we read little-endian and must recover
    // these exact logical sample values.
    private static readonly short[] Samples = [0x0102, unchecked((short)0xFFFE), 0x7FFF, unchecked((short)0x8000), 0, -1, 12345];

    private static byte[] BigEndianPcm(short[] samples)
    {
        var b = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(i * 2), samples[i]);
        }
        return b;
    }

    private static byte[] FullHeaderFrame(ulong gpsNanos, uint sampleRate, byte channels, short[] samples)
    {
        byte[] pcm = BigEndianPcm(samples);
        var f = new byte[29 + pcm.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(f, 0x5043);           // "PC"
        f[2] = 1;                                                       // version
        f[3] = 2;                                                       // inner format (pcm-zstd)
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(4), gpsNanos);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(12), 0);      // deprecated wall clock
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(20), sampleRate);
        f[24] = channels;
        pcm.CopyTo(f.AsSpan(29));
        return f;
    }

    private static byte[] MinimalHeaderFrame(ulong gpsNanos, short[] samples)
    {
        byte[] pcm = BigEndianPcm(samples);
        var f = new byte[13 + pcm.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(f, 0x504D);           // "PM"
        f[2] = 1;                                                       // version
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(3), gpsNanos);
        pcm.CopyTo(f.AsSpan(13));
        return f;
    }

    private static short[] ReadLittleEndian(ReadOnlyMemory<byte> pcm)
    {
        var span = pcm.Span;
        var s = new short[span.Length / 2];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
        }
        return s;
    }

    [Fact]
    public void Full_Header_Parses_Timestamp_Rate_Channels_And_Byteswaps_Pcm()
    {
        using var d = new PcmBinaryDecoder();
        byte[] frame = FullHeaderFrame(gpsNanos: 1784907596300809169UL, sampleRate: 48000, channels: 2, Samples);

        PcmPacket pkt = d.Decode(frame, isZstd: false);

        pkt.GpsTimestampNanos.Should().Be(1784907596300809169UL);
        pkt.SampleRate.Should().Be(48000);
        pkt.Channels.Should().Be(2);
        ReadLittleEndian(pkt.Pcm).Should().Equal(Samples);
    }

    [Fact]
    public void Zstd_Wrapped_Full_Header_Decodes_Identically()
    {
        using var compressor = new Compressor();
        using var d = new PcmBinaryDecoder();
        byte[] frame = FullHeaderFrame(1000UL, 48000, 2, Samples);
        byte[] wrapped = compressor.Wrap(frame).ToArray();

        PcmPacket pkt = d.Decode(wrapped, isZstd: true);

        pkt.SampleRate.Should().Be(48000);
        ReadLittleEndian(pkt.Pcm).Should().Equal(Samples);
    }

    [Fact]
    public void Minimal_Header_Reuses_Rate_And_Channels_From_Preceding_Full_Header()
    {
        using var d = new PcmBinaryDecoder();
        d.Decode(FullHeaderFrame(1UL, 96000, 2, Samples), isZstd: false); // establishes rate/channels

        PcmPacket pkt = d.Decode(MinimalHeaderFrame(2UL, Samples), isZstd: false);

        pkt.GpsTimestampNanos.Should().Be(2UL);
        pkt.SampleRate.Should().Be(96000);
        pkt.Channels.Should().Be(2);
        ReadLittleEndian(pkt.Pcm).Should().Equal(Samples);
    }

    [Fact]
    public void Minimal_Header_Uses_Preseeded_Iq48_Defaults()
    {
        // The client pre-seeds 48000/2 because the server may open with minimal frames.
        using var d = new PcmBinaryDecoder { LastSampleRate = 48000, LastChannels = 2 };

        PcmPacket pkt = d.Decode(MinimalHeaderFrame(5UL, Samples), isZstd: false);

        pkt.SampleRate.Should().Be(48000);
        pkt.Channels.Should().Be(2);
    }

    [Fact]
    public void Minimal_Header_Without_Any_Prior_Rate_Throws()
    {
        using var d = new PcmBinaryDecoder(); // no pre-seed, no prior full header
        Action act = () => d.Decode(MinimalHeaderFrame(1UL, Samples), isZstd: false);
        act.Should().Throw<InvalidDataException>().WithMessage("*minimal header before*");
    }

    [Fact]
    public void Invalid_Magic_Throws()
    {
        using var d = new PcmBinaryDecoder();
        byte[] frame = [0x00, 0x00, 0x01, 0x02, 0x03, 0x04];
        Action act = () => d.Decode(frame, isZstd: false);
        act.Should().Throw<InvalidDataException>().WithMessage("*invalid PCM magic*");
    }

    [Fact]
    public void Truncated_Full_Header_Throws()
    {
        using var d = new PcmBinaryDecoder();
        byte[] frame = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x5043); // "PC" but only 20 bytes
        Action act = () => d.Decode(frame, isZstd: false);
        act.Should().Throw<InvalidDataException>().WithMessage("*too short*");
    }
}
