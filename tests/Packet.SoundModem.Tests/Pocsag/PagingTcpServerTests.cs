using System.Net.Sockets;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Pocsag;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// The daemon's paging endpoint, end to end: grammar validation, and a page submitted
/// over TCP going through the real channel-access path (CSMA, PTT, the transmit queue)
/// into audio that decodes — with the HEARD broadcast closing the loop. The channel runs
/// at 22050 Hz so the same captured transmission also feeds multimon-ng unresampled.
/// </summary>
public class PagingTcpServerTests : IAsyncLifetime
{
    private const int SampleRate = 22050;

    private readonly SoundModemChannel _channel;
    private readonly PagingTcpServer _server;
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly RecordingPtt _ptt = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(20));
    private Task? _transmitter;

    public PagingTcpServerTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 100;
        _server = new PagingTcpServer(_channel, port: 0);
    }

    public Task InitializeAsync()
    {
        _server.Start();
        _transmitter = _channel.RunTransmitterAsync(_output, _ptt, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync();
        _cancellation.Dispose();
    }

    private async Task<(TcpClient Client, StreamReader Reader)> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _server.LocalPort);
        return (client, new StreamReader(client.GetStream()));
    }

    private static async Task<string> SendAsync(TcpClient client, StreamReader reader, string line)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await client.GetStream().WriteAsync(bytes);
        return await ReadLineAsync(reader);
    }

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("connection closed");
    }

    [Fact]
    public async Task The_Grammar_Accepts_Valid_Pages_And_Rejects_Bad_Ones()
    {
        var (client, reader) = await ConnectAsync();
        using (client)
        {
            (await SendAsync(client, reader, "PAGE 133703 3 ALPHA Hello there")).Should().StartWith("OK ");
            (await SendAsync(client, reader, "PAGE 8 0 NUMERIC 555-0100")).Should().StartWith("OK ");
            (await SendAsync(client, reader, "PAGE 21 1 TONE")).Should().StartWith("OK ");
            (await SendAsync(client, reader, "page 21 1 tone")).Should().StartWith("OK ", "keywords are case-insensitive");

            (await SendAsync(client, reader, "PAGE 2097152 0 NUMERIC 1"))
                .Should().Be("ERR ric must be 0..2097151");
            (await SendAsync(client, reader, "PAGE 1 4 ALPHA hi"))
                .Should().Be("ERR function must be 0..3");
            (await SendAsync(client, reader, "PAGE 1 0 BEEP hi"))
                .Should().Be("ERR type must be ALPHA, NUMERIC or TONE");
            (await SendAsync(client, reader, "PAGE 1 0 NUMERIC ABC"))
                .Should().StartWith("ERR ", "letters are not in the numeric character set");
            (await SendAsync(client, reader, "PAGE 1 3 ALPHA naïve"))
                .Should().StartWith("ERR ", "alpha pages are 7-bit ASCII");
            (await SendAsync(client, reader, "PAGE 1"))
                .Should().Be("ERR usage: PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]");
            (await SendAsync(client, reader, "HELLO"))
                .Should().Be("ERR unknown command (expected PAGE)");
            (await SendAsync(client, reader, $"PAGE 1 3 ALPHA {new string('x', 241)}"))
                .Should().Be($"ERR text too long (max {PagingTcpServer.MaxTextLength} characters)");
        }
    }

    [Fact]
    public async Task Queue_Ids_Are_Sequential_Per_Server()
    {
        var (client, reader) = await ConnectAsync();
        using (client)
        {
            string first = await SendAsync(client, reader, "PAGE 1 3 ALPHA one");
            string second = await SendAsync(client, reader, "PAGE 1 3 ALPHA two");
            int a = int.Parse(first[3..]);
            int b = int.Parse(second[3..]);
            b.Should().Be(a + 1);
        }
    }

    private async Task<float[]> CaptureTransmissionAsync()
    {
        // Wait for the transmitter to render and finish (sample count stops growing).
        int seen = 0;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            int now = _output.WrittenCount;
            if (now > 0 && now == seen)
            {
                return _output.Snapshot();
            }

            seen = now;
        }

        throw new TimeoutException("no transmission captured");
    }

    [Fact]
    public async Task A_Submitted_Page_Goes_To_Air_Through_Ptt_And_Comes_Back_As_Heard()
    {
        var (sender, senderReader) = await ConnectAsync();
        var (listener, listenerReader) = await ConnectAsync();
        using (sender)
        using (listener)
        {
            (await SendAsync(sender, senderReader, "PAGE 133703 3 ALPHA Paged via TCP"))
                .Should().StartWith("OK ");

            float[] transmitted = await CaptureTransmissionAsync();
            _ptt.Events.Should().StartWith(["key", "unkey"], "the page keys the transmitter like any frame");

            // Half-duplex loopback: play the transmission back into the channel — the
            // decoder tap hears it and every paging client gets the HEARD line.
            _channel.ProcessReceive([.. transmitted, .. new float[SampleRate / 2]]);

            (await ReadLineAsync(senderReader)).Should().Be("HEARD 133703 3 ALPHA Paged via TCP");
            (await ReadLineAsync(listenerReader)).Should().Be("HEARD 133703 3 ALPHA Paged via TCP");
        }
    }

    [Fact]
    public async Task A_Tone_Page_Is_Heard_As_Tone()
    {
        var (client, reader) = await ConnectAsync();
        using (client)
        {
            (await SendAsync(client, reader, "PAGE 42 1 TONE")).Should().StartWith("OK ");
            float[] transmitted = await CaptureTransmissionAsync();
            _channel.ProcessReceive([.. transmitted, .. new float[SampleRate / 2]]);

            (await ReadLineAsync(reader)).Should().Be("HEARD 42 1 TONE");
        }
    }

    [SkippableFact]
    public async Task The_Endpoint_Transmission_Satisfies_Multimon()
    {
        Skip.If(PocsagMultimonTests.MultimonMissing, "multimon-ng is not installed");

        var (client, reader) = await ConnectAsync();
        using (client)
        {
            (await SendAsync(client, reader, "PAGE 133703 3 ALPHA Endpoint to multimon"))
                .Should().StartWith("OK ");

            float[] transmitted = await CaptureTransmissionAsync();
            string[] lines = PocsagMultimonTests.RunMultimonRaw(transmitted, "POCSAG1200");
            lines.Should().ContainSingle()
                .Which.Should().Be(
                    "POCSAG1200: Address:  133703  Function: 3  Alpha:   Endpoint to multimon");
        }
    }
}
