using System.Net;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>Whether a test can expect a listener bound to 127.0.0.1 to answer
/// <c>Host: localhost</c> on this machine.</summary>
internal static class LocalhostHost
{
    /// <summary>
    /// Skips the test where it cannot hold. Off Windows, HttpListener opens a socket per prefix
    /// on the first address the prefix's host resolves to, so "localhost" shares the 127.0.0.1
    /// socket only where it resolves to 127.0.0.1 first; a machine that puts ::1 first gets a
    /// second socket, and a request arriving on 127.0.0.1 still matches nothing.
    /// </summary>
    public static void SkipUnlessItSharesTheLoopbackSocket()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        IPAddress[] addresses = Dns.GetHostAddresses("localhost");
        Assert.SkipUnless(
            addresses.Length > 0 && addresses[0].Equals(IPAddress.Loopback),
            "localhost does not resolve to 127.0.0.1 first here");
    }
}
