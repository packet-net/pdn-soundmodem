namespace Packet.SoundModem.Waterfall;

/// <summary>
/// The picker: the front page of a site that offers several receivers, listing what the daemon
/// knows about each of them and linking to its page.
/// </summary>
/// <remarks>
/// <para>Served by the host rather than by <see cref="WaterfallWebServer"/>, because a picker is
/// about a list of receivers and a waterfall server is about one receiver's audio. It ships here
/// beside the page it links to, embedded in the same assembly and stamped the same way, because
/// the two are one design and are edited together.</para>
/// <para>Self-contained: one file, no framework, no build step, no external assets. It reads
/// <c>/api/instances</c> relative to its own path, so it works at the root of a site and behind
/// whatever a tunnel puts in front of it.</para>
/// </remarks>
public static class MonitorPage
{
    private static readonly Lazy<(byte[] Bytes, string Version)> Loaded =
        new(() => EmbeddedPage.Load("monitor.html"));

    /// <summary>The page as served, UTF-8, with its version stamped in.</summary>
    public static byte[] Bytes => Loaded.Value.Bytes;

    /// <summary>The version written into the page: a hash of its own text. A snapshot carries
    /// the same string, and a tab running an older one reloads itself.</summary>
    public static string Version => Loaded.Value.Version;
}
