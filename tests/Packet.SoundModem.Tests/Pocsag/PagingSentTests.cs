using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Pocsag;
using Packet.SoundModem.Tests.Channel;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// The paging endpoint's record of what it actually put on the air.
/// </summary>
/// <remarks>
/// A page goes through the channel's audio path rather than as an addressed frame, so none of it
/// raises <c>FrameTransmitted</c> and nothing outside this server could tell that a page had been
/// transmitted at all: a station that had paged all day answered "what did I put on the air" with
/// an empty table (issue #473). <see cref="PagingTcpServer.PageSent"/> is what a journal writes
/// that record from, and it fires on completion so that the thing written down is a transmission
/// that happened rather than one that was queued.
/// </remarks>
public class PagingSentTests
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
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await client.GetStream().WriteAsync(bytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("connection closed");
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!done())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task A_Page_That_Goes_On_The_Air_Is_Announced_With_Its_Address_And_Message()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.Csma.Persistence = 255;   // never defer, so this test's own keyup is the only wait

        await using var server = new PagingTcpServer(channel, port: 0);
        var sent = new List<PageSentEvent>();
        server.PageSent += sent.Add;
        server.Start();

        var output = new FakeAudioOutput(SampleRate);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(output, new RecordingPtt(), stopping.Token);

        var (client, reader) = await ConnectAsync(server);
        using (client)
        {
            string response = await SendAsync(client, reader, "PAGE 1234567 1 ALPHA HELLO");
            response.Should().StartWith("OK ", "the channel took it");

            await WaitForAsync(() => sent.Count > 0);
        }

        await stopping.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        sent.Should().ContainSingle();
        sent[0].Ric.Should().Be(1234567u);
        sent[0].Function.Should().Be(1);
        sent[0].Kind.Should().Be("ALPHA");
        sent[0].Text.Should().Be("HELLO");
        sent[0].Summary.Should().Be("page 1234567 1 ALPHA HELLO");
        output.WrittenCount.Should().BeGreaterThan(0, "the page was rendered to the device");
    }

    [Fact]
    public async Task A_Page_That_Dies_Waiting_Is_Never_Announced_As_Sent()
    {
        // The drop scenario from PagingDropTests, asked the opposite question: a page that was
        // accepted and then refused must not appear in a record of what the station transmitted.
        // The clock is faked so that OK is certainly answered before the inhibit timeout expires.
        var clock = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time: clock, randomSeed: 7)
        {
            TransmitInhibit = () => true,
            TransmitInhibitTimeout = TimeSpan.FromMilliseconds(1),
        };

        await using var server = new PagingTcpServer(channel, port: 0);
        var sent = new List<PageSentEvent>();
        var drops = new List<PageDropEvent>();
        server.PageSent += sent.Add;
        server.PageDropped += drops.Add;
        server.Start();

        var (client, reader) = await ConnectAsync(server);
        using (client)
        {
            string response = await SendAsync(client, reader, "PAGE 1234567 0 TONE");
            response.Should().StartWith("OK ");

            clock.Advance(TimeSpan.FromMilliseconds(100));
            await WaitForAsync(() => drops.Count > 0);
        }

        sent.Should().BeEmpty("a transmission that did not happen must not be logged as one");
    }

    [Fact]
    public void A_Recorded_Page_Is_Not_Read_As_A_Callsign()
    {
        // What the frame log keeps for a page is the sentence, there being no frame, and
        // everything that reads a payload as an AX.25 frame shifts each byte right by one and
        // accepts [A-Z0-9]. A page whose text is a callsign is the case that would mint a station
        // that never transmitted, so the prefix has to refuse the read on its own.
        var page = new PageSentEvent(1, 1234567u, 1, "ALPHA", "M0LTE DE GB7RDG PSE QSY");

        Ax25AddressParser.TryParse(Encoding.ASCII.GetBytes(page.Summary), out string from, out string to)
            .Should().BeFalse();
        from.Should().BeEmpty();
        to.Should().BeEmpty();
    }

    [Fact]
    public void A_Tone_Only_Page_Says_So_Rather_Than_Recording_An_Empty_Message()
    {
        new PageSentEvent(3, 42u, 0, "TONE", "").Summary.Should().Be("page 42 0 TONE");
    }
}
