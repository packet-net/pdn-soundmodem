using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The GB7BPQ case, end to end through audio: a station configured for IL2P+CRC hearing a
/// neighbour that sends plain IL2P, and the split between what it shows and what it hands on.
/// </summary>
/// <remarks>
/// <para>A station running <c>bpsk300-il2pc</c> at 2150 Hz on 7.0516 MHz had a signal survey full
/// of bursts it could not read. One capture, replayed offline through the whole 12 kHz mode
/// family, decoded as
/// <c>bpsk300-nocrc @ 2116 Hz -> 46 B GB7BPQ&gt;BEACON: =5828.54N/00612.69W- {BPQ32}</c> with the
/// carrier measured at ~2123 Hz - 27 Hz from the station's own centre and well inside its
/// diversity bank. Same bank, same centre, same audio: the <c>crc: true</c> modem did not decode
/// it and the <c>crc: false</c> one did, because that BPQ32 node sends IL2P with no trailing CRC.
/// These tests reproduce that shape by transmitting with the plain sibling of each mode and
/// receiving with the IL2P+CRC one.</para>
/// <para>The two paths tested here are the two an <see cref="IModem"/> has: the constructor frame
/// sink is what a KISS host is given, and <see cref="IModem.FrameDecoded"/> is what the display,
/// the frame log, the journal and the survey see. A plain frame reaches the second whatever the
/// configuration says, and the first only when the operator asked for it.</para>
/// </remarks>
public class Il2pPlainPassthroughTests
{
    public static TheoryData<string, string, int> ModePairs => new()
    {
        // Receiving mode (IL2P+CRC), transmitting mode (plain IL2P), DSP rate. Two families, so
        // that the seam is shown to be shared rather than bolted onto the BPSK path.
        { "bpsk300", "bpsk300-nocrc", 12000 },
        { "afsk300-il2pc", "afsk300-il2p", 12000 },
    };

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void A_Plain_Il2p_Frame_Is_Shown_And_Withheld_By_Default(
        string crcMode, string plainMode, int rate)
    {
        // The default, and the operator's own reasoning: IL2P+CRC exists because Reed-Solomon
        // alone was letting corrupt traffic through, so a frame with no CRC behind it is not
        // something his node should be given. It is still something he wants to see.
        var host = new List<byte[]>();
        var monitor = new List<FrameQuality>();
        IModem receiver = ModemCatalog.Create(crcMode, rate, host.Add);
        receiver.FrameDecoded += (_, q) => monitor.Add(q);

        receiver.Process(Transmission(plainMode, rate));

        host.Should().BeEmpty("the host asked for IL2P+CRC and gets IL2P+CRC");
        FrameQuality quality = monitor.Should().ContainSingle(
            "and the station read the burst, so it can say so").Subject;
        quality.CrcValid.Should().BeNull("there was no CRC to check");
        quality.PlainIl2p.Should().BeTrue("which is what a display badges");
        quality.MonitorOnly.Should().BeTrue("and what stops it being relayed onward");
    }

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void A_Plain_Il2p_Frame_Reaches_The_Host_Once_When_The_Modem_Is_Told_To_Accept_Them(
        string crcMode, string plainMode, int rate)
    {
        var host = new List<byte[]>();
        var monitor = new List<FrameQuality>();
        IModem receiver = ModemCatalog.Create(
            crcMode, rate, host.Add, new ModemOptions(AcceptPlainIl2p: true));
        receiver.FrameDecoded += (_, q) => monitor.Add(q);

        receiver.Process(Transmission(plainMode, rate));

        host.Should().ContainSingle("the burst the station could not read is now delivered, once")
            .Which.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
        FrameQuality quality = monitor.Should().ContainSingle().Subject;
        quality.CrcValid.Should().BeNull(
            "there was no CRC to check - the frame stands on Reed-Solomon alone, and the "
            + "frame log records that honestly rather than claiming a check that never ran");
        quality.PlainIl2p.Should().BeTrue(
            "the badge is about the decode, so it is there whether or not the option is on");
        quality.MonitorOnly.Should().BeFalse("the option is what passes it to the host");
    }

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void An_Ordinary_Il2p_Crc_Frame_Is_Still_Heard_Exactly_Once(
        string crcMode, string plainMode, int rate)
    {
        // The hazard, and now the hazard on every IL2P+CRC modem rather than the handful that
        // opted in: the modem reads the same bits both ways, and both readings decode an ordinary
        // IL2P+CRC frame. If the plain copy were emitted rather than held, every frame on the
        // channel would arrive twice - far worse than the bug being fixed. Run with the option
        // both ways, because it no longer decides whether the second reading exists. plainMode is
        // unused here; the transmission is the receiving mode's own.
        _ = plainMode;
        foreach (bool accept in (bool[])[false, true])
        {
            var host = new List<byte[]>();
            var monitor = new List<FrameQuality>();
            IModem receiver = ModemCatalog.Create(
                crcMode, rate, host.Add, new ModemOptions(AcceptPlainIl2p: accept));
            receiver.FrameDecoded += (_, q) => monitor.Add(q);

            receiver.Process(Transmission(crcMode, rate));

            host.Should().ContainSingle($"acceptPlainIl2p: {accept} must not double-deliver")
                .Which.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
            FrameQuality quality = monitor.Should().ContainSingle().Subject;
            quality.CrcValid.Should().BeTrue("the copy delivered is the one whose CRC was checked");
            quality.PlainIl2p.Should().BeFalse("a CRC stood behind this one");
            quality.MonitorOnly.Should().BeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void The_Channel_Raises_A_Withheld_Frame_On_The_Monitor_Event_Only(
        string crcMode, string plainMode, int rate)
    {
        // The same split one level up, where the daemon wires it: FrameReceived is the host path
        // (the KISS servers) and FrameReceivedWithQuality is the monitor path (waterfall, frame
        // log, journal, survey). A withheld frame appears on exactly one of them.
        var channel = new SoundModem.Channel.SoundModemChannel(rate, randomSeed: 3);
        var host = new List<byte[]>();
        var monitor = new List<FrameQuality>();
        channel.AddModem(0, sink => ModemCatalog.Create(crcMode, rate, sink));
        channel.FrameReceived += (_, frame) => host.Add(frame);
        channel.FrameReceivedWithQuality += (_, _, quality) => monitor.Add(quality);

        channel.ProcessReceive(Transmission(plainMode, rate));

        host.Should().BeEmpty();
        monitor.Should().ContainSingle().Which.MonitorOnly.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void A_Withheld_Frame_Does_Not_Dedupe_Away_The_Same_Frame_Delivered_Just_After(
        string crcMode, string plainMode, int rate)
    {
        // The trap the banks' content dedupe sets once every IL2P+CRC modem reads plain IL2P as
        // well. A burst whose trailer will not verify now produces a shown-and-withheld copy; if
        // the far end retransmits the identical frame a second later - the ordinary AX.25 answer
        // to a damaged copy - the good copy must still reach the host rather than being deduped
        // against the bad one. The dedupe window is a few seconds wide, so these two land inside
        // it, which is the point.
        var host = new List<byte[]>();
        IModem receiver = ModemCatalog.Create(crcMode, rate, host.Add);

        receiver.Process(Transmission(plainMode, rate));
        receiver.Process(Transmission(crcMode, rate));

        host.Should().ContainSingle("the verified retransmission still gets through")
            .Which.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
    }

    /// <summary>One burst of the beacon in <paramref name="mode"/>, with half a second of silence
    /// either side - the trailing silence matters, because it is what drops DCD and releases a
    /// held plain frame.</summary>
    private static float[] Transmission(string mode, int rate)
    {
        float[] burst = ModemCatalog.Create(mode, rate, static _ => { })
            .Modulate(Il2pReceiverTests.Gb7bpqBeacon(), txDelayMilliseconds: 300);
        int pad = rate / 2;
        var audio = new float[pad + burst.Length + pad];
        burst.CopyTo(audio, pad);
        return audio;
    }
}
