using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Modem;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Daemon;

namespace Packet.SoundModem.Tests.S5066;

/// <summary>
/// The STANAG 5066 node an <c>"s5066"</c> section starts (<see cref="S5066Station"/>): reading the
/// section, the default of starting nothing, and two pdn stations exchanging a SIS message over
/// simulated audio through the same channel, transmitter and receive tap the daemon uses.
/// </summary>
public sealed class S5066StationTests(ITestOutputHelper output) : IDisposable
{
    private const int ChannelRate = 48000;

    private readonly ScratchDirectory _dir = new("pdnsm-stanag");

    public void Dispose() => _dir.Dispose();

    private const string Node =
        """
        { "local_address": "0.0.1.1", "csma": { "mode": "jitter" },
          "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 } }
        """;

    // ------------------------------------------------------------------------ reading the section

    [Fact]
    public void A_File_Without_The_Section_Has_No_Node()
    {
        DaemonConfig config = Load("""{ "device": "null", "modems": [ { "mode": "afsk1200" } ] }""");
        config.S5066.Should().BeNull();
    }

    [Fact]
    public void The_Shipped_Example_Has_No_Node()
    {
        string example = Path.Combine(RepoRoot(), "soundmodem.example.json");
        DaemonConfig? config = DaemonConfig.TryLoad(example, out string error);
        config.Should().NotBeNull(error);
        config!.S5066.Should().BeNull();
    }

    [Fact]
    public void A_Section_Is_Read_With_The_Node_Settings_As_Written()
    {
        DaemonConfig config = Load($$"""{ "device": "null", "s5066": { "port": 5070, "node": {{Node}} } }""");

        config.S5066.Should().NotBeNull();
        config.S5066!.Port.Should().Be(5070);
        config.S5066.Parsed.Should().NotBeNull();
        M0LTE.S5066.Core.Dts.DpduAddress.ToDotted(config.S5066.Parsed!.Node.LocalAddress).Should().Be("0.0.1.1");
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_Contention_Slot_No_Longer_Than_The_Preamble_Is_Warned_Of()
    {
        string node = Node.Replace("\"csma\": { \"mode\": \"jitter\" }", "\"csma\": { \"mode\": \"jitter\", \"cont_slot_width_ms\": 10 }");
        DaemonConfig config = Load($$"""{ "device": "null", "s5066": { "node": {{node}} } }""");

        config.Warnings.Should().ContainSingle(w => w.StartsWith("s5066: ") && w.Contains("cont_slot_width_ms 10"));
    }

    [Fact]
    public void The_Port_Defaults_To_5066()
    {
        DaemonConfig config = Load($$"""{ "device": "null", "s5066": { "node": {{Node}} } }""");
        config.S5066!.Port.Should().Be(5066);
    }

    [Theory]
    [InlineData("""{ }""", "needs a \"node\" object")]
    [InlineData("""{ "node": { "csma": { "mode": "jitter" } } }""", "local_address")]
    [InlineData("""{ "node": { "local_address": "nonsense", "csma": { "mode": "jitter" } } }""", "local_address")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 } } }""", "csma.mode")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "csma": { "mode": "jitter" } } }""", "cas.max_links")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "csma": { "mode": "jitter" }, "cas": { "max_links": 4, "break_retries": 3 } } }""", "cas.link_request_retries")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "csma": { "mode": "jitter" }, "cas": { "max_links": 4, "link_request_retries": 5 } } }""", "cas.break_retries")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "csma": { "mode": "slotted" }, "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 } } }""", "csma.node_slot_position")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "csma": { "mode": "slotted", "node_slot_position": 999 }, "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 } } }""", "csma.node_slot_position")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "csma": { "mode": "jitter" }, "drc": { "default_speed": { "wn": 99, "interleaver": "short" } }, "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 } } }""", "drc.default_speed")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "callsign": "G8ABC" } }""", "callsign")]
    [InlineData("""{ "node": { "local_address": "0.0.1.1", "host": { "sis_listen": "127.0.0.1:5066" } } }""", "this station is the host")]
    [InlineData("""{ "port": 0, "node": { "local_address": "0.0.1.1" } }""", "use 1 to 65535")]
    public void A_Wrong_Section_Is_Refused_When_The_File_Is_Read(string section, string said)
    {
        string error = Refusal("""{ "device": "null", "s5066": """ + section + " }");
        error.Should().Contain("s5066").And.Contain(said);
    }

    [Fact]
    public void It_Cannot_Share_A_Station_With_Ardop()
    {
        Refusal($$"""{ "device": "null", "modems": [ { "mode": "ardop" } ], "s5066": { "node": {{Node}} } }""")
            .Should().Contain("ARDOP");
    }

    [Fact]
    public void Its_Port_Is_Claimed_Like_Every_Other()
    {
        Refusal($$"""{ "device": "null", "kissPort": 5066, "s5066": { "node": {{Node}} } }""")
            .Should().Contain("the STANAG 5066 SIS port").And.Contain("5066");
    }

    [Fact]
    public void An_Unknown_Key_In_The_Section_Is_Reported()
    {
        DaemonConfig config = Load($$"""{ "device": "null", "s5066": { "prot": 5070, "node": {{Node}} } }""");
        config.Warnings.Should().ContainSingle().Which.Should().StartWith("s5066: \"prot\"");
    }

    // ------------------------------------------------------------------------ the daemon

    [Fact]
    public async Task The_Daemon_Starts_No_Node_Without_The_Section()
    {
        // The paging line is printed after the point where the node would start, so by the time it is
        // seen the node has been started or not.
        string path = Write(StationJson($$""" "paging": { "port": {{FreePorts.Next()}} }, """));
        string said = await RunUntilAsync(path, "paging tcp: ");
        said.Should().NotContain("s5066 sis tcp").And.NotContain("s5066:").And.NotContain("SIS listening");
    }

    [Fact]
    public async Task The_Daemon_Serves_The_Sis_With_The_Section()
    {
        int port = FreePorts.Next();
        string path = Write(StationJson($$""" "s5066": { "port": {{port}}, "node": {{Node}} }, """));
        await RunUntilAsync(path, "s5066 sis tcp: ", whileRunning: async said =>
        {
            said.Should().Contain($"s5066 sis tcp: 127.0.0.1:{port} (node 0.0.1.1, MIL-STD-188-110D on the 48000 Hz channel");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using SisClient client = await SisClient.ConnectAsync("127.0.0.1", port, cts.Token);
            SisBindAccepted bound = await client.BindAsync(5, new ServiceType(1), cts.Token);
            bound.Should().NotBeNull();
        });
    }

    // ------------------------------------------------------------------------ two stations

    // On virtual time (#871): both nodes share one fake clock, which only the bench moves, one
    // millisecond at a time and only while every thread of both stations is at rest. Nothing here
    // waits on real time; the guards on the awaits only turn a hang into a failure.
    [Fact]
    public async Task Two_Stations_Exchange_A_Confirmed_Message_Over_Simulated_Audio()
    {
        await using var bench = new VirtualBench();
        await using S5066Station a = bench.StartNode(0, "0.0.1.1", "0000000000000001");
        await using S5066Station b = bench.StartNode(1, "0.0.1.2", "0000000000000002");
        await bench.SettleAsync();

        await using SisClient ca = await bench.Guard(SisClient.ConnectAsync(a.SisEndpoint));
        await using SisClient cb = await bench.Guard(SisClient.ConnectAsync(b.SisEndpoint));
        await bench.Guard(ca.BindAsync(5, new ServiceType(1)));
        await bench.Guard(cb.BindAsync(5, new ServiceType(1)));
        await bench.SettleAsync();

        byte[] message = Encoding.ASCII.GetBytes("over pdn-soundmodem's channel\r\n");
        int outputs = bench.OutputsOf(0);
        Task<SendResult> sending = await bench.Guard(ca.StartSendAsync(
            new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, message) { DeliveryMode = new ServiceType(1, Confirm: 1) }));

        // The request travels over a real socket: the clock stays put until the node has acted on it.
        await bench.OutputAfterAsync(0, outputs);
        Task<SisUnidataIndication> receiving = cb.Received.ReadAsync().AsTask();
        while (!(sending.IsCompleted && receiving.IsCompleted))
        {
            await bench.StepAsync();
        }

        SendResult sent = await sending;
        SisUnidataIndication got = await receiving;
        output.WriteLine($"confirmed after about {bench.VirtualMs / 1000.0:F1} s of virtual time; journal:\n{bench.Journal()}");

        got.UPdu.ToArray().Should().Equal(message);
        sent.Status.Should().Be(SendStatus.Confirmed);
        bench.Keyups.Should().BeGreaterThan(1, "both stations transmitted through their channels");
        bench.Sent.Should().BeGreaterThan(1, "every burst that went out is reported for the journal and frame log");
        bench.Heard.Should().BeGreaterThan(1, "every burst heard is reported too");
    }

    [Fact]
    public async Task A_Burst_Goes_Out_Behind_Txdelay_Of_Silence()
    {
        var channel = new SoundModemChannel(ChannelRate, randomSeed: 3);
        channel.Csma.TxDelayMilliseconds = 100;
        var written = new List<float>();
        var sink = new RecordingSink(block =>
        {
            lock (written)
            {
                written.AddRange(block);
            }
        });
        using var stop = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(sink, sink, stop.Token);
        var audio = new S5066Station.ChannelAudio(channel, _ => { }, () => { });
        var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 50 ms of a 1 kHz tone at the modem's 9600 Hz.
        float[] tone = [.. Enumerable.Range(0, 480).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / 9600))];
        audio.Play(tone, played.SetResult);
        await played.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        float[] heard;
        lock (written)
        {
            heard = [.. written];
        }

        int lead = ChannelRate / 10;
        heard.Length.Should().BeGreaterThanOrEqualTo(lead + (tone.Length * 5), "TXDELAY, then the burst at 48 kHz");
        heard.Take(lead).Max(MathF.Abs).Should().Be(0, "the first TXDELAY is silence, while the radio switches");
        heard.Skip(lead).Max(MathF.Abs).Should().BeGreaterThan(0.3f, "the burst follows it");
    }

    [Fact]
    public void The_Busy_Input_Is_A_Wrapper_And_Passes_The_Block_Duration_On()
    {
        var inner = new FakeModem();
        using var busy = new S5066Station.BusyInput(inner, new SoundModemChannel(ChannelRate, randomSeed: 5), heard: _ => { });
        ((IHostModemWrapper)busy).Inner.Should().BeSameAs(inner, "the host looks through it to the Ms110d modem for its calibration and trace sink");

        var seen = new List<int?>();
        busy.Start(new RecordingEvents(seen));
        inner.Events!.BurstStarted(1234);
        inner.Events.BurstStarted(null);

        seen.Should().Equal(1234, null);
    }

    // ------------------------------------------------------------------------ helpers

    private DaemonConfig Load(string json)
    {
        string path = Write(json);
        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
        config.Should().NotBeNull(error);
        return config!;
    }

    private string Refusal(string json)
    {
        string path = Write(json);
        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
        config.Should().BeNull("the file is wrong");
        return error;
    }

    private string Write(string json)
    {
        string path = Path.Combine(_dir.FullName, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string StationJson(string extra) =>
        "{ \"device\": \"flex:mock\", " + extra
        + $" \"waterfall\": {{ \"port\": {FreePorts.Next()} }},"
        + $" \"modems\": [ {{ \"mode\": \"afsk1200\", \"port\": {FreePorts.Next()} }} ] }}";

    /// <summary>Runs the daemon until a line starting <paramref name="until"/>, then runs
    /// <paramref name="whileRunning"/>, stops it and returns all it printed.</summary>
    private static async Task<string> RunUntilAsync(
        string path, string until, Func<string, Task>? whileRunning = null)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "pdn-soundmodem.dll");
        var info = new ProcessStartInfo(UsageTests.DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(path);

        var said = new StringBuilder();
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = info };
        void Line(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (said)
            {
                said.AppendLine(line);
            }

            if (line.StartsWith(until, StringComparison.Ordinal))
            {
                found.TrySetResult();
            }
        }

        string Said()
        {
            lock (said)
            {
                return said.ToString();
            }
        }

        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await Task.WhenAny(found.Task, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(60));
            found.Task.IsCompleted.Should().BeTrue($"the daemon prints a line starting \"{until}\"; it said:\n{Said()}");
            if (whileRunning is not null)
            {
                await whileRunning(Said());
            }

            return Said();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private sealed class FakeModem : IHostModem
    {
        public IModemEvents? Events { get; private set; }

        public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) => throw new NotSupportedException();

        public void Start(IModemEvents events) => Events = events;

        public void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom)
        {
        }

        public void TerminateReceive()
        {
        }

        public void SetMaxInputBlocks(int n)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingEvents(List<int?> blockMs) : IModemEvents
    {
        public void BurstStarted(int? blockMs1) => blockMs.Add(blockMs1);

        public void BytesReceived(ReadOnlyMemory<byte> bytes)
        {
        }

        public void BurstEnded(BurstEndReason reason, int wn, Interleaver interleaver, int k, int? snrTenthDb, int blocks)
        {
        }

        public void TransmitDone()
        {
        }

        public void ChannelBusy(bool busy)
        {
        }
    }

    /// <summary>A sound card that records what it is given and takes no time to play it.</summary>
    private sealed class RecordingSink(Action<float[]> onBlock) : IAudioOutput, IPttControl
    {
        public int SampleRate => ChannelRate;

        public void Write(ReadOnlySpan<float> samples) => onBlock(samples.ToArray());

        public void Drain()
        {
        }

        public void Key()
        {
        }

        public void Unkey()
        {
        }
    }
}
