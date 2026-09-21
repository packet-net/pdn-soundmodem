using Packet.SoundModem.Channel;
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

    /// <summary>
    /// A transmission the channel sat on says so, and one that went straight out stays quiet.
    /// </summary>
    /// <remarks>
    /// The wait is invisible to the host that queued the frame: a KISS write returns as soon as
    /// the socket takes it, so a station whose carrier sense held a UA for 3 m 48 s (GB7RDG-2,
    /// 2026-09-21) looked to LinBPQ exactly like one that sent it instantly. The journal is where
    /// an operator finds out otherwise, so the wording is pinned here like the rest of the line.
    /// </remarks>
    [Fact]
    public void A_Transmission_Carrier_Sense_Held_Says_How_Long()
    {
        string held = ActivityLog.Transmitted(
            2, "bpsk300", Frame(sourceSsid: 7), heldFor: TimeSpan.FromSeconds(228));

        held.Should().Be(
            "tx[2] bpsk300 M0LTE-7>GB7RDG-2 20 bytes  held 3m48s waiting for the channel");

        // Under a minute reads in seconds, because that is the resolution a channel-access
        // problem is argued in.
        ActivityLog.Transmitted(2, "bpsk300", Frame(sourceSsid: 7), heldFor: TimeSpan.FromSeconds(4.25))
            .Should().EndWith("held 4.3s waiting for the channel");

        // And a frame that went out promptly says nothing, so the note means something when it
        // does appear rather than being a column of "held 0.0s" on every line.
        ActivityLog.Transmitted(2, "bpsk300", Frame(sourceSsid: 7), heldFor: TimeSpan.FromMilliseconds(40))
            .Should().Be("tx[2] bpsk300 M0LTE-7>GB7RDG-2 20 bytes");

        // ASCII only: this goes to the journal, whose pager runs under a C locale.
        held.Should().MatchRegex("^[\\x20-\\x7E]*$");
    }

    /// <summary>
    /// The note names what took most of the wait, so that the two readings of one number can be
    /// told apart on the line rather than by opening the frame log.
    /// </summary>
    /// <remarks>
    /// GB7RDG-2's 8.3 s row on 2026-09-21 was the third frame of a MAXFRAME=3 window, 1.5 s of
    /// channel access and 6.8 s of this station's own two earlier bursts: a station working
    /// normally. An 8.3 s row that is somebody else occupying the frequency is the same number and
    /// wants a different response, and until the channel measured the split neither line could say
    /// which it was.
    /// </remarks>
    [Fact]
    public void A_Held_Transmission_Names_What_Took_The_Time()
    {
        string ours = ActivityLog.Transmitted(
            2, "bpsk300", Frame(sourceSsid: 7), heldFor: TimeSpan.FromMilliseconds(8281),
            waits: new TransmitWaits
            {
                Total = TimeSpan.FromMilliseconds(8281),
                ChannelBusy = TimeSpan.FromMilliseconds(1476),
                OurTransmission = TimeSpan.FromMilliseconds(6805),
            });

        ours.Should().Be(
            "tx[2] bpsk300 M0LTE-7>GB7RDG-2 20 bytes  held 8.3s (6.8s behind our own transmissions)");

        string theirs = ActivityLog.Transmitted(
            2, "bpsk300", Frame(sourceSsid: 7), heldFor: TimeSpan.FromMilliseconds(8281),
            waits: new TransmitWaits
            {
                Total = TimeSpan.FromMilliseconds(8281),
                ChannelBusy = TimeSpan.FromMilliseconds(8100),
                BusySubChannels = 1,
                BusiestSubChannel = 0,
            });

        theirs.Should().Be(
            "tx[2] bpsk300 M0LTE-7>GB7RDG-2 20 bytes  held 8.3s (8.1s channel busy on ch0)");

        // No one cause worth naming: the line says how long and stops, rather than picking the
        // largest of six numbers that between them mean "ordinary channel access".
        ActivityLog.Transmitted(
                2, "bpsk300", Frame(sourceSsid: 7), heldFor: TimeSpan.FromSeconds(4),
                waits: new TransmitWaits
                {
                    Total = TimeSpan.FromSeconds(4),
                    ChannelBusy = TimeSpan.FromSeconds(1.4),
                    Backoff = TimeSpan.FromSeconds(1.3),
                    OurTransmission = TimeSpan.FromSeconds(1.3),
                })
            .Should().EndWith("held 4.0s waiting for the channel");

        // ASCII only, both ways round: this goes to the journal.
        ours.Should().MatchRegex("^[\\x20-\\x7E]*$");
        theirs.Should().MatchRegex("^[\\x20-\\x7E]*$");
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
    public void An_Ardop_Receive_Names_The_Frame_Type_The_Callsigns_And_The_Quality()
    {
        string line = ActivityLog.ArdopReceived(
            2, "IDFrame", "GB7NOT-2", null, 0, decodedOk: true, quality: 78, snDb: null);

        line.Should().Be("rx[2] ardop IDFrame GB7NOT-2>? 0 bytes  crc ok  q 78");
    }

    [Fact]
    public void An_Ardop_Receive_With_No_Callsign_Says_So_Rather_Than_Inventing_One()
    {
        // A data frame belonging to someone else's session carries no callsign at all - not
        // even the marker's own AX.25 address field to fall back on, because ARDOP is not AX.25.
        string line = ActivityLog.ArdopReceived(
            2, "0FEC64", null, null, 12, decodedOk: false, quality: 41, snDb: null);

        line.Should().Contain("(no callsign)");
        line.Should().NotMatch("*>*", "a marker that reads like a callsign pair is worse than none");
    }

    [Fact]
    public void An_Ardop_Receives_Signal_To_Noise_Is_Shown_Only_When_Ardop_Actually_Measured_One()
    {
        // The live defect: ArdopDecodedFrame.SnDb is 0 on every frame that is not a Ping, and
        // passing it straight through read as a station on the edge of the noise beside a frame
        // that decoded perfectly (#479).
        string ping = ActivityLog.ArdopReceived(
            2, "Ping", "M0LTE", "GB7RDG", 12, decodedOk: true, quality: 90, snDb: -3.0);
        string idFrame = ActivityLog.ArdopReceived(
            2, "IDFrame", "GB7NOT", null, 0, decodedOk: true, quality: 78, snDb: null);

        ping.Should().Contain("sn -3.0 dB");
        idFrame.Should().NotContain("sn", "IDFrame never measures one, and 0 dB would be a claim");
    }

    [Fact]
    public void An_Ardop_Transmission_Names_The_Frame_Type_And_The_Callsigns()
    {
        string line = ActivityLog.ArdopTransmitted(2, "ConReq500M", "M0LTE", "GB7RDG", 0);

        line.Should().Be("tx[2] ardop ConReq500M M0LTE>GB7RDG 0 bytes");
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
