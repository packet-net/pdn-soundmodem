using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Packet.SoundModem.Tests;

/// <summary>
/// A TCP port for a test server to listen on, that nothing else on the box can be handed while
/// the test is using it.
/// </summary>
/// <remarks>
/// <para><b>Why not the usual trick.</b> Every test that started a server used to ask the OS for a
/// port by binding port 0, reading the number back and closing the probe. The number that comes
/// back is from the ephemeral range, and so is every other port 0 bind on the box: the KISS,
/// paging and fake-rigctld servers in this suite bind port 0 and keep it, and so does every test
/// process running beside this one (CI runs two runners on one machine). Between the probe
/// closing and the server binding, any of them can be given the same number, and the server's
/// <c>Start()</c> throws "Address already in use" (#408, #546). Remembering the numbers already
/// handed out stopped this process repeating itself; it could not stop anything else.</para>
/// <para><b>What this does instead.</b> Ports come from outside
/// <c>/proc/sys/net/ipv4/ip_local_port_range</c>, which the kernel never hands to a port 0 bind or
/// to an outgoing connection, so nothing on the box can be given one by accident. That leaves
/// other test processes running this same code, so the space is cut into blocks and a process
/// claims a block by listening on its first port for as long as it runs. A second process finds
/// that port taken and moves on to the next block; the claim cannot go stale, because the kernel
/// drops it when the process exits however it exits. Each port handed out is still checked by
/// binding it, which skips the odd one a real service on the box happens to hold.</para>
/// <para>Each test class used to keep its own copy of the probe. They share this one, which is
/// what makes "never repeated" mean anything: two allocators that do not know about each other
/// collide exactly as readily as none at all.</para>
/// </remarks>
internal static class FreePorts
{
    /// <summary>Ports per claim, the first of them being the claim itself.</summary>
    private const int BlockSize = 64;

    /// <summary>The lowest port considered; below it are too many fixed, well-known services.</summary>
    private const int Floor = 10000;

    private static readonly Lock Gate = new();

    /// <summary>The sockets that hold this process's claims. Never closed: the process owns its
    /// blocks until it exits.</summary>
    private static readonly List<Socket> Claims = [];

    private static (int Low, int High)? _space;
    private static int _nextBlock = -1;
    private static int _blocksTried;
    private static int _next;
    private static int _end;

    /// <summary>A port to listen on, different from every port this process has already taken
    /// and outside anything the OS hands out by itself.</summary>
    internal static int Next()
    {
        lock (Gate)
        {
            while (true)
            {
                while (_next < _end)
                {
                    int port = _next++;
                    if (CanBind(port))
                    {
                        return port;
                    }
                }

                ClaimBlock();
            }
        }
    }

    private static void ClaimBlock()
    {
        (int low, int high) = _space ??= Space();
        int blocks = (high - low) / BlockSize;
        if (_nextBlock < 0)
        {
            // Start each process somewhere different so that several starting at once do not
            // all queue for the first block; any block is as good as any other.
            _nextBlock = Environment.ProcessId % blocks;
        }

        while (_blocksTried < blocks)
        {
            int start = low + (_nextBlock * BlockSize);
            _nextBlock = (_nextBlock + 1) % blocks;
            _blocksTried++;

            var claim = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                claim.Bind(new IPEndPoint(IPAddress.Loopback, start));
                claim.Listen(1);
            }
            catch (SocketException)
            {
                // Another test process's claim, or something real that happens to live there.
                claim.Dispose();
                continue;
            }

            Claims.Add(claim);
            _next = start + 1;
            _end = start + BlockSize;
            return;
        }

        throw new InvalidOperationException(
            $"every block of {BlockSize} ports in {low}-{high - 1} is claimed by another process "
            + $"or in use ({Claims.Count} claimed by this one)");
    }

    /// <summary>
    /// The span of ports to share out: below the kernel's ephemeral range if there is room
    /// there, else above it.
    /// </summary>
    private static (int Low, int High) Space()
    {
        (int ephemeralLow, int ephemeralHigh) = EphemeralRange();
        const int Enough = BlockSize * 16;
        if (ephemeralLow - Floor >= Enough)
        {
            return (Floor, ephemeralLow);
        }

        if (65536 - (ephemeralHigh + 1) >= Enough)
        {
            return (ephemeralHigh + 1, 65536);
        }

        throw new InvalidOperationException(
            $"the ephemeral port range {ephemeralLow}-{ephemeralHigh} leaves no room for test "
            + "servers; narrow net.ipv4.ip_local_port_range");
    }

    /// <summary>
    /// Linux's ephemeral range as configured, or the IANA dynamic range (what Windows and macOS
    /// use) where there is no such file.
    /// </summary>
    private static (int Low, int High) EphemeralRange()
    {
        const string RangeFile = "/proc/sys/net/ipv4/ip_local_port_range";
        try
        {
            string[] parts = File.ReadAllText(RangeFile)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return (int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or FormatException or IndexOutOfRangeException)
        {
            return (49152, 65535);
        }
    }

    /// <summary>Whether nothing else is listening on the port, on any address.</summary>
    private static bool CanBind(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
