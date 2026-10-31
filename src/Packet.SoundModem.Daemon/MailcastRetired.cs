namespace Packet.SoundModem.Daemon;

/// <summary>
/// The notice for a configuration that still has a <c>mailcast</c> section: the built-in mailcast
/// receiver was removed (issue #588), the station starts and runs as a modem without it, and says
/// once where the receiver is now. The section is not read; its old state directory is left on
/// disk untouched.
/// </summary>
internal static class MailcastRetired
{
    /// <summary>The guide for moving a built-in receiver's settings to the standalone one.</summary>
    internal const string GuideUrl =
        "https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md";

    /// <summary>The one journal line, plain ASCII.</summary>
    internal const string Line =
        "mailcast: WARNING - the built-in mailcast receiver was removed and the \"mailcast\" section is ignored; "
        + "install pdn-mailcast-receiver (audio \"soundmodem\") instead, guide: " + GuideUrl;
}
