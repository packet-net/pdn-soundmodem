using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The GB7BPQ case, end to end through audio: a station configured for IL2P+CRC hearing a
/// neighbour that sends plain IL2P.
/// </summary>
/// <remarks>
/// A station running <c>bpsk300-il2pc</c> at 2150 Hz on 7.0516 MHz had a signal survey full of
/// bursts it could not read. One capture, replayed offline through the whole 12 kHz mode family,
/// decoded as <c>bpsk300-nocrc @ 2116 Hz -> 46 B GB7BPQ&gt;BEACON: =5828.54N/00612.69W- {BPQ32}</c>
/// with the carrier measured at ~2123 Hz - 27 Hz from the station's own centre and well inside its
/// diversity bank. Same bank, same centre, same audio: the <c>crc: true</c> modem did not decode
/// it and the <c>crc: false</c> one did, because that BPQ32 node sends IL2P with no trailing CRC.
/// These tests reproduce that shape by transmitting with the plain sibling of each mode and
/// receiving with the IL2P+CRC one.
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
    public void A_Plain_Il2p_Frame_Is_Not_Heard_By_Default(string crcMode, string plainMode, int rate)
    {
        // The behaviour on air today, and the default this must not disturb: IL2P+CRC is the
        // interop ground truth, so a frame with no CRC behind it is not a frame.
        var got = new List<byte[]>();
        IModem receiver = ModemCatalog.Create(crcMode, rate, got.Add);

        receiver.Process(Transmission(plainMode, rate));

        got.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void A_Plain_Il2p_Frame_Is_Heard_Once_When_The_Modem_Is_Told_To_Accept_Them(
        string crcMode, string plainMode, int rate)
    {
        var got = new List<byte[]>();
        var quality = new List<FrameQuality>();
        IModem receiver = ModemCatalog.Create(
            crcMode, rate, got.Add, new ModemOptions(AcceptPlainIl2p: true));
        receiver.FrameDecoded += (_, q) => quality.Add(q);

        receiver.Process(Transmission(plainMode, rate));

        got.Should().ContainSingle("the burst the station could not read is now delivered, once")
            .Which.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
        quality.Should().ContainSingle()
            .Which.CrcValid.Should().BeNull(
                "there was no CRC to check - the frame stands on Reed-Solomon alone, and the "
                + "frame log records that honestly rather than claiming a check that never ran");
    }

    [Theory]
    [MemberData(nameof(ModePairs))]
    public void An_Ordinary_Il2p_Crc_Frame_Is_Still_Heard_Exactly_Once(
        string crcMode, string plainMode, int rate)
    {
        // The hazard: with the option on, the modem is reading the same bits both ways, and both
        // readings decode an ordinary IL2P+CRC frame. If the plain copy were delivered rather than
        // held, every frame on the channel would arrive twice - far worse than the bug being
        // fixed. plainMode is unused here; the transmission is the receiving mode's own.
        _ = plainMode;
        var got = new List<byte[]>();
        var quality = new List<FrameQuality>();
        IModem receiver = ModemCatalog.Create(
            crcMode, rate, got.Add, new ModemOptions(AcceptPlainIl2p: true));
        receiver.FrameDecoded += (_, q) => quality.Add(q);

        receiver.Process(Transmission(crcMode, rate));

        got.Should().ContainSingle().Which.Should().Equal(Il2pReceiverTests.Gb7bpqBeacon());
        quality.Should().ContainSingle()
            .Which.CrcValid.Should().BeTrue("the copy delivered is the one whose CRC was checked");
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
