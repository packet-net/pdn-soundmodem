using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Loopback cover for the M0LTE.Il2p 0.1.1 fix: the IL2P+CRC trailer is computed over
/// the original AX.25 frame, but Type 1 header translation is lossy (single C bit, PID
/// canonicalisation), and receivers check the CRC against their reconstruction - so a
/// v1-style both-C-bits-clear UI frame encoded as Type 1 failed the CRC at any
/// conforming receiver and was silently dropped. Under 0.1.0 this killed the whole
/// il2pc TX self-loopback for such frames (measured 0/N at every TXDELAY and length,
/// 2026-07-31) while real NinoTNC captures - the TNC transmits Type 0 throughout -
/// decoded fine. 0.1.1 falls back to Type 0 whenever the header does not round-trip
/// byte-exactly under an appended CRC, which is byte-exact under either convention.
/// </summary>
public class Il2pCrcDegenerateHeaderTests
{
    [Theory]
    [InlineData("c4fsk9600", 48000)]
    [InlineData("c4fsk19200", 48000)]
    [InlineData("afsk300-il2pc", 12000)]
    public void Both_C_Bits_Clear_Frames_Survive_The_Il2pc_Loopback(string mode, int rate)
    {
        // QST > M0LTE-1 UI with both C bits clear - the exact framing style
        // nino_capture.py transmits and the wire style observed from a real NinoTNC.
        byte[] frame =
        [
            0xA2, 0xA6, 0xA8, 0x40, 0x40, 0x40, 0x60,
            0x9A, 0x60, 0x98, 0xA8, 0x8A, 0x40, 0x63,
            0x03, 0xF0,
            .. System.Text.Encoding.ASCII.GetBytes("degenerate-C loopback probe"),
        ];

        IModem tx = NinoTncParityTests.Create(mode, rate, _ => { });
        var audio = new List<float>();
        audio.AddRange(new float[rate / 2]);
        audio.AddRange(tx.Modulate(frame, 120));
        audio.AddRange(new float[rate / 2]);

        var got = new List<byte[]>();
        IModem rx = NinoTncParityTests.Create(mode, rate, got.Add);
        rx.Process(audio.ToArray());

        got.Should().ContainSingle(
            "a both-C-bits-clear frame must survive the {0} IL2P+CRC loopback byte-exactly", mode);
        got[0].Should().Equal(frame, "Type 0 transparent encapsulation reproduces the frame byte-for-byte");
    }
}
