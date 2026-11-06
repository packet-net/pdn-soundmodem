using System.Net;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The configuration page, at <c>/config</c> on the station page's listener.
/// </summary>
/// <remarks>
/// A page of its own rather than a pane of the station page: the station page is something left
/// open on a wall, and this is something opened to change the station and closed again. It has no
/// list of settings in it. It reads <c>api/config/schema</c> (see <see cref="ConfigSchema"/>) and
/// draws a field for every key there, so it is whole for any version of the daemon that serves it.
/// </remarks>
internal static class ConfigPage
{
    /// <summary>Where the page is served, relative to the listener's base.</summary>
    public const string Path = "/config";

    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using Stream stream = typeof(ConfigPage).Assembly.GetManifestResourceStream("config.html")
            ?? throw new InvalidOperationException("config.html is not embedded in the daemon");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    /// <summary>The page, as served.</summary>
    internal static byte[] Bytes => Page.Value;

    public static async Task ServeAsync(HttpListenerContext context)
    {
        byte[] page = Page.Value;
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        // As the station page: no heuristic caching, so an upgraded station serves its own page.
        context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        context.Response.ContentLength64 = page.Length;
        if (context.Request.HttpMethod != "HEAD")
        {
            await context.Response.OutputStream.WriteAsync(page).ConfigureAwait(false);
        }

        context.Response.Close();
    }
}
