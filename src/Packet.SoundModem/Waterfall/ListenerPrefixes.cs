using System.Net;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// The HttpListener prefixes for a page on one bind address and port, shared by
/// <see cref="WaterfallWebServer"/> and <see cref="WaterfallRouter"/>.
/// </summary>
internal static class ListenerPrefixes
{
    /// <summary>The prefixes to register for <paramref name="bind"/> and
    /// <paramref name="port"/> on this machine.</summary>
    /// <param name="bind">Bind address; "*" listens on all interfaces.</param>
    /// <param name="port">HTTP listen port.</param>
    public static IReadOnlyList<string> For(string bind, int port) =>
        For(bind, port, OperatingSystem.IsWindows(), FirstLocalhostAddress);

    /// <summary>The prefixes, with the platform and the name lookup handed in.</summary>
    /// <param name="bind">Bind address; "*" listens on all interfaces.</param>
    /// <param name="port">HTTP listen port.</param>
    /// <param name="matchesHostOnly">True where HttpListener is http.sys (Windows), which
    /// matches a prefix on the Host header alone and opens no socket of its own for it.</param>
    /// <param name="firstLocalhostAddress">The first address "localhost" resolves to, or null
    /// if it does not resolve.</param>
    internal static IReadOnlyList<string> For(
        string bind, int port, bool matchesHostOnly, Func<IPAddress?> firstLocalhostAddress)
    {
        // HttpListener wants "+" for "every interface" and rejects the literal 0.0.0.0 that a
        // TcpListener is perfectly happy with - the daemon uses one bind setting for both, so
        // translate here rather than make the operator know which listener wants which spelling.
        if (bind is "*" or "0.0.0.0" or "::" or "[::]")
        {
            return [$"http://+:{port}/"];
        }

        string bound = $"http://{bind}:{port}/";
        if (!IPAddress.TryParse(bind.Trim('[', ']'), out IPAddress? address)
            || !IPAddress.IsLoopback(address))
        {
            return [bound];
        }

        // HttpListener matches a request's Host header against its prefixes, so a page bound to
        // 127.0.0.1 answered http://localhost:PORT/ - the obvious URL, and what an SSH tunnel
        // opened with -L PORT:localhost:PORT is browsed as - with its own 404 before any route was
        // looked at. Off Windows, though, the managed HttpListener opens a socket for each prefix
        // on the first address its host resolves to, and matches only the prefixes on the socket
        // a request arrived on. So "localhost" is added only where it lands on the bound socket:
        // where it resolves to ::1 first it would open a second socket, still leave a request
        // arriving on 127.0.0.1 unmatched, and fail start-up outright on a host with IPv6 off.
        if (matchesHostOnly || address.Equals(firstLocalhostAddress()))
        {
            return [bound, $"http://localhost:{port}/"];
        }

        return [bound];
    }

    private static IPAddress? FirstLocalhostAddress()
    {
        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses("localhost");
            return addresses.Length > 0 ? addresses[0] : null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}
