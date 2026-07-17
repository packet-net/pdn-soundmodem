using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Packet.SoundModem.Ardop;
using Packet.SoundModem.Ardop.Host;
using Packet.SoundModem.Audio;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Ardop;

/// <summary>
/// The decisive Phase D live legs against a real ardopcf (git a7c9228) over
/// snd-aloop:
/// (1) host-protocol conformance — one command script run against ardopcf's host
/// interface and ours, transcripts diffed byte-for-byte;
/// (2) full-stack ARQ sessions through the real daemon binary — a byte-faithful
/// scripted host reproducing Pat's exact command sequences (wl2k-go
/// transport/ardop/tnc.go) drives our --ardop daemon against ardopcf, both roles;
/// (3) RXO — our monitor decodes a third-party ardopcf↔ardopcf session running on a
/// shared dmix/dsnoop channel.
/// The real-Pat leg lives beside these (<c>Pat_Exchanges_A_Message…</c>), gated on a
/// PAT binary.
/// </summary>
/// <remarks>
/// Gated on <c>ARDOPCF</c> (binary path) and <c>ARDOP_ALOOP_CARD</c> (snd-aloop card
/// index); run under <c>sg audio</c>. The Pat leg additionally needs <c>PAT</c> (Pat
/// binary path). One test at a time — xUnit serializes within the class.
/// </remarks>
public class ArdopHostLiveTests(ITestOutputHelper output)
{
    private static readonly int BasePort = 8700 + (Environment.ProcessId % 100) * 4;

    private static (string Binary, int Card)? Rig()
    {
        string? binary = Environment.GetEnvironmentVariable("ARDOPCF");
        string? card = Environment.GetEnvironmentVariable("ARDOP_ALOOP_CARD");
        if (binary is null || !File.Exists(binary) || card is null || !int.TryParse(card, out int index))
        {
            return null;
        }

        return (binary, index);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>A host-side client of the ardop TCP host protocol — works against
    /// ardopcf and against our server alike (that interchangeability is the point).</summary>
    private sealed class HostClient : IDisposable
    {
        private readonly TcpClient _command;
        private readonly TcpClient _data;
        private readonly List<string> _lines = [];
        private readonly List<(string Tag, byte[] Data)> _blocks = [];
        private readonly CancellationTokenSource _stop = new();

        public HostClient(int commandPort)
        {
            _command = ConnectWithRetry(commandPort);
            _data = ConnectWithRetry(commandPort + 1);
            _ = Task.Run(ReadCommandSocket);
            _ = Task.Run(ReadDataSocket);
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public byte[] DataOfTag(string tag)
        {
            lock (_blocks)
            {
                return [.. _blocks.Where(b => b.Tag == tag).SelectMany(b => b.Data)];
            }
        }

        public void Command(string command) =>
            _command.GetStream().Write(Encoding.ASCII.GetBytes(command + "\r"));

        public void SendData(byte[] data)
        {
            var framed = new byte[data.Length + 2];
            framed[0] = (byte)(data.Length >> 8);
            framed[1] = (byte)data.Length;
            data.CopyTo(framed, 2);
            _data.GetStream().Write(framed);
        }

        public bool WaitFor(Func<IReadOnlyList<string>, bool> predicate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate(Lines))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return predicate(Lines);
        }

        public bool WaitForData(string tag, int byteCount, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (DataOfTag(tag).Length >= byteCount)
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return DataOfTag(tag).Length >= byteCount;
        }

        /// <summary>Sends one command and collects its reply lines: everything that
        /// arrives until the socket goes quiet for 400 ms (max 3 s).</summary>
        public List<string> Exchange(string command)
        {
            int before = Lines.Count;
            Command(command);
            var sw = Stopwatch.StartNew();
            int seen = before;
            long lastNews = sw.ElapsedMilliseconds;
            while (sw.ElapsedMilliseconds < 3000)
            {
                int now = Lines.Count;
                if (now != seen)
                {
                    seen = now;
                    lastNews = sw.ElapsedMilliseconds;
                }
                else if (seen > before && sw.ElapsedMilliseconds - lastNews > 400)
                {
                    break;
                }

                Thread.Sleep(25);
            }

            return [.. Lines.Skip(before)];
        }

        private static TcpClient ConnectWithRetry(int port)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return new TcpClient("127.0.0.1", port) { NoDelay = true };
                }
                catch (SocketException) when (attempt < 100)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void ReadCommandSocket()
        {
            try
            {
                var stream = _command.GetStream();
                var line = new StringBuilder();
                var buffer = new byte[1024];
                while (!_stop.IsCancellationRequested)
                {
                    int got = stream.Read(buffer);
                    if (got <= 0)
                    {
                        return;
                    }

                    for (int i = 0; i < got; i++)
                    {
                        if (buffer[i] == '\r')
                        {
                            lock (_lines)
                            {
                                _lines.Add(line.ToString());
                            }

                            line.Clear();
                        }
                        else
                        {
                            line.Append((char)buffer[i]);
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ReadDataSocket()
        {
            try
            {
                var stream = _data.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    byte[] header = ReadExactly(stream, 2);
                    int length = (header[0] << 8) + header[1];
                    byte[] body = ReadExactly(stream, length);
                    lock (_blocks)
                    {
                        _blocks.Add((Encoding.ASCII.GetString(body, 0, 3), body[3..]));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, read, count - read);
                if (got <= 0)
                {
                    throw new IOException("data socket closed");
                }

                read += got;
            }

            return buffer;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _command.Dispose();
            _data.Dispose();
        }
    }

    private sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly List<string> _stdout = [];

        public ChildProcess(string fileName, string arguments, IDictionary<string, string>? environment = null)
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    info.Environment[key] = value;
                }
            }

            _process = Process.Start(info)!;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_stdout)
                    {
                        _stdout.Add(e.Data);
                    }
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (_stdout)
                    {
                        _stdout.Add(e.Data);
                    }
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public IReadOnlyList<string> Output
        {
            get
            {
                lock (_stdout)
                {
                    return [.. _stdout];
                }
            }
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public bool WaitForOutput(Func<IReadOnlyList<string>, bool> predicate, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate(Output))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return predicate(Output);
        }

        public bool WaitForExit(int timeoutMs) => _process.WaitForExit(timeoutMs);

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private static ChildProcess StartArdopcf(string binary, int port, string captureDevice, string playbackDevice) =>
        new(binary, $"--nologfile {port} \"{captureDevice}\" \"{playbackDevice}\"");

    private static string DaemonBinary()
    {
        // The test assembly sits in tests/…/bin/Debug/net10.0; the daemon executable
        // (AssemblyName pdn-soundmodem) in src/…/bin/Debug/net10.0 of the same build.
        string testDir = Path.GetDirectoryName(typeof(ArdopHostLiveTests).Assembly.Location)!;
        string root = Path.GetFullPath(Path.Combine(testDir, "../../../../.."));
        return Path.Combine(root, "src/Packet.SoundModem.Daemon/bin/Debug/net10.0/pdn-soundmodem");
    }

    private static ChildProcess StartDaemon(int card, int ardopPort)
    {
        var daemon = new ChildProcess(
            DaemonBinary(), $"--device plughw:{card},1 --capture-rate 12000 --ardop {ardopPort}");
        daemon.WaitForOutput(o => o.Any(l => l.Contains("ardop host tcp")), 20000)
            .Should().BeTrue($"the daemon must start its ARDOP host interface: {string.Join(" | ", daemon.Output)}");
        return daemon;
    }

    // ================================================== 1. transcript conformance

    // Commands only — nothing here keys a transmitter on our side; ardopcf-side
    // stray transmissions (the PING quirk) surface only as PTT lines, filtered
    // below as timing-dependent.
    private static readonly string[] ConformanceScript =
    [
        "INITIALIZE", "STATE", "PROTOCOLMODE", "VERSION",
        "BUFFER", "BUFFER 5", "DATATOSEND",
        "ARQBW", "ARQBW 500MAX", "ARQBW 300MAX", "ARQBW UNDEFINED", "ARQBW",
        "CALLBW", "CALLBW 1000FORCED", "CALLBW UNDEFINED", "CALLBW",
        "ARQTIMEOUT", "ARQTIMEOUT 90", "ARQTIMEOUT 20",
        "AUTOBREAK", "AUTOBREAK FALSE", "AUTOBREAK MAYBE",
        "BUSYBLOCK", "BUSYBLOCK TRUE",
        "BUSYDET", "BUSYDET 0", "BUSYDET 11",
        "CWID", "CWID TRUE", "CWID ONOFF", "CWID FALSE", "CWID BOGUS",
        "DISCONNECT",
        "DRIVELEVEL", "DRIVELEVEL 50", "DRIVELEVEL 150",
        "ENABLEPINGACK", "ENABLEPINGACK FALSE",
        "EXTRADELAY", "EXTRADELAY 10",
        "FASTSTART", "FASTSTART FALSE",
        "FECID", "FECID TRUE", "FECID FALSE",
        "FECMODE", "FECMODE 8PSK.1000.100", "FECMODE 4FSK.9999.1",
        "FECREPEATS", "FECREPEATS 2", "FECREPEATS 9",
        "FECSEND",
        "FSKONLY", "FSKONLY TRUE", "FSKONLY FALSE",
        "GRIDSQUARE", "GRIDSQUARE IO91WM", "GRIDSQUARE", "GRIDSQUARE ZZ99XX", "GRIDSQUARE IO9",
        "LEADER", "LEADER 245", "LEADER 20",
        "LISTEN", "LISTEN FALSE", "LISTEN TRUE",
        "MONITOR", "MONITOR FALSE",
        "MYAUX",
        "SENDID", "TWOTONETEST", "FECSEND TRUE",
        "ARQCALL", "ARQCALL G8BBB", "ARQCALL G8BBB X", "ARQCALL G8BBB 0", "ARQCALL G8BBB 5",
        "MYCALL", "MYCALL M0LTE-7", "MYCALL",
        "MYAUX M0AAA-2,G8BBB", "MYAUX",
        // A *successful* ARQCALL is deliberately absent: it would start a real call
        // whose asynchronous repeat/timeout notifications bleed into later commands'
        // reply windows (the session tests cover the success path). The mode faults:
        "PROTOCOLMODE FEC", "ARQCALL G8BBB 5",
        "SQUELCH", "SQUELCH 7", "SQUELCH 0",
        "STATE X",
        "TRAILER", "TRAILER 35", "TRAILER 300",
        "TUNINGRANGE", "TUNINGRANGE 50", "TUNINGRANGE 300",
        "USE600MODES", "USE600MODES TRUE",
        "PURGEBUFFER", "ABORT", "NOSUCHCMD HELLO",
        "PROTOCOLMODE ARQ", "STATE", "ARQCALL G8BBB 0",
        "PROTOCOLMODE RXO", "ARQCALL G8BBB 5", "PING G8BBB 2",
        "PROTOCOLMODE FEC", "PROTOCOLMODE",
    ];

    // Lines that are legitimately environment/timing dependent, not protocol
    // conformance: PTT keying (ardopcf transmits its RXO-quirk ping; we don't key in
    // a receive-only mode), INPUTPEAKS level telemetry, BUSY channel-detector
    // notifications (our busy detector is unported — documented divergence).
    private static bool IsEnvironmental(string line) =>
        line.StartsWith("PTT ", StringComparison.Ordinal)
        || line.StartsWith("INPUTPEAKS", StringComparison.Ordinal)
        || line.StartsWith("BUSY ", StringComparison.Ordinal);

    [SkippableFact]
    public async Task Host_Command_Transcript_Matches_Ardopcf_Byte_For_Byte()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        using var cf = StartArdopcf(rig!.Value.Binary, BasePort, $"plughw:{rig.Value.Card},0", $"plughw:{rig.Value.Card},0");
        using var cfHost = new HostClient(BasePort);

        var tnc = new ArdopHostTnc(captureDevice: "default", playbackDevice: "default")
        {
            Transmitter = _ => Task.CompletedTask,
        };
        await using var ours = new ArdopHostServer(tnc, commandPort: BasePort + 2, ownsTnc: true);
        ours.Start();
        using var ourHost = new HostClient(ours.LocalCommandPort);

        var mismatches = new List<string>();
        int compared = 0;
        foreach (string command in ConformanceScript)
        {
            List<string> theirs = [.. cfHost.Exchange(command).Where(l => !IsEnvironmental(l))];
            List<string> mine = [.. ourHost.Exchange(command).Where(l => !IsEnvironmental(l))];
            compared++;

            if (command == "VERSION")
            {
                // The one deliberate divergence: the implementation name.
                theirs.Should().ContainSingle().Which.Should().StartWith("VERSION ");
                mine.Should().ContainSingle().Which.Should().StartWith("VERSION ");
                output.WriteLine($"VERSION: theirs='{theirs[0]}' ours='{mine[0]}' (excluded from diff)");
                continue;
            }

            if (!theirs.SequenceEqual(mine, StringComparer.Ordinal))
            {
                mismatches.Add(
                    $"'{command}': ardopcf=[{string.Join(" | ", theirs)}] ours=[{string.Join(" | ", mine)}]");
            }
        }

        output.WriteLine($"compared {compared} commands against ardopcf {rig.Value.Binary}");
        foreach (string mismatch in mismatches)
        {
            output.WriteLine("MISMATCH " + mismatch);
        }

        mismatches.Should().BeEmpty("every reply and fault must match ardopcf byte-for-byte");
    }

    // =================================== 2. full stack through the daemon binary

    // Pat's exact initialization sequence (wl2k-go transport/ardop tnc.go:126-170 —
    // init(), SetMycall, SetGridSquare, SetListenEnabled; booleans are Go %t
    // lowercase). The transcripts of a real `pat connect` show the same order.
    private static void PatInit(HostClient host, string mycall, string grid, bool listen)
    {
        host.Exchange("INITIALIZE");
        host.Exchange("STATE");
        host.Exchange("PROTOCOLMODE ARQ");
        host.Exchange("ARQTIMEOUT 90");
        host.Exchange("LISTEN false");
        host.Exchange($"MYCALL {mycall}");
        host.Exchange($"GRIDSQUARE {grid}");
        if (listen)
        {
            host.Exchange("MYCALL");        // Pat's Listen() re-reads mycall
            host.Exchange("LISTEN true");
        }
    }

    [SkippableFact]
    public void Scripted_Pat_Host_Session_Our_Daemon_Calls_Ardopcf()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        int cfPort = BasePort;
        int ourPort = BasePort + 2;
        byte[] payload = new byte[128];
        new Random(41).NextBytes(payload);

        using var cf = StartArdopcf(rig!.Value.Binary, cfPort, $"plughw:{rig.Value.Card},0", $"plughw:{rig.Value.Card},0");
        using var cfHost = new HostClient(cfPort);
        cfHost.Exchange("INITIALIZE");
        cfHost.Exchange("MYCALL G8BBB");
        cfHost.Exchange("GRIDSQUARE IO92XX");
        cfHost.Exchange("PROTOCOLMODE ARQ");
        cfHost.Exchange("ARQBW 500MAX");
        cfHost.Exchange("ARQTIMEOUT 60");
        cfHost.Exchange("LISTEN TRUE");

        using var daemon = StartDaemon(rig.Value.Card, ourPort);
        using var pat = new HostClient(ourPort);
        PatInit(pat, "M0AAA", "IO81VK", listen: false);

        // Pat Dial: query + set bandwidth, then ARQCALL (dial.go:96-113).
        pat.Exchange("ARQBW");
        pat.Exchange("ARQBW 500MAX");
        pat.SendData(payload);
        pat.WaitFor(l => l.Contains("BUFFER 128"), 5000)
            .Should().BeTrue("Pat's conn.Write blocks on the BUFFER update");
        pat.Command("ARQCALL G8BBB 10");

        pat.WaitFor(l => l.Any(x => x.StartsWith("CONNECTED G8BBB 500", StringComparison.Ordinal)), 60000)
            .Should().BeTrue($"our modem must connect: {string.Join(" | ", pat.Lines)}");
        cfHost.WaitFor(l => l.Any(x => x.StartsWith("CONNECTED M0AAA 500", StringComparison.Ordinal)), 30000)
            .Should().BeTrue("ardopcf must report the session");

        cfHost.WaitForData("ARQ", payload.Length, 120000)
            .Should().BeTrue("the payload must arrive at ardopcf's host");
        cfHost.DataOfTag("ARQ").Should().Equal(payload, "byte-exact ARQ delivery");

        // Pat Flush + Close (conn.go:141-199): wait BUFFER 0, DISCONNECT, wait
        // DISCONNECTED.
        pat.WaitFor(l => l.Contains("BUFFER 0"), 60000).Should().BeTrue();
        pat.Command("DISCONNECT");
        pat.WaitFor(l => l.Contains("DISCONNECTED"), 60000)
            .Should().BeTrue("the disconnect handshake must complete");
        cfHost.WaitFor(l => l.Contains("DISCONNECTED"), 30000).Should().BeTrue();

        output.WriteLine(
            $"ours→ardopcf via daemon binary: CONNECTED both sides, {payload.Length} bytes byte-exact, " +
            $"orderly disconnect. Our host transcript tail: " +
            string.Join(" | ", pat.Lines.TakeLast(6)));
    }

    [SkippableFact]
    public void Scripted_Pat_Host_Session_Ardopcf_Calls_Our_Daemon()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        int cfPort = BasePort;
        int ourPort = BasePort + 2;
        byte[] payload = new byte[96];
        new Random(43).NextBytes(payload);

        using var cf = StartArdopcf(rig!.Value.Binary, cfPort, $"plughw:{rig.Value.Card},0", $"plughw:{rig.Value.Card},0");
        using var cfHost = new HostClient(cfPort);
        cfHost.Exchange("INITIALIZE");
        cfHost.Exchange("MYCALL G8BBB");
        cfHost.Exchange("PROTOCOLMODE ARQ");
        cfHost.Exchange("ARQBW 500MAX");
        cfHost.Exchange("ARQTIMEOUT 60");
        cfHost.Exchange("LISTEN FALSE");

        using var daemon = StartDaemon(rig.Value.Card, ourPort);
        using var pat = new HostClient(ourPort);
        PatInit(pat, "M0AAA", "IO81VK", listen: true);

        cfHost.SendData(payload);
        cfHost.WaitFor(l => l.Any(x => x.StartsWith("BUFFER ", StringComparison.Ordinal)), 5000).Should().BeTrue();
        cfHost.Command("ARQCALL M0AAA 10");

        // Pat's listener keys on TARGET before CONNECTED (listen.go:86-99).
        pat.WaitFor(l => l.Contains("TARGET M0AAA"), 60000)
            .Should().BeTrue($"we must report the connect request's target: {string.Join(" | ", pat.Lines)}");
        pat.WaitFor(l => l.Any(x => x.StartsWith("CONNECTED G8BBB 500", StringComparison.Ordinal)), 30000)
            .Should().BeTrue();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 120000 && pat.DataOfTag("ARQ").Length < payload.Length)
        {
            Thread.Sleep(100);
        }

        pat.DataOfTag("ARQ").Should().Equal(payload, "ardopcf's payload must arrive at our host byte-exact");

        cfHost.Command("DISCONNECT");
        pat.WaitFor(l => l.Contains("DISCONNECTED"), 60000).Should().BeTrue();
        cfHost.WaitFor(l => l.Contains("DISCONNECTED"), 30000).Should().BeTrue();

        output.WriteLine(
            $"ardopcf→ours via daemon binary: TARGET+CONNECTED reported, {payload.Length} bytes " +
            "byte-exact at our host, orderly disconnect.");
    }

    // ============================================================== 3. real Pat

    [SkippableFact]
    public void Pat_Exchanges_A_Message_Through_Our_Modem_And_Ardopcf()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");
        string? pat = Environment.GetEnvironmentVariable("PAT");
        Skip.If(pat is null || !File.Exists(pat), "set PAT to a Pat binary for the real-Pat leg");

        int cfPort = BasePort;
        int ourPort = BasePort + 2;
        string scratch = Path.Combine(Path.GetTempPath(), $"pat-live-{Environment.ProcessId}");

        // Two self-contained Pat homes: G8BBB listens through ardopcf, M0AAA
        // connects through our daemon.
        string homeListen = MakePatHome(scratch, "listen", "G8BBB", "IO92XX", cfPort);
        string homeConnect = MakePatHome(scratch, "connect", "M0AAA", "IO81VK", ourPort);

        using var cf = StartArdopcf(rig!.Value.Binary, cfPort, $"plughw:{rig.Value.Card},0", $"plughw:{rig.Value.Card},0");
        using var daemon = StartDaemon(rig.Value.Card, ourPort);

        // Queue a P2P message for G8BBB in the connecting Pat's outbox (compose
        // reads the body from stdin, mail(1) style — pipe it via the shell).
        using (var compose = new ChildProcess(
            "/bin/sh", $"-c \"echo 'ARDOP Phase D acceptance message' | HOME={homeConnect} {pat} compose --p2p-only -s 'pdn phase D' G8BBB\""))
        {
            compose.WaitForExit(15000).Should().BeTrue("compose must finish");
        }

        Directory.GetFiles(Path.Combine(homeConnect, ".local/share/pat/mailbox/M0AAA/out"))
            .Should().NotBeEmpty("the composed message must sit in the outbox");

        // G8BBB: headless listener (`pat --listen ardop http` keeps it alive).
        using var listener = new ChildProcess(
            pat!, $"--listen ardop http --addr 127.0.0.1:{BasePort + 3000}",
            new Dictionary<string, string> { ["HOME"] = homeListen });
        listener.WaitForOutput(o => o.Any(l => l.Contains("Listening for incoming traffic")), 20000)
            .Should().BeTrue($"Pat listener must come up: {string.Join(" | ", listener.Output)}");

        // M0AAA: the real Pat dials through OUR modem.
        using var connect = new ChildProcess(
            pat!, "connect ardop:///G8BBB",
            new Dictionary<string, string> { ["HOME"] = homeConnect });
        connect.WaitForExit(180000).Should().BeTrue("pat connect must finish");

        output.WriteLine("pat connect output:");
        foreach (string line in connect.Output)
        {
            output.WriteLine("  " + line);
        }

        connect.ExitCode.Should().Be(0, "the Pat session must succeed");
        connect.Output.Should().Contain(l => l.Contains("Connected to G8BBB"),
            "Pat must report the ARQ connection through our modem");

        // The message crossed the air: it left M0AAA's outbox and sits in G8BBB's
        // inbox.
        var swInbox = Stopwatch.StartNew();
        string inbox = Path.Combine(homeListen, ".local/share/pat/mailbox/G8BBB/in");
        while (swInbox.ElapsedMilliseconds < 20000
            && (!Directory.Exists(inbox) || Directory.GetFiles(inbox).Length == 0))
        {
            Thread.Sleep(500);
        }

        Directory.GetFiles(inbox).Should().NotBeEmpty("the message must arrive in the listener's inbox");
        Directory.GetFiles(Path.Combine(homeConnect, ".local/share/pat/mailbox/M0AAA/out"))
            .Should().BeEmpty("the message must have left the outbox");
        output.WriteLine($"message delivered: {Directory.GetFiles(inbox)[0]}");
    }

    private static string MakePatHome(string scratch, string name, string mycall, string grid, int ardopPort)
    {
        string home = Path.Combine(scratch, name);
        string configDir = Path.Combine(home, ".config/pat");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), $$"""
            {
              "mycall": "{{mycall}}",
              "locator": "{{grid}}",
              "ardop": {
                "addr": "localhost:{{ardopPort}}",
                "arq_bandwidth": { "Forced": false, "Max": 500 },
                "connect_requests": 10,
                "rig": "",
                "ptt_ctrl": false,
                "beacon_interval": 0,
                "cwid_enabled": false
              }
            }
            """);
        return home;
    }

    // ==================================================================== 4. RXO

    /// <summary>Builds an ALSA config letting a monitor overhear a two-station aloop
    /// link. The stations keep their proven direct playback pipes (A plays into
    /// hw:card,0,0; B into hw:card,1,0 — ardopcf wedges mid-TX on a dmix playback,
    /// so playback stays exclusive) while each station's capture goes through
    /// dsnoop, which lets the monitor attach to both pipes as an extra reader.
    /// <c>pdnboth</c> sums the two directions (multi + route), i.e. the composite
    /// on-air channel, and <c>pdnmonitor</c> is its capture-only duplex view (null
    /// playback) for the daemon.</summary>
    private static string SharedChannelAlsaConfig(int card)
    {
        // Empirically pinned: the slaves name explicit subdevices so every dsnoop
        // client attaches to the same aloop pipe (auto-picked subdevices silently
        // land on unrelated pipes); capture periods are the 600-frame size ardopcf's
        // set_period_size_near(600)-before-set_rate negotiation needs (a smaller pin
        // makes plug wander to a resampled config and the 12 kHz set_rate then
        // faults); ipc keys are per-run so a SIGKILLed client's stale shared-memory
        // segment can never poison the next run.
        int key = 0x50D0000 + (Environment.ProcessId % 0xFFF) * 2;
        string path = Path.Combine(Path.GetTempPath(), $"pdn-ardop-rxo-{Environment.ProcessId}.conf");
        File.WriteAllText(path, $$"""
            pcm.pdnmonA {
                type plug
                slave.pcm {
                    type dsnoop
                    ipc_key {{key}}
                    slave {
                        pcm "hw:{{card}},0,0"
                        rate 12000
                        format S16_LE
                        channels 1
                        period_size 600
                        buffer_size 3600
                    }
                }
            }
            pcm.pdnmonB {
                type plug
                slave.pcm {
                    type dsnoop
                    ipc_key {{key + 1}}
                    slave {
                        pcm "hw:{{card}},1,0"
                        rate 12000
                        format S16_LE
                        channels 1
                        period_size 600
                        buffer_size 3600
                    }
                }
            }
            pcm.pdnboth {
                type route
                slave.pcm {
                    type multi
                    slaves.a.pcm "pdnmonA"
                    slaves.a.channels 1
                    slaves.b.pcm "pdnmonB"
                    slaves.b.channels 1
                    bindings.0.slave a
                    bindings.0.channel 0
                    bindings.1.slave b
                    bindings.1.channel 0
                }
                slave.channels 2
                ttable.0.0 1
                ttable.0.1 1
            }
            pcm.pdnmonitor {
                type asym
                playback.pcm "null"
                capture.pcm "pdnboth"
            }
            """);
        return "/usr/share/alsa/alsa.conf:" + path;
    }

    [SkippableFact]
    public void Rxo_Monitors_A_Live_Third_Party_Ardopcf_Session()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");
        int card = rig!.Value.Card;

        string alsaConfig = SharedChannelAlsaConfig(card);
        var env = new Dictionary<string, string> { ["ALSA_CONFIG_PATH"] = alsaConfig };

        using var stationA = new ChildProcess(
            rig.Value.Binary, $"--nologfile {BasePort} pdnmonA plughw:{card},0,0", env);
        using var stationB = new ChildProcess(
            rig.Value.Binary, $"--nologfile {BasePort + 2} pdnmonB plughw:{card},1,0", env);
        using var hostA = new HostClient(BasePort);
        using var hostB = new HostClient(BasePort + 2);
        foreach (var (host, call) in new[] { (hostA, "G7AAA"), (hostB, "G7BBB") })
        {
            host.Exchange("INITIALIZE");
            host.Exchange($"MYCALL {call}");
            host.Exchange("PROTOCOLMODE ARQ");
            host.Exchange("ARQBW 500MAX");
            host.Exchange("FSKONLY TRUE");
            host.Exchange("ARQTIMEOUT 60");
            host.Exchange("LISTEN TRUE");
        }

        // Our monitor: the REAL daemon binary on the composite channel, put into RXO
        // over its host socket like any other host application would.
        using var monitorDaemon = new ChildProcess(
            DaemonBinary(), $"--device pdnmonitor --capture-rate 12000 --ardop {BasePort + 4}", env);
        monitorDaemon.WaitForOutput(o => o.Any(l => l.Contains("ardop host tcp")), 20000)
            .Should().BeTrue($"the monitor daemon must start: {string.Join(" | ", monitorDaemon.Output)}");
        using var monitor = new HostClient(BasePort + 4);
        monitor.Exchange("INITIALIZE");
        monitor.Exchange("PROTOCOLMODE RXO").Should().Contain("PROTOCOLMODE now RXO");

        // The third-party session: A calls B, sends 128 bytes (8 data frames at the
        // 16-byte 200.50S rung — plenty for the monitor even if one is marginal),
        // disconnects.
        byte[] payload = new byte[128];
        new Random(17).NextBytes(payload);
        hostA.SendData(payload);
        hostA.WaitFor(l => l.Contains("BUFFER 128"), 5000).Should().BeTrue();
        hostA.Command("ARQCALL G7BBB 5");

        hostB.WaitFor(l => l.Any(x => x.StartsWith("CONNECTED G7AAA 500", StringComparison.Ordinal)), 60000)
            .Should().BeTrue($"the third-party session must connect: A=[{string.Join(" | ", hostA.Lines.TakeLast(5))}]");
        hostB.WaitForData("ARQ", payload.Length, 120000)
            .Should().BeTrue("the third-party transfer must complete");
        hostA.Command("DISCONNECT");
        hostA.WaitFor(l => l.Contains("DISCONNECTED"), 60000).Should().BeTrue();

        Thread.Sleep(2000);  // let the monitor drain the tail
        List<string> rxo = [.. monitor.Lines.Where(s => s.StartsWith("STATUS [RXO", StringComparison.Ordinal))];

        output.WriteLine($"monitor heard {rxo.Count} RXO frames:");
        foreach (string line in rxo)
        {
            output.WriteLine("  " + line);
        }

        byte sessionId = ArdopCrc.SessionId("G7AAA", "G7BBB");
        rxo.Should().Contain("STATUS [RXO FF] ConReq500M frame received OK.");
        rxo.Should().Contain($"STATUS [RXO {sessionId:X2}] ConAck500 frame received OK.");
        rxo.Should().Contain(s =>
            s.StartsWith($"STATUS [RXO {sessionId:X2}] 4FSK.") && s.EndsWith("frame received OK."),
            "the session's data frames decode under its own session ID");
        rxo.Should().Contain($"STATUS [RXO {sessionId:X2}] DataACK frame received OK.");
        rxo.Should().Contain($"STATUS [RXO {sessionId:X2}] DISC frame received OK.");
    }
}
