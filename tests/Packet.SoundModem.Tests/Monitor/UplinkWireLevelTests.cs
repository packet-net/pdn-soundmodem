using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// The frame level verdict on the uplink wire, read at the parser rather than through a socket.
/// </summary>
/// <remarks>
/// <para>Its own class deliberately. <see cref="UplinkTests"/> is a collection of whole sites on
/// real ports with a fake clock, and every test in it carries a timeout; a plain synchronous fact
/// sitting among them was measured to wedge one of its neighbours for thirty seconds a run. This
/// asks the same questions of <see cref="UplinkWire.ReadFrame"/> directly, which is where the
/// answers actually live.</para>
/// <para>Both directions of the version skew, which is the point of carrying a verdict at all. A
/// monitor has no thresholds of its own any more: they belong to the modem that decoded the
/// frame, and that modem is at the other end of the uplink.</para>
/// </remarks>
public class UplinkWireLevelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    /// <summary>What a station on this release sends, minus whichever fields a case leaves out.</summary>
    private static string Message(string? level) =>
        $$"""
        {"type":"frame","sub":0,"mode":"c4fsk19200-il2pc","from":"M0LTE","to":"GB7RDG-2",
         "lenBytes":15,"snrDb":12.5,"crc":true,"peakDbFs":-4.0,"clipped":false,
         {{(level is null ? "" : $"\"level\":\"{level}\",")}}
         "at":"2026-09-07T10:00:00.0000000+00:00"}
        """;

    private static RelayedFrame Read(string? level)
    {
        using JsonDocument message = JsonDocument.Parse(Message(level));
        RelayedFrame? frame = UplinkWire.ReadFrame(message.RootElement, Now);
        frame.Should().NotBeNull("the message is well formed whatever the verdict is");
        return frame!;
    }

    /// <summary>
    /// An old station to a new monitor: the measurements arrive, the verdict does not, and the
    /// row carries no verdict rather than one this site invented.
    /// </summary>
    /// <remarks>
    /// v0.60.x measures a frame's level and has no verdict to send, because until this change the
    /// rule was applied at the far end's page and at this one's. -4 dBFS on a C4FSK mode is two
    /// dB inside that family's loud edge, so a site still holding a copy of the rule would badge
    /// this row; a site that has given the rule back to the modems cannot, and must not guess.
    /// </remarks>
    [Fact]
    public void A_Station_That_Sends_No_Verdict_Gets_No_Verdict_Here()
    {
        RelayedFrame frame = Read(level: null);

        frame.PeakDbFs.Should().Be(-4, "what it measured is a measurement and still arrives");
        frame.Clipped.Should().BeFalse();
        frame.Level.Should().BeNull(
            "and what it made of that is the part only its own modem could supply");
    }

    /// <summary>A station on this release: the verdict is carried, not recomputed.</summary>
    [Fact]
    public void A_Stations_Own_Verdict_Crosses_The_Wire()
    {
        Read("loud").Level.Should().Be(FrameLevel.Loud);
        Read("quiet").Level.Should().Be(FrameLevel.Quiet);
        Read("ok").Level.Should().Be(
            FrameLevel.Ok, "measured and fine, which is not the same as not measured");
    }

    /// <summary>
    /// A station one release ahead, sending a verdict this build has never heard of, is listed
    /// with no badge rather than dropped.
    /// </summary>
    [Fact]
    public void A_Verdict_This_Build_Does_Not_Know_Reads_As_None()
    {
        RelayedFrame frame = Read("deafening");

        frame.Level.Should().BeNull();
        frame.PeakDbFs.Should().Be(-4, "the rest of the row is untouched by a word it cannot use");
    }

    /// <summary>
    /// A new station to an old monitor: the field is inert to a reader that ignores it.
    /// </summary>
    /// <remarks>
    /// A v0.60.x monitor runs this same parser minus the one line that looks at <c>level</c>, so
    /// what has to be true is that its presence changes nothing else. Read the identical message
    /// with and without it and compare everything but the verdict, which is exactly what such a
    /// monitor would have got.
    /// </remarks>
    [Fact]
    public void Adding_The_Verdict_Changes_Nothing_A_Reader_That_Ignores_It_Can_See()
    {
        RelayedFrame withVerdict = Read("loud");
        RelayedFrame without = Read(level: null);

        (withVerdict with { Level = null }).Should().Be(without);
    }
}
