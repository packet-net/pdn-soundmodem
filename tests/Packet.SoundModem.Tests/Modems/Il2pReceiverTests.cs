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
    public void A_Plain_Il2p_Frame_Is_Read_And_Withheld_From_The_Host_By_Default()
    {
        // The GB7BPQ case, and the split the operator asked for: the station reads the frame
        // whatever its configuration says, because it cannot report a neighbour it never
        // demodulated, and then does not hand an RS-only frame to a host that asked for IL2P+CRC.
        byte[] beacon = Gb7bpqBeacon();

        (List<byte[]> got, List<Il2pDecodeInfo> info, List<Il2pDelivery> delivery) = PushWithInfo(
            FrameBits(beacon, crc: false), crcMode: true, acceptPlainIl2p: false);

        got.Should().ContainSingle("the burst is read even with the option off")
            .Which.Should().Equal(beacon);
        info[0].CrcValid.Should().BeNull("there was no CRC to check");
        delivery[0].PlainIl2p.Should().BeTrue(
            "the fact a display badges: Reed-Solomon alone stood behind this frame");
        delivery[0].MonitorOnly.Should().BeTrue(
            "IL2P+CRC is the interop ground truth, so the host does not see this one");
    }

    [Fact]
    public void A_Plain_Il2p_Frame_Reaches_The_Host_Once_When_The_Link_Accepts_Them()
    {
        byte[] beacon = Gb7bpqBeacon();

        (List<byte[]> got, List<Il2pDecodeInfo> info, List<Il2pDelivery> delivery) = PushWithInfo(
            FrameBits(beacon, crc: false), crcMode: true, acceptPlainIl2p: true);

        got.Should().ContainSingle("the frame the station could not read is now delivered, once")
            .Which.Should().Equal(beacon);
        info[0].CrcValid.Should().BeNull(
            "no CRC was checked, and null is the honest way to say so - Reed-Solomon alone stood "
            + "behind this frame");
        delivery[0].PlainIl2p.Should().BeTrue(
            "the badge is about the decode, so it is there whether or not the option is on");
        delivery[0].MonitorOnly.Should().BeFalse("the option is what passes it to the host");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_Il2p_Crc_Frame_Reaches_The_Host_Exactly_Once_Either_Way(bool acceptPlainIl2p)
    {
        // The hazard the whole seam exists to avoid, and now the hazard on every IL2P+CRC link
        // rather than on the handful that opted in: both readings decode this frame, so a naive
        // pair of deframers would hand every ordinary frame on the channel up twice. Run with the
        // option both ways, because it no longer decides whether the second reading exists.
        byte[] beacon = Gb7bpqBeacon();

        (List<byte[]> got, List<Il2pDecodeInfo> info, List<Il2pDelivery> delivery) = PushWithInfo(
            FrameBits(beacon, crc: true), crcMode: true, acceptPlainIl2p);

        got.Should().ContainSingle("the IL2P+CRC reading wins and the plain copy of it is dropped")
            .Which.Should().Equal(beacon);
        info[0].CrcValid.Should().BeTrue("the delivered copy is the one that had its CRC checked");
        delivery[0].PlainIl2p.Should().BeFalse("a CRC stood behind this one");
        delivery[0].MonitorOnly.Should().BeFalse("and an ordinary frame always goes to the host");
    }

    [Fact]
    public void An_Il2p_Crc_Frame_Is_Delivered_Once_When_The_Carrier_Drops_On_The_Trailer()
    {
        // Reset is the DCD falling edge, and it releases whatever plain frame is being held. The
        // frame here has already been claimed by the IL2P+CRC reading by then, so there is
        // nothing to release and the reset must not conjure a second copy.
        byte[] beacon = Gb7bpqBeacon();
        var got = new List<byte[]>();
        var receiver = new Il2pReceiver(
            (frame, _, _) => got.Add(frame), crcMode: true, acceptPlainIl2p: true);
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
        var receiver = new Il2pReceiver(
            (frame, _, _) => got.Add(frame), crcMode: true, acceptPlainIl2p: true);
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
        var receiver = new Il2pReceiver(
            (frame, _, _) => got.Add(frame), crcMode: true, acceptPlainIl2p: true);
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
        // to switch on and - importantly - no second reading to hand the same frame up twice.
        byte[] beacon = Gb7bpqBeacon();
        var receiver = new Il2pReceiver((_, _, _) => { }, crcMode: false, acceptPlainIl2p: true);
        receiver.ReadsPlainIl2p.Should().BeFalse();

        (List<byte[]> got, _, List<Il2pDelivery> delivery) = PushWithInfo(
            FrameBits(beacon, crc: false), crcMode: false, acceptPlainIl2p: true);

        got.Should().ContainSingle().Which.Should().Equal(beacon);
        delivery[0].MonitorOnly.Should().BeFalse(
            "this is the link's own traffic, not a second opinion about somebody else's");
        delivery[0].PlainIl2p.Should().BeTrue(
            "and it is still RS-only, which is the question the badge asks - a frame on a "
            + "CRC-less mode has exactly as little behind it as one read off an IL2P+CRC link");
    }

    [Fact]
    public void An_Il2p_Crc_Link_Always_Runs_The_Second_Reading()
    {
        // The point of the change, stated as a property rather than inferred from behaviour: the
        // option decides where a plain frame goes, not whether the station can read one.
        new Il2pReceiver((_, _, _) => { }, crcMode: true, acceptPlainIl2p: false)
            .ReadsPlainIl2p.Should().BeTrue("a station cannot report a neighbour it never read");
        new Il2pReceiver((_, _, _) => { }, crcMode: true, acceptPlainIl2p: false)
            .DeliversPlainIl2p.Should().BeFalse("and off means the host is not told");
        new Il2pReceiver((_, _, _) => { }, crcMode: true, acceptPlainIl2p: true)
            .DeliversPlainIl2p.Should().BeTrue();
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

    [Fact]
    public void A_Retransmission_After_A_Burst_Boundary_Is_Not_Swallowed_As_A_Late_Reading()
    {
        // The GB7RDG off-air capture's shape, synthetically: the same frame twice with a burst
        // boundary between, the first trailer verifying, the second corrupted so only the plain
        // reading can produce it. The late-reading guard exists for deframer skew within one
        // transmission, but its window is one maximum-length frame - over half a minute at
        // 300 baud - so before it was cleared on Reset it swallowed exactly this: the plain copy
        // of a genuine retransmission, matched byte-for-byte against the previous burst's
        // delivery. Skew cannot cross a burst boundary; a retransmission always does.
        byte[] ax25 = Ax25Frame("GB7RDG", "GB7OXF", "retransmission");
        byte[] good = FrameBits(ax25, crc: true);
        byte[] bad = (byte[])good.Clone();
        for (int i = 1; i <= 8; i++)
        {
            bad[^i] ^= 1;   // corrupt the trailing CRC, leaving the RS-protected payload intact
        }

        var frames = new List<byte[]>();
        var info = new List<Il2pDecodeInfo>();
        var delivery = new List<Il2pDelivery>();
        var receiver = new Il2pReceiver(
            (frame, decode, routing) =>
            {
                frames.Add(frame);
                info.Add(decode);
                delivery.Add(routing);
            },
            crcMode: true, acceptPlainIl2p: false);
        foreach (byte bit in good)
        {
            receiver.PushBit(bit);
        }

        receiver.Reset();   // the DCD falling edge between the two bursts
        foreach (byte bit in bad)
        {
            receiver.PushBit(bit);
        }

        receiver.Reset();   // end of the second burst releases its held plain reading

        frames.Should().HaveCount(2, "a retransmission is a new frame, not a late reading");
        info[0].CrcValid.Should().BeTrue("the first burst's trailer verifies");
        delivery[1].PlainIl2p.Should().BeTrue("the second stands on Reed-Solomon alone");
        frames[1].Should().Equal(ax25, "and it is the same bytes, which is what made it look "
            + "like a duplicate to a guard that outlived its burst");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void A_Grazed_Trailer_Corroborates_The_Frame_And_It_Is_Delivered(int damagedBits)
    {
        // The corpus's dominant failure: payload recovered byte-exact, trailer clipped by the
        // end-of-burst pulse truncation. Within CorroborationMaxBits the trailer still proves
        // the payload, so the frame is delivered even on a link that withholds plain IL2P.
        // Two bits is the floor for this path: a single flipped trailer bit never reaches it,
        // because the trailer's own Hamming layer corrects it and the CRC reading verifies.
        byte[] beacon = Gb7bpqBeacon();
        byte[] bits = FrameBits(beacon, crc: true);
        for (int i = 1; i <= damagedBits; i++)
        {
            bits[^i] ^= 1;   // clip the trailer's tail, exactly where the cliff bites
        }

        (List<byte[]> got, List<Il2pDecodeInfo> info, List<Il2pDelivery> delivery) = PushWithInfo(
            bits, crcMode: true, acceptPlainIl2p: false);

        got.Should().ContainSingle().Which.Should().Equal(beacon);
        info[0].CrcValid.Should().BeNull("the CRC as decoded did not verify");
        delivery[0].TrailerNearBits.Should().Be(damagedBits,
            "the distance is measured, and it is what corroborated the frame");
        delivery[0].MonitorOnly.Should().BeFalse(
            "a corroborated frame is CRC-strength evidence and goes to the host");
        delivery[0].PlainIl2p.Should().BeTrue("the reading itself was still the plain one");
    }

    [Fact]
    public void A_Wrecked_Trailer_Does_Not_Corroborate()
    {
        byte[] beacon = Gb7bpqBeacon();
        byte[] bits = FrameBits(beacon, crc: true);
        for (int i = 1; i <= 8; i++)
        {
            bits[^i] ^= 1;
        }

        (List<byte[]> _, List<Il2pDecodeInfo> _, List<Il2pDelivery> delivery) = PushWithInfo(
            bits, crcMode: true, acceptPlainIl2p: false);

        delivery[0].TrailerNearBits.Should().BeNull("eight of thirty-two bits is not a graze");
        delivery[0].MonitorOnly.Should().BeTrue("so the frame stays a withheld plain reading");
    }

    [Fact]
    public void A_Genuinely_Plain_Frame_Is_Not_Corroborated_By_The_Noise_Behind_It()
    {
        // A plain-IL2P station's frames are followed by whatever the channel does next, not by
        // a trailer. The corroboration check must not promote them off that noise.
        byte[] beacon = Gb7bpqBeacon();
        byte[] plainBits = FrameBits(beacon, crc: false);
        var random = new Random(7);
        byte[] bits = new byte[plainBits.Length + IdleBits];
        plainBits.CopyTo(bits, 0);
        for (int i = plainBits.Length; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        (List<byte[]> _, List<Il2pDecodeInfo> _, List<Il2pDelivery> delivery) = PushWithInfo(
            bits, crcMode: true, acceptPlainIl2p: false, idleBits: 0);

        delivery[0].TrailerNearBits.Should().BeNull(
            "random bits sit ~16 of 32 from any implied trailer");
        delivery[0].MonitorOnly.Should().BeTrue();
    }

    [Fact]
    public void A_Burst_That_Ends_Before_The_Trailer_Is_Released_Uncorroborated()
    {
        // Reset (the DCD falling edge) releases the held frame before 32 trailer bits arrived;
        // with nothing whole to measure it stays a plain reading, exactly as before.
        byte[] beacon = Gb7bpqBeacon();
        byte[] bits = FrameBits(beacon, crc: false);
        var frames = new List<byte[]>();
        var deliveries = new List<Il2pDelivery>();
        var receiver = new Il2pReceiver(
            (frame, _, routing) =>
            {
                frames.Add(frame);
                deliveries.Add(routing);
            },
            crcMode: true, acceptPlainIl2p: false);
        foreach (byte bit in bits)
        {
            receiver.PushBit(bit);
        }

        receiver.Reset();

        frames.Should().ContainSingle().Which.Should().Equal(beacon);
        deliveries[0].TrailerNearBits.Should().BeNull();
        deliveries[0].MonitorOnly.Should().BeTrue();
    }

    private static List<byte[]> Push(byte[] bits, bool crcMode, bool acceptPlainIl2p) =>
        PushWithInfo(bits, crcMode, acceptPlainIl2p).Frames;

    private static (List<byte[]> Frames, List<Il2pDecodeInfo> Info, List<Il2pDelivery> Delivery)
        PushWithInfo(byte[] bits, bool crcMode, bool acceptPlainIl2p, int? idleBits = null)
    {
        var frames = new List<byte[]>();
        var info = new List<Il2pDecodeInfo>();
        var delivery = new List<Il2pDelivery>();
        var receiver = new Il2pReceiver(
            (frame, decode, routing) =>
            {
                frames.Add(frame);
                info.Add(decode);
                delivery.Add(routing);
            },
            crcMode, acceptPlainIl2p);
        foreach (byte bit in bits)
        {
            receiver.PushBit(bit);
        }

        for (int i = 0; i < (idleBits ?? IdleBits); i++)
        {
            receiver.PushBit(0);
        }

        return (frames, info, delivery);
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
