using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// What stood behind a station's reading of a frame, on the uplink wire.
/// </summary>
/// <remarks>
/// <para>Read at the parser rather than through a socket, for the reason
/// <see cref="UplinkWireLevelTests"/> gives: a plain synchronous fact among the whole-site tests
/// wedges its neighbours.</para>
/// <para>Three fields, and they are not the same kind of thing. <c>snrWorthShowing</c> is a
/// verdict, carried because silence from an older station must not be read as a refusal.
/// <c>trailerNearBits</c> and <c>chasedBits</c> are facts, carried because a monitor writes the
/// station's rows into its own frame log, and a row read back out of that log has to withhold the
/// callsign the live row withheld - otherwise the reported defect reappears one hop downstream,
/// on a page reload. And a callsign has no field of its own at all: where the station's decode did
/// not support naming a station, <c>from</c> and <c>to</c> are simply not sent.</para>
/// </remarks>
public class UplinkWireDecodeStandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 22, 0, 0, TimeSpan.Zero);

    /// <summary>What a station on this release sends, plus whichever fields a case adds.</summary>
    private static string Message(string extra = "") =>
        $$"""
        {"type":"frame","sub":2,"mode":"bpsk300-il2pc","from":"GB7BPQ","to":"GB7RDG-2",
         "lenBytes":21,"snrDb":9.4,"plain":true,"monitorOnly":true,{{extra}}
         "at":"2026-09-12T22:00:00.0000000+00:00"}
        """;

    private static RelayedFrame Read(string extra = "")
    {
        using JsonDocument message = JsonDocument.Parse(Message(extra));
        RelayedFrame? frame = UplinkWire.ReadFrame(message.RootElement, Now);
        frame.Should().NotBeNull("the message is well formed whatever it says about the decode");
        return frame!;
    }

    /// <summary>
    /// An old station to a new monitor: it says nothing about what stood behind the reading, and
    /// the monitor invents nothing.
    /// </summary>
    [Fact]
    public void A_Station_That_Says_Nothing_Is_Not_Read_As_Saying_No()
    {
        RelayedFrame frame = Read();

        frame.SnrDb.Should().Be(9.4, "the measurement arrives, as it always did");
        frame.SnrWorthShowing.Should().BeNull(
            "no field, no answer - and a row with no answer is listed with its figure");
        frame.TrailerNearBits.Should().BeNull();
        frame.ChasedBits.Should().BeNull();
        DecodeStanding.CallsignWorthShowing(frame.PlainIl2p, frame.TrailerNearBits, frame.ChasedBits)
            .Should().BeTrue("silence is not evidence that the chase moved anything");
    }

    [Fact]
    public void A_Stations_Own_Answer_About_Its_Snr_Crosses_The_Wire()
    {
        Read("\"snrWorthShowing\":false,").SnrWorthShowing.Should().BeFalse();
        Read("\"snrWorthShowing\":true,").SnrWorthShowing.Should().BeTrue();
        Read("\"snrWorthShowing\":false,").SnrDb.Should().Be(
            9.4, "the measurement crosses either way, because a monitor logs it");
    }

    /// <summary>
    /// And the two facts behind it, which are what a monitor's own copy of the log needs.
    /// </summary>
    [Fact]
    public void What_Stood_Behind_The_Reading_Crosses_As_Facts()
    {
        RelayedFrame fabricated = Read("\"chasedBits\":6,");
        fabricated.ChasedBits.Should().Be(6);
        DecodeStanding.CallsignWorthShowing(
                fabricated.PlainIl2p, fabricated.TrailerNearBits, fabricated.ChasedBits)
            .Should().BeFalse("Reed-Solomon alone, reached by moving bits, names nobody here either");

        RelayedFrame corroborated = Read("\"chasedBits\":6,\"trailerNearBits\":2,");
        corroborated.TrailerNearBits.Should().Be(2);
        DecodeStanding.CallsignWorthShowing(
                corroborated.PlainIl2p, corroborated.TrailerNearBits, corroborated.ChasedBits)
            .Should().BeTrue("a near-exact trailer is evidence of the same order as a passing CRC");
    }

    /// <summary>
    /// A new station to an old monitor: the fields are inert to a reader that ignores them.
    /// </summary>
    [Fact]
    public void Adding_Them_Changes_Nothing_A_Reader_That_Ignores_Them_Can_See()
    {
        RelayedFrame told = Read("\"snrWorthShowing\":false,\"chasedBits\":6,\"trailerNearBits\":2,");
        RelayedFrame silent = Read();

        (told with { SnrWorthShowing = null, ChasedBits = null, TrailerNearBits = null })
            .Should().Be(silent);
    }
}
