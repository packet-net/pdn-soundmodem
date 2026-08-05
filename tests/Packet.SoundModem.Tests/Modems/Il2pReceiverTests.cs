using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The IL2P receive seam, and the M0LTE.Il2p behaviour the whole design rests on.
/// <see cref="A_Crcless_Deframer_Reads_An_Il2p_Crc_Frame_As_The_Same_Frame_Thirty_Two_Bits_Early"/>
/// is the load-bearing one: it pins what a <c>crcMode: false</c> deframer does when a well-formed
/// IL2P+CRC frame goes past it, which is what decides whether two deframers can be run side by
/// side at all. If a future M0LTE.Il2p changes that answer, that test fails first and loudly,
/// rather than the channel quietly starting to hand every frame up twice.
/// </summary>
public class Il2pReceiverTests
{
    /// <summary>Bits of silence pushed after a frame, standing in for the noise a real receiver
    /// keeps demodulating once the carrier has gone.</summary>
    private const int IdleBits = 200;

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(200)]
    [InlineData(700)]
    public void A_Crcless_Deframer_Reads_An_Il2p_Crc_Frame_As_The_Same_Frame_Thirty_Two_Bits_Early(int infoBytes)
    {
        // Measured against M0LTE.Il2p 0.1.2. Both readings decode the same AX.25 frame from the
        // same bits: the CRC-less one sizes the payload from the header, decodes it and goes back
        // to hunting, leaving the four trailer bytes to be hunted through as though they were
        // noise, so it finishes exactly one trailer earlier. It does not fail, and it does not
        // hand up a frame two bytes longer - it hands up an identical one, which is why
        // Il2pReceiver holds it rather than delivering it.
        byte[] ax25 = Ax25Frame("GB7BPQ", "BEACON", new string('x', infoBytes));
        byte[] bits = FrameBits(ax25, crc: true);

        byte[]? plainFrame = null;
        byte[]? crcFrame = null;
        int plainAt = -1;
        int crcAt = -1;
        int index = 0;
        var plain = new Il2pDeframer((frame, _) => (plainFrame, plainAt) = (frame, index), crcMode: false);
        var crc = new Il2pDeframer((frame, _) => (crcFrame, crcAt) = (frame, index), crcMode: true);
        for (; index < bits.Length + IdleBits; index++)
        {
            int bit = index < bits.Length ? bits[index] : 0;
            plain.PushBit(bit);
            crc.PushBit(bit);
        }

        crcFrame.Should().Equal(ax25, "the IL2P+CRC reading decodes the frame it was sent");
        plainFrame.Should().Equal(ax25, "and the plain reading decodes the identical frame");
        (crcAt - plainAt).Should().Be(
            Il2pCodec.TrailingCrcWireLength * 8,
            "the plain reading finishes exactly one CRC trailer before the IL2P+CRC one, at any "
            + "payload size - which is the window Il2pReceiver holds a plain frame for");
    }

    [Fact]
    public void A_Plain_Il2p_Frame_Is_Dropped_By_An_Il2p_Crc_Link_By_Default()
    {
        // The GB7BPQ case as it stands today, and the default this change must not disturb.
        List<byte[]> got = Push(FrameBits(Gb7bpqBeacon(), crc: false), crcMode: true, acceptPlainIl2p: false);

        got.Should().BeEmpty("IL2P+CRC is the interop ground truth and nothing else gets in");
    }

    [Fact]
    public void A_Plain_Il2p_Frame_Is_Delivered_Once_When_The_Link_Accepts_Them()
    {
        byte[] beacon = Gb7bpqBeacon();

        (List<byte[]> got, List<Il2pDecodeInfo> info) = PushWithInfo(
            FrameBits(beacon, crc: false), crcMode: true, acceptPlainIl2p: true);

        got.Should().ContainSingle("the frame the station could not read is now delivered, once")
            .Which.Should().Equal(beacon);
        info[0].CrcValid.Should().BeNull(
            "no CRC was checked, and null is the honest way to say so - Reed-Solomon alone stood "
            + "behind this frame");
    }

    [Fact]
    public void An_Il2p_Crc_Frame_Is_Delivered_Once_When_The_Link_Also_Accepts_Plain_Il2p()
    {
        // The hazard the whole seam exists to avoid: both readings decode this frame, so a naive
        // pair of deframers would deliver every ordinary frame on the channel twice.
        byte[] beacon = Gb7bpqBeacon();

        (List<byte[]> got, List<Il2pDecodeInfo> info) = PushWithInfo(
            FrameBits(beacon, crc: true), crcMode: true, acceptPlainIl2p: true);

        got.Should().ContainSingle("the IL2P+CRC reading wins and the plain copy of it is dropped")
            .Which.Should().Equal(beacon);
        info[0].CrcValid.Should().BeTrue("the delivered copy is the one that had its CRC checked");
    }

    [Fact]
    public void An_Il2p_Crc_Frame_Is_Delivered_Once_When_The_Carrier_Drops_On_The_Trailer()
    {
        // Reset is the DCD falling edge, and it releases whatever plain frame is being held. The
        // frame here has already been claimed by the IL2P+CRC reading by then, so there is
        // nothing to release and the reset must not conjure a second copy.
        byte[] beacon = Gb7bpqBeacon();
        var got = new List<byte[]>();
        var receiver = new Il2pReceiver((frame, _) => got.Add(frame), crcMode: true, acceptPlainIl2p: true);
        foreach (byte bit in FrameBits(beacon, crc: true))
        {
            receiver.PushBit(bit);
        }

        receiver.Reset();

        got.Should().ContainSingle().Which.Should().Equal(beacon);
    }

    [Fact]
    public void A_Plain_Frame_Still_Being_Held_When_The_Carrier_Drops_Is_Delivered_Anyway()
    {
        // The usual shape of the case this option is for: a plain frame ends where the
        // transmission ends, so the 32 bits the hold is waiting for never arrive. On a 300 baud
        // BPSK link DCD drops about 24 bit times after the last symbol, which is inside the hold.
        byte[] beacon = Gb7bpqBeacon();
        var got = new List<byte[]>();
        var receiver = new Il2pReceiver((frame, _) => got.Add(frame), crcMode: true, acceptPlainIl2p: true);
        foreach (byte bit in FrameBits(beacon, crc: false))
        {
            receiver.PushBit(bit);
        }

        got.Should().BeEmpty("the plain reading is held until the IL2P+CRC one has had its turn");
        receiver.Reset();

        got.Should().ContainSingle("and a held frame is released by the reset, not discarded")
            .Which.Should().Equal(beacon);
    }

    [Fact]
    public void A_Held_Plain_Frame_Waits_For_The_Whole_Trailer_Before_It_Is_Released()
    {
        // Pins the hold length from the outside: one bit short of the trailer, nothing has been
        // delivered; the bit that completes it delivers the IL2P+CRC reading's copy instead.
        byte[] beacon = Gb7bpqBeacon();
        byte[] bits = FrameBits(beacon, crc: true);
        var got = new List<byte[]>();
        var receiver = new Il2pReceiver((frame, _) => got.Add(frame), crcMode: true, acceptPlainIl2p: true);
        for (int i = 0; i < bits.Length - 1; i++)
        {
            receiver.PushBit(bits[i]);
        }

        got.Should().BeEmpty("the plain copy emitted 31 bits ago is still being held");
        receiver.PushBit(bits[^1]);

        got.Should().ContainSingle().Which.Should().Equal(beacon);
    }

    [Fact]
    public void Accepting_Plain_Il2p_Does_Nothing_To_A_Link_That_Is_Already_Crcless()
    {
        // A -nocrc/-il2p mode reads plain IL2P with the one deframer it has, so there is nothing
        // to switch on and - importantly - no second reading to deliver the same frame twice.
        byte[] beacon = Gb7bpqBeacon();
        var receiver = new Il2pReceiver((_, _) => { }, crcMode: false, acceptPlainIl2p: true);
        receiver.AcceptsPlainIl2p.Should().BeFalse();

        List<byte[]> got = Push(FrameBits(beacon, crc: false), crcMode: false, acceptPlainIl2p: true);

        got.Should().ContainSingle().Which.Should().Equal(beacon);
    }

    [Fact]
    public void Two_Plain_Frames_In_A_Row_Are_Both_Delivered()
    {
        // The hold is per frame, not a filter: back-to-back transmissions both come through, in
        // order, however similar their contents.
        byte[] first = Ax25Frame("GB7BPQ", "BEACON", "first");
        byte[] second = Ax25Frame("GB7BPQ", "BEACON", "second");
        byte[] bits = [.. FrameBits(first, crc: false), .. FrameBits(second, crc: false)];

        List<byte[]> got = Push(bits, crcMode: true, acceptPlainIl2p: true);

        got.Should().HaveCount(2);
        got[0].Should().Equal(first);
        got[1].Should().Equal(second);
    }

    private static List<byte[]> Push(byte[] bits, bool crcMode, bool acceptPlainIl2p) =>
        PushWithInfo(bits, crcMode, acceptPlainIl2p).Frames;

    private static (List<byte[]> Frames, List<Il2pDecodeInfo> Info) PushWithInfo(
        byte[] bits, bool crcMode, bool acceptPlainIl2p)
    {
        var frames = new List<byte[]>();
        var info = new List<Il2pDecodeInfo>();
        var receiver = new Il2pReceiver(
            (frame, decode) =>
            {
                frames.Add(frame);
                info.Add(decode);
            },
            crcMode, acceptPlainIl2p);
        foreach (byte bit in bits)
        {
            receiver.PushBit(bit);
        }

        for (int i = 0; i < IdleBits; i++)
        {
            receiver.PushBit(0);
        }

        return (frames, info);
    }

    private static byte[] FrameBits(byte[] ax25, bool crc) =>
        Il2pFramer.FrameBits(
            Il2pCodec.Encode(ax25, appendCrc: crc), preambleBits: 64, Il2pFramer.PreambleStyle.Zeros);

    /// <summary>The burst the 40 m station could not read, in shape: a BPQ32 node's position
    /// beacon, which is what turned up in the survey as <c>bpsk300-nocrc @ 2116 Hz</c>. Shared
    /// with <see cref="Il2pPlainPassthroughTests"/>, which puts it through real audio.</summary>
    internal static byte[] Gb7bpqBeacon() =>
        Ax25Frame("GB7BPQ", "BEACON", "=5828.54N/00612.69W- {BPQ32}");

    /// <summary>
    /// A UI frame in the AX.25 v2 command convention: destination C bit set, source C bit clear.
    /// That convention matters here. IL2P Type 1 header translation keeps a single C bit, so a
    /// v1-style both-bits-clear frame does not round-trip through it byte-exactly - which is
    /// invisible in the IL2P+CRC modes, where the codec falls back to the transparent Type 0
    /// encapsulation when the header does not round-trip (M0LTE.Il2p 0.1.1, see
    /// <see cref="Il2pCrcDegenerateHeaderTests"/>), and very visible in the plain ones, where
    /// there is no CRC to make it fall back.
    /// </summary>
    private static byte[] Ax25Frame(string from, string to, string info)
    {
        byte[] frame = new byte[16 + info.Length];
        WriteAddress(frame, 0, to, 0xE0);
        WriteAddress(frame, 7, from, 0x61);
        frame[14] = 0x03; // UI
        frame[15] = 0xF0; // no layer 3
        for (int i = 0; i < info.Length; i++)
        {
            frame[16 + i] = (byte)info[i];
        }

        return frame;

        static void WriteAddress(byte[] frame, int at, string call, byte ssid)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = ssid;
        }
    }
}
