using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// The test suite's port allocator hands out ports that nothing else on the box is ever given by
/// the OS, which is what keeps a test server's bind from losing a race it cannot see.
/// </summary>
public class FreePortsTests
{
    [Fact]
    public void A_Port_Is_Never_One_The_Kernel_Would_Hand_To_A_Port_0_Bind_Or_A_Connect()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the ephemeral range is read from /proc");
        string[] range = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int low = int.Parse(range[0], CultureInfo.InvariantCulture);
        int high = int.Parse(range[1], CultureInfo.InvariantCulture);

        for (int i = 0; i < 100; i++)
        {
            int port = FreePorts.Next();
            port.Should().NotBeInRange(low, high, "the kernel draws every port 0 bind and every "
                + "outgoing connection's source port from that range");
        }
    }

    [Fact]
    public void A_Port_Is_Handed_Out_Once_And_Can_Be_Listened_On()
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < 100; i++)
        {
            int port = FreePorts.Next();
            seen.Add(port).Should().BeTrue($"port {port} was handed out twice");

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
    }
}
