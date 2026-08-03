using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// Who is attached to a KISS port, and when they left.
/// </summary>
/// <remarks>
/// A host that quietly drops its TCP session stops passing traffic, and from the modem's side that
/// is indistinguishable from a band that went quiet — the station keeps no other record of it. The
/// reason matters as much as the event: "the host closed" and "the host vanished" are different
/// problems with different fixes.
/// </remarks>
public class KissClientEventTests
{
    private static SoundModemChannel Channel()
    {
        var channel = new SoundModemChannel(12000, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", 12000, sink));
        return channel;
    }

    [Fact]
    public async Task A_Host_Attaching_Is_Announced_With_Its_Address_And_The_Client_Count()
    {
        await using var server = new KissTcpServer(Channel(), 0, IPAddress.Loopback);
        var connects = new List<KissClientEvent>();
        server.ClientConnected += connects.Add;
        server.Start();

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        await WaitFor(() => connects.Count == 1);

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        await WaitFor(() => connects.Count == 2);

        connects[0].Remote.Should().NotBeNull();
        connects[0].Clients.Should().Be(1);
        connects[1].Clients.Should().Be(2, "the count is what is attached now, not a running total");
        connects.Should().AllSatisfy(e => e.Reason.Should().BeNull("a connect has no reason"));
    }

    [Fact]
    public async Task A_Clean_Close_Is_Announced_Without_A_Reason()
    {
        await using var server = new KissTcpServer(Channel(), 0, IPAddress.Loopback);
        var goodbyes = new List<KissClientEvent>();
        server.ClientDisconnected += goodbyes.Add;
        server.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        client.Close();

        await WaitFor(() => goodbyes.Count == 1);
        goodbyes[0].Reason.Should().BeNull("an orderly close is not a fault and must not read like one");
        goodbyes[0].Clients.Should().Be(0);
        goodbyes[0].Remote.Should().NotBeNull("the address is captured before the socket is disposed");
    }

    [Fact]
    public async Task A_Host_That_Vanishes_Is_Announced_With_Why()
    {
        await using var server = new KissTcpServer(Channel(), 0, IPAddress.Loopback);
        var goodbyes = new List<KissClientEvent>();
        server.ClientDisconnected += goodbyes.Add;
        server.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        await Task.Delay(100);
        // Abort rather than close: the host process dying, not saying goodbye. Set on the socket
        // itself — LingerState on the TcpClient wrapper does not reach it before Close, and the
        // server then sees an orderly EOF instead of the reset this is about.
        client.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
        client.Client.Close();

        await WaitFor(() => goodbyes.Count == 1);
        goodbyes[0].Reason.Should().NotBeNullOrEmpty(
            "'the host vanished' and 'the host closed' are different problems");
    }

    [Fact]
    public async Task Shutting_The_Server_Down_Is_Not_Reported_As_A_Client_Fault()
    {
        var server = new KissTcpServer(Channel(), 0, IPAddress.Loopback);
        var goodbyes = new List<KissClientEvent>();
        server.ClientDisconnected += goodbyes.Add;
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        await Task.Delay(100);

        await server.DisposeAsync();

        await WaitFor(() => goodbyes.Count >= 1);
        goodbyes[0].Reason.Should().BeNull(
            "the station stopping is not the host misbehaving, and a journal full of errors on a "
            + "clean shutdown teaches operators to ignore them");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue("the event should have arrived");
    }
}
