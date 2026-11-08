using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
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
    private const int BlockMs = 20;

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
        string path = Write(StationJson(""));
        string said = await RunUntilAsync(path, "flex: setting the slice receive filter to ", linger: TimeSpan.FromSeconds(2));
        said.Should().NotContain("s5066 sis tcp").And.NotContain("s5066:").And.NotContain("SIS listening");
    }

    [Fact]
    public async Task The_Daemon_Serves_The_Sis_With_The_Section()
    {
        int port = FreePorts.Next();
        string path = Write(StationJson($$""" "s5066": { "port": {{port}}, "node": {{Node}} }, """));
        await RunUntilAsync(path, "s5066 sis tcp: ", linger: TimeSpan.Zero, whileRunning: async said =>
        {
            said.Should().Contain($"s5066 sis tcp: 127.0.0.1:{port} (node 0.0.1.1, MIL-STD-188-110D on the 48000 Hz channel");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using SisClient client = await SisClient.ConnectAsync("127.0.0.1", port, cts.Token);
            SisBindAccepted bound = await client.BindAsync(5, new ServiceType(1), cts.Token);
            bound.Should().NotBeNull();
        });
    }

    // ------------------------------------------------------------------------ two stations

    [Fact]
    public async Task Two_Stations_Exchange_A_Confirmed_Message_Over_Simulated_Audio()
    {
        await using var bench = new Bench();
        await using S5066Station a = bench.StartNode(0, "0.0.1.1", "0000000000000001");
        await using S5066Station b = bench.StartNode(1, "0.0.1.2", "0000000000000002");
        bench.Run();

        await using SisClient ca = await SisClient.ConnectAsync(a.SisEndpoint);
        await using SisClient cb = await SisClient.ConnectAsync(b.SisEndpoint);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await ca.BindAsync(5, new ServiceType(1), cts.Token);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);

        var elapsed = Stopwatch.StartNew();
        byte[] message = Encoding.ASCII.GetBytes("over pdn-soundmodem's channel\r\n");
        SendResult sent = await ca.SendAsync(
            new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, message) { DeliveryMode = new ServiceType(1, Confirm: 1) },
            cts.Token);
        SisUnidataIndication got = await cb.Received.ReadAsync(cts.Token);
        output.WriteLine($"confirmed in {elapsed.Elapsed.TotalSeconds:F1} s; journal:\n{bench.Journal()}");

        got.UPdu.ToArray().Should().Equal(message);
        sent.Status.Should().Be(SendStatus.Confirmed);
        bench.Keyups.Should().BeGreaterThan(1, "both stations transmitted through their channels");
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
    /// <paramref name="whileRunning"/> or waits <paramref name="linger"/>, stops it and returns all it
    /// printed.</summary>
    private static async Task<string> RunUntilAsync(
        string path, string until, TimeSpan linger, Func<string, Task>? whileRunning = null)
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

            await Task.Delay(linger);
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

    /// <summary>
    /// Two stations and the air between them: each a <see cref="SoundModemChannel"/> at 48 kHz with
    /// an afsk1200 modem, as the daemon builds one, and its own transmitter loop, playing in real
    /// time into the other's capture, which delivers 20 ms blocks continuously, silence included.
    /// The pattern of <c>ArdopSharedChannelSessionTests</c>.
    /// </summary>
    private sealed class Bench : IAsyncDisposable
    {
        private readonly SoundModemChannel[] _channels = new SoundModemChannel[2];
        private readonly Queue<float[]>[] _inbound = [new(), new()];
        private readonly Task[] _transmitters = new Task[2];
        private readonly CancellationTokenSource _stop = new();
        private readonly StringBuilder _journal = new();
        private Task? _capture;
        private int _keyups;

        public Bench()
        {
            for (int i = 0; i < 2; i++)
            {
                int other = 1 - i;
                var channel = new SoundModemChannel(ChannelRate, randomSeed: 11 + (18 * i));
                channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", ChannelRate, sink));
                channel.TransmittingChanged += keyed =>
                {
                    if (keyed)
                    {
                        Interlocked.Increment(ref _keyups);
                    }
                };
                var sink = new PacedSink(block =>
                {
                    lock (_inbound[other])
                    {
                        _inbound[other].Enqueue(block);
                    }
                });
                _transmitters[i] = channel.RunTransmitterAsync(sink, sink, _stop.Token);
                _channels[i] = channel;
            }
        }

        public int Keyups => Volatile.Read(ref _keyups);

        public S5066Station StartNode(int station, string address, string seed)
        {
            // The short CSMA, CAS and DTS values M0LTE.S5066's own audio test uses: a bench, not air.
            string node = $$"""
                {
                  "local_address": "{{address}}",
                  "prng_seed": "{{seed}}",
                  "csma": { "mode": "jitter", "num_cont_slots": 4, "cont_slot_width_ms": 200, "lbt_wait_dcd_ms": 2000, "lbt_wait_vdcd_ms": 500 },
                  "drc": { "default_speed": { "wn": 6, "interleaver": "short" } },
                  "cas": { "max_links": 4, "link_request_timeout_ms": 8000, "link_request_retries": 5, "break_retries": 3 },
                  "dts": { "max_transmission_ms": 6000, "ack_wait_ms": 8000 }
                }
                """;
            var config = new S5066Config { Port = 0, Node = JsonDocument.Parse(node).RootElement.Clone() };
            return S5066Station.Start(_channels[station], config, IPAddress.Loopback, line =>
            {
                lock (_journal)
                {
                    _journal.AppendLine($"[{station}] {line}");
                }
            });
        }

        /// <summary>Starts the capture clock, once both nodes have their receive taps in.</summary>
        public void Run() => _capture = CaptureAsync(_stop.Token);

        public string Journal()
        {
            lock (_journal)
            {
                return _journal.ToString();
            }
        }

        private async Task CaptureAsync(CancellationToken cancellation)
        {
            var elapsed = Stopwatch.StartNew();
            var quiet = new float[ChannelRate * BlockMs / 1000];
            var random = new Random(5);
            long delivered = 0;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(BlockMs, cancellation);
                    long due = elapsed.ElapsedMilliseconds / BlockMs;
                    while (delivered < due)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            float[]? block;
                            lock (_inbound[i])
                            {
                                block = _inbound[i].Count > 0 ? _inbound[i].Dequeue() : null;
                            }

                            float[] heard = (float[])(block ?? quiet).Clone();
                            for (int s = 0; s < heard.Length; s++)
                            {
                                // A little noise, so nothing downstream sees digital silence.
                                heard[s] += (float)((random.NextDouble() - 0.5) * 0.002);
                            }

                            _channels[i].ProcessReceive(heard);
                        }

                        delivered++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (Task t in _transmitters.Append(_capture ?? Task.CompletedTask))
            {
                try
                {
                    await t;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _stop.Dispose();
        }
    }

    /// <summary>The playback device: hands each 20 ms block to the air as it plays, in real time.</summary>
    private sealed class PacedSink(Action<float[]> onBlock) : IAudioOutput, IPttControl
    {
        private readonly int _blockSamples = ChannelRate * BlockMs / 1000;

        public int SampleRate => ChannelRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            var elapsed = Stopwatch.StartNew();
            long played = 0;
            for (int i = 0; i < samples.Length; i += _blockSamples)
            {
                int count = Math.Min(_blockSamples, samples.Length - i);
                onBlock(samples.Slice(i, count).ToArray());
                played += count;
                int wait = (int)((played * 1000 / ChannelRate) - elapsed.ElapsedMilliseconds);
                if (wait > 0)
                {
                    Thread.Sleep(wait);
                }
            }
        }

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
