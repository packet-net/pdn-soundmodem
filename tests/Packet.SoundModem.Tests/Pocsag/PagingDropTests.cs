using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Pocsag;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// The paging endpoint's answer when a page cannot go out. A channel that refuses
/// synchronously (receive-only) answers <c>ERR</c> instead of a false <c>OK</c>; a page
/// that dies after <c>OK</c> (the inhibit timeout) is announced on
/// <see cref="PagingTcpServer.PageDropped"/> - it used to vanish without record.
/// </summary>
public class PagingDropTests
{
    private const int SampleRate = 12000;

    private static async Task<(TcpClient Client, StreamReader Reader)> ConnectAsync(PagingTcpServer server)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.LocalPort);
        return (client, new StreamReader(client.GetStream()));
    }

    private static async Task<string> SendAsync(TcpClient client, StreamReader reader, string line)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await client.GetStream().WriteAsync(bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("connection closed");
    }

    [Fact]
    public async Task A_Page_On_A_Receive_Only_Channel_Is_Refused_With_ERR()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7)
        {
            ReceiveOnlyReason = "this station receives only",
        };
        await using var server = new PagingTcpServer(channel, port: 0);
        server.Start();

        var (client, reader) = await ConnectAsync(server);
        using (client)
        {
            string response = await SendAsync(client, reader, "PAGE 1234567 0 TONE");
            response.Should().StartWith("ERR").And.Contain("receives only");
        }
    }

    [Fact]
    public async Task A_Page_That_Dies_Waiting_On_The_Inhibit_Raises_PageDropped()
    {
        // The channel's clock is faked so the OK-before-drop ordering is a certainty rather
        // than a race: with a real clock and a 1 ms inhibit timeout, a scheduler preemption
        // longer than 1 ms between the enqueue stamping its wait start and its first elapsed
        // check made the refusal synchronous - the server answered "ERR ... (waited 0s)"
        // instead of OK, which is exactly how this test failed on a loaded CI runner
        // (2026-08-07, run 31221914872). On the fake clock no time passes until this test
        // says so, so the enqueue always parks on its first inhibit poll and OK is sent;
        // advancing the clock then expires the timeout and raises the drop.
        var clock = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time: clock, randomSeed: 7)
        {
            TransmitInhibit = () => true,
            TransmitInhibitTimeout = TimeSpan.FromMilliseconds(1),
        };
        await using var server = new PagingTcpServer(channel, port: 0);
        var drops = new List<PageDropEvent>();
        server.PageDropped += drops.Add;
        server.Start();

        var (client, reader) = await ConnectAsync(server);
        using (client)
        {
            string response = await SendAsync(client, reader, "PAGE 1234567 0 TONE");
            response.Should().StartWith("OK ", "the refusal happens after the inhibit timeout, not at submission");

            // Past the 50 ms inhibit poll the enqueue is parked on; the next check sees the
            // 1 ms timeout long expired and faults the page.
            clock.Advance(TimeSpan.FromMilliseconds(100));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (drops.Count == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, timeout.Token);
            }

            drops.Should().ContainSingle();
            drops[0].Ric.Should().Be(1234567u);
            drops[0].Reason.Should().Contain("holding the channel");
        }
    }
}
