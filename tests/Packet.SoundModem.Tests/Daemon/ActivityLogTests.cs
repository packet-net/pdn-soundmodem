using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The per-frame lines a running station writes to the journal.
/// </summary>
/// <remarks>
/// `journalctl -u pdn-soundmodem -f` is the only view of a station most operators use - the
/// waterfall needs a browser and the frame log needs SQL. These lines end up in grep pipelines and
/// bug reports, so the text is an interface and is pinned here rather than left to interpolation
/// nobody reads.
/// </remarks>
public class ActivityLogTests
{
    /// <summary>An AX.25 UI frame from M0LTE to GB7RDG-2, as the modems deliver one.</summary>
    private static byte[] Frame(string source = "M0LTE", int sourceSsid = 0,
                               string destination = "GB7RDG", int destSsid = 2)
    {
        var f = new byte[20];
        Write(f, 0, destination, destSsid, last: false);
        Write(f, 7, source, sourceSsid, last: true);
        f[14] = 0x03;
        f[15] = 0xF0;
        return f;

        static void Write(byte[] f, int at, string call, int ssid, bool last)
        {
            for (int i = 0; i < 6; i++)
            {
                f[at + i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            f[at + 6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
        }
    }

    [Fact]
    public void A_Received_Frame_Names_The_Station_The_Mode_And_How_Well_It_Decoded()
    {
        string line = ActivityLog.Received(
            0, Frame(), new FrameQuality("afsk300-il2pc", 20, CorrectedBytes: 0, CrcValid: true,
                                         FrequencyOffsetHz: -35));

        // "rx[0] afsk300-il2pc M0LTE>GB7RDG-2 20 bytes  crc ok  fec 0  -35 Hz"
        line.Should().StartWith("rx[0] afsk300-il2pc M0LTE>GB7RDG-2 20 bytes");
        line.Should().Contain("crc ok").And.Contain("fec 0").And.Contain("-35 Hz");
    }

    [Fact]
    public void The_Burst_Strength_Rides_The_Line_When_It_Was_Measured()
    {
        string line = ActivityLog.Received(
            0, Frame(), new FrameQuality("bpsk300", 20, CorrectedBytes: 0, CrcValid: true,
                                         FrequencyOffsetHz: 4, SnrDb: 19.3));

        // "rx[0] bpsk300 M0LTE>GB7RDG-2 20 bytes  crc ok  fec 0  snr 19.3 dB  +4 Hz"
        line.Should().Contain("snr 19.3 dB");
    }

    [Fact]
    public void A_Frame_With_No_Measured_Burst_Says_Nothing_About_Snr()
    {
        string line = ActivityLog.Received(
            0, Frame(), new FrameQuality("bpsk300", 20, CorrectedBytes: 0, CrcValid: true));

        line.Should().NotContain("snr", "an unmeasured figure must be absent, not zero");
    }

    [Fact]
    public void A_Bad_Crc_Is_Shouted_Rather_Than_Mentioned()
    {
        // The difference between "a frame" and "probably a frame" is the one thing an operator
        // scanning a journal must not miss.
        string line = ActivityLog.Received(
            1, Frame(), new FrameQuality("bpsk300", 20, CorrectedBytes: 3, CrcValid: false));

        line.Should().Contain("CRC BAD");
        line.Should().NotContain("crc ok");
    }

    [Fact]
    public void A_Mode_With_No_Crc_To_Check_Claims_Neither_Verdict()
    {
        string line = ActivityLog.Received(
            0, Frame(), new FrameQuality("afsk1200", 20, CorrectedBytes: null, CrcValid: null));

        line.Should().NotContain("crc").And.NotContain("CRC");
        line.Should().NotContain("fec", "no FEC ran, so a correction count would be an invention");
    }

    [Fact]
    public void A_Transmitted_Frame_Is_Logged_At_All()
    {
        // The gap this closes: the transmit side had only a rejection event, so a station's
        // journal recorded every frame it failed to send and none of the ones it sent.
        string line = ActivityLog.Transmitted(2, "qpsk2400", Frame(sourceSsid: 7));

        line.Should().Be("tx[2] qpsk2400 M0LTE-7>GB7RDG-2 20 bytes");
    }

    [Fact]
    public void A_Dropped_Frame_Says_Which_Frame_And_Why()
    {
        string line = ActivityLog.Dropped(
            0, Frame(), new InvalidOperationException("this station receives only"));

        line.Should().StartWith("tx[0] DROPPED M0LTE>GB7RDG-2 20 bytes: ");
        line.Should().EndWith("this station receives only");
    }

    [Fact]
    public void A_Frame_That_Is_Not_Ax25_Says_So_Instead_Of_Inventing_A_Callsign()
    {
        // A KISS host may send anything, and several modes carry payloads that are not AX.25 at
        // all. A mangled callsign would be worse than admitting there is not one.
        string line = ActivityLog.Received(
            0, new byte[] { 1, 2, 3 }, new FrameQuality("fsk9600", 3, null, null));

        line.Should().Contain("(no ax25 header)");
    }

    [Fact]
    public void A_Frame_That_Decoded_And_Would_Not_Yield_Callsigns_Says_Why()
    {
        // The line that used to raise a question instead of answering one. This is the live 40 m
        // case: a 118-byte bpsk300 frame, CRC-valid and zero corrections, whose payload is not an
        // AX.25 address field - so the bits are right and the reading of them is not. Diagnosing
        // one of these meant pulling the payload blob out of the frame log by hand.
        byte[] frame = [0x00, 0x01, 0x02, 0x03, .. new byte[114]];

        string line = ActivityLog.Received(
            2,
            frame,
            new FrameQuality(
                "bpsk300-il2pc-multi9", 118, 0, true,
                FrequencyOffsetHz: -13, HeaderType: M0LTE.Il2p.Il2pHeaderType.Type1));

        line.Should().Contain("(no ax25 header)")
            .And.Contain("il2p Type1", "which encapsulation carried it is the first question")
            .And.Contain("byte 0")
            .And.Contain("destination");
    }

    [Fact]
    public void An_Ordinary_Frame_Carries_No_Attribution_Note()
    {
        // The note is for the frames that need explaining. Every other line stays as it was -
        // these end up in other people's grep pipelines.
        string line = ActivityLog.Received(
            0, Frame(), new FrameQuality("bpsk300-il2pc", 20, 0, true));

        line.Should().Be("rx[0] bpsk300-il2pc M0LTE>GB7RDG-2 20 bytes  crc ok  fec 0");
    }

    [Fact]
    public void A_Plain_Il2p_Frame_Says_So_And_Says_Whether_The_Host_Got_It()
    {
        // A row on an -il2pc modem with nothing but Reed-Solomon behind it, which the mode name
        // beside it flatly contradicts. The second half is worth as much as the first: a frame
        // the station showed and withheld is a different event from a delivery, and the journal
        // is the only place most operators will ever see either.
        string withheld = ActivityLog.Received(
            0, Frame(), new FrameQuality(
                "bpsk300-il2pc-multi9", 46, 0, null, PlainIl2p: true, MonitorOnly: true));
        string delivered = ActivityLog.Received(
            0, Frame(), new FrameQuality(
                "bpsk300-il2pc-multi9", 46, 0, null, PlainIl2p: true, MonitorOnly: false));

        withheld.Should().Contain("plain il2p (rs only, not passed to host)");
        delivered.Should().Contain("plain il2p (rs only)")
            .And.NotContain("not passed to host");
        withheld.Should().NotContain("crc ok").And.NotContain("CRC BAD",
            "there was no CRC to check, and claiming either verdict would be a lie");
    }

    [Fact]
    public void An_Ordinary_Frame_Says_Nothing_About_Plain_Il2p()
    {
        // The note is for the frames that need it. Everything else keeps the line it had.
        string line = ActivityLog.Received(
            0, Frame(), new FrameQuality("bpsk300-il2pc", 20, 0, true));

        line.Should().NotContain("plain il2p").And.NotContain("rs only");
    }

    [Fact]
    public void An_Untwisted_Signal_Does_Not_Report_Emphasis()
    {
        string quiet = ActivityLog.Received(
            0, Frame(), new FrameQuality("afsk1200-multi", 20, 0, true, EmphasisDb: 0));
        string twisted = ActivityLog.Received(
            0, Frame(), new FrameQuality("afsk1200-multi", 20, 0, true, EmphasisDb: -6));

        quiet.Should().NotContain("emph", "zero emphasis is the normal case and is noise in a log");
        twisted.Should().Contain("emph -6 dB");
    }

    [Fact]
    public void A_Kiss_Client_Line_Says_Which_Port_Which_Host_And_What_That_Port_Reaches()
    {
        var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.50"), 54312);

        string shared = ActivityLog.ClientConnected(8105, null, new KissClientEvent(remote, 2));
        string dedicated = ActivityLog.ClientConnected(8101, 3, new KissClientEvent(remote, 1));

        shared.Should().Be("kiss[8105] 192.168.1.50:54312 connected - 2 clients (all modems)");
        // Which modems a port reaches is the thing host operators get wrong, so it is on every line.
        dedicated.Should().Be("kiss[8101] 192.168.1.50:54312 connected - 1 client (modem 3 only)");
    }

    [Fact]
    public void A_Host_That_Vanished_Reads_Differently_From_One_That_Said_Goodbye()
    {
        var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 40000);

        string clean = ActivityLog.ClientDisconnected(8105, null, new KissClientEvent(remote, 0));
        string reset = ActivityLog.ClientDisconnected(
            8105, null, new KissClientEvent(remote, 0, "Connection reset by peer."));

        clean.Should().Be("kiss[8105] 127.0.0.1:40000 disconnected - 0 clients (all modems)");
        reset.Should().Contain("disconnected: Connection reset by peer.");
    }

    [Fact]
    public void A_Host_Whose_Socket_Had_Already_Gone_Is_Still_Reported()
    {
        // Better an unnamed host than a swallowed disconnect: the count is the part that matters.
        string line = ActivityLog.ClientDisconnected(8105, null, new KissClientEvent(null, 0));

        line.Should().Contain("(unknown host)").And.Contain("0 clients");
    }
}
