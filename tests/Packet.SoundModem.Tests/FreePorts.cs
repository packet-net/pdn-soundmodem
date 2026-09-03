using System.Net;
using System.Net.Sockets;

namespace Packet.SoundModem.Tests;

/// <summary>
/// A TCP port nothing else in this process has been given.
/// </summary>
/// <remarks>
/// <para>Every test that starts a server has always asked the OS for one by binding port 0,
/// reading the number back and closing the socket. That is the usual trick and it has a hole in
/// it: the port goes straight back into the ephemeral pool the moment the probe closes, so two
/// tests asking a moment apart can be handed the same number, and two <c>HttpListener</c>s in one
/// process on one port end with "Address already in use" thrown from <c>Close</c> - reported
/// against whichever of the two happened to be disposing at the time, which is never the one that
/// caused it.</para>
/// <para>So the numbers handed out are remembered and never repeated. The residual race - another
/// process taking the port between the probe closing and the server binding - is the one the probe
/// trick has always had and is not made worse by this.</para>
/// <para>Each test class used to keep its own copy of the probe. They now share this one, which is
/// what makes "never repeated" mean anything: two allocators that do not know about each other
/// collide exactly as readily as none at all.</para>
/// </remarks>
internal static class FreePorts
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<int> Handed = [];

    /// <summary>A port to listen on, different from every port this process has already taken.</summary>
    internal static int Next()
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                if (Handed.Add(port))
                {
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"could not find a port the ephemeral range has not already given this process "
                + $"({Handed.Count} taken so far)");
        }
    }
}
