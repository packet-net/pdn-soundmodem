using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using M0LTE.Ardop;
using M0LTE.Ardop.Arq;
using Packet.SoundModem.Audio;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Ardop;

/// <summary>
/// The decisive live tests: full ARQ sessions between our engine and a live ardopcf
/// (git a7c9228) over the snd-aloop virtual audio cable, both roles — both ends
/// FSKONLY (the Phase B exit, docs/ardop-design.md §6.2 rung 3 / §7) and unrestricted
/// mixed-mode where the gearshift must climb into the PSK/QAM rungs (the Phase C
/// exit). ardopcf runs its own ALSA on loopback device 0 and is driven over its TCP
/// host interface; our side is the same <see cref="ArdopArqStation"/> the hermetic
/// tests use, pumped by a real 20 ms ALSA duplex loop on device 1 — the audio clock,
/// not the virtual one.
/// </summary>
/// <remarks>
/// Gated on two environment variables so CI stays hermetic: <c>ARDOPCF</c> (path to the
/// ardopcf binary) and <c>ARDOP_ALOOP_CARD</c> (the snd-aloop card index, e.g. 4).
/// ALSA device access needs the audio group: run under <c>sg audio</c>
/// (docs/qtsm-loop.md). One ardopcf instance at a time — the tests are serialized by
/// xUnit's per-class collection.
/// </remarks>
public class ArdopArdopcfLiveSessionTests(ITestOutputHelper output)
{
    // Fresh port pair per run: a crashed earlier run can leave an ardopcf squatting
    // the previous ports (observed live — a stale instance made both legs fail).
    private static readonly int HostPort = 8600 + (Environment.ProcessId % 200);

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

    /// <summary>ardopcf + its TCP host interface (command port, data port = +1).</summary>
    private sealed class ArdopcfHost : IDisposable
    {
        private readonly Process _process;
        private readonly TcpClient _command;
        private readonly TcpClient _data;
        private readonly List<string> _notifications = [];
        private readonly List<byte> _arqData = [];
        private readonly CancellationTokenSource _stop = new();

        public ArdopcfHost(string binary, int card)
        {
            _process = Process.Start(new ProcessStartInfo(
                binary, $"--nologfile {HostPort} plughw:{card},0 plughw:{card},0")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _command = ConnectWithRetry(HostPort);
            _data = ConnectWithRetry(HostPort + 1);
            _ = Task.Run(ReadCommandSocket);
            _ = Task.Run(ReadDataSocket);
        }

        public IReadOnlyList<string> Notifications
        {
            get
            {
                lock (_notifications)
                {
                    return [.. _notifications];
                }
            }
        }

        public byte[] ArqData
        {
            get
            {
                lock (_arqData)
                {
                    return [.. _arqData];
                }
            }
        }

        public void Command(string command)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(command + "\r");
            _command.GetStream().Write(bytes);
        }

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
                if (predicate(Notifications))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return predicate(Notifications);
        }

        public bool WaitForData(int byteCount, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ArqData.Length >= byteCount)
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return ArqData.Length >= byteCount;
        }

        private static TcpClient ConnectWithRetry(int port)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return new TcpClient("127.0.0.1", port);
                }
                catch (SocketException) when (attempt < 50)
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
                var buffer = new byte[512];
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
                            lock (_notifications)
                            {
                                _notifications.Add(line.ToString());
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
                    // TNC→host: [2-byte BE length][3-byte tag][payload]
                    // (TCPAddTagToDataAndSendToHost, TCPHostInterface.c:220).
                    byte[] header = ReadExactly(stream, 2);
                    int length = (header[0] << 8) + header[1];
                    byte[] body = ReadExactly(stream, length);
                    string tag = Encoding.ASCII.GetString(body, 0, 3);
                    if (tag == "ARQ")
                    {
                        lock (_arqData)
                        {
                            _arqData.AddRange(body.AsSpan(3).ToArray());
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
            try
            {
                _command.Dispose();
                _data.Dispose();
            }
            finally
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }

                _process.Dispose();
            }
        }
    }

    /// <summary>Our station pumped by a real ALSA duplex loop in 20 ms blocks — the
    /// audio-clock driver of the same engine the hermetic tests run.</summary>
    private sealed class LiveStation : IDisposable
    {
        private readonly int _card;
        private AlsaPcm _capture;
        private readonly AlsaPcm _playback;
        private readonly Thread _pump;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _lock = new();
        private int _captureReopens;

        public ArdopArqStation Station { get; }

        public List<string> Notes { get; } = [];

        public List<byte> Received { get; } = [];

        public LiveStation(int card, ArdopArqConfig config)
        {
            _card = card;
            Station = new ArdopArqStation(config);
            Station.Engine.HostNotification += n =>
            {
                lock (Notes)
                {
                    Notes.Add(n);
                }
            };
            Station.Engine.DataReceived += data =>
            {
                lock (Received)
                {
                    Received.AddRange(data);
                }
            };

            _capture = AlsaPcm.Open($"plughw:{card},1", AlsaPcm.Direction.Capture, 1, 12000, 60_000);
            _playback = AlsaPcm.Open($"plughw:{card},1", AlsaPcm.Direction.Playback, 1, 12000, 60_000);
            _playback.Write(new short[1200]);  // prime ~100 ms so playback never starves

            _pump = new Thread(Pump) { IsBackground = true, Name = "ardop-live-pump" };
            _pump.Start();
        }

        /// <summary>Runs an engine command on the pump clock (the station is
        /// single-threaded by design).</summary>
        public T WithStation<T>(Func<ArdopArqStation, T> action)
        {
            lock (_lock)
            {
                return action(Station);
            }
        }

        public void WithStation(Action<ArdopArqStation> action) =>
            WithStation<object?>(s =>
            {
                action(s);
                return null;
            });

        public bool WaitFor(Func<ArdopArqStation, bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (WithStation(condition))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return WithStation(condition);
        }

        public int CaptureXruns => _capture.Xruns;

        public int CaptureReopens => _captureReopens;

        private void Pump()
        {
            var rx = new short[240];
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    _capture.Read(rx);  // blocks 20 ms — the audio clock
                }
                catch (InvalidOperationException)
                {
                    // snd-aloop invalidates a running capture with EIO whenever the
                    // linked playback re-opens with fresh hw params — which ardopcf
                    // does per transmission (SHARECAPTURE, ALSASound.c:27). Reopen
                    // and continue; the ≥240 ms leader absorbs the gap, and pacing
                    // falls to the playback side meanwhile.
                    _capture.Dispose();
                    _capture = AlsaPcm.Open(
                        $"plughw:{_card},1", AlsaPcm.Direction.Capture, 1, 12000, 60_000);
                    _captureReopens++;
                    Array.Clear(rx);
                }

                short[] tx;
                lock (_lock)
                {
                    tx = Station.Step(rx);
                }

                _playback.Write(tx);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _pump.Join(2000);
            _capture.Dispose();
            _playback.Dispose();
        }
    }

    private static ArdopArqConfig OurConfig(bool fskOnly = true, string bandwidth = "500") => new()
    {
        MyCall = Station("M0AAA"),
        GridSquare = "IO81VK",
        ArqBandwidth = bandwidth switch
        {
            "500" => ArdopBandwidth.B500Max,
            "1000" => ArdopBandwidth.B1000Max,
            _ => ArdopBandwidth.B2000Max,
        },
        FskOnly = fskOnly,
        ArqTimeoutSeconds = 60,
    };

    private static ArdopStationId Station(string call)
    {
        ArdopStationId.TryParse(call, out var id).Should().BeTrue();
        return id;
    }

    private static void ConfigureArdopcf(ArdopcfHost cf, bool fskOnly = true, string bandwidth = "500")
    {
        cf.Command("INITIALIZE");
        cf.Command("MYCALL G8BBB");
        cf.Command("GRIDSQUARE IO92XX");
        cf.Command("PROTOCOLMODE ARQ");
        cf.Command($"ARQBW {bandwidth}MAX");
        cf.Command($"FSKONLY {(fskOnly ? "TRUE" : "FALSE")}");
        cf.Command("ARQTIMEOUT 60");
        cf.Command("ENABLEPINGACK TRUE");
        cf.Command("LISTEN TRUE");
        cf.WaitFor(n => n.Contains("LISTEN now TRUE"), 5000).Should().BeTrue(
            "ardopcf must acknowledge the setup commands");
    }

    [SkippableFact]
    public void Our_Station_Calls_Ardopcf_And_Transfers_Data()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        byte[] payload = new byte[64];
        new Random(99).NextBytes(payload);

        using var cf = new ArdopcfHost(rig!.Value.Binary, rig.Value.Card);
        ConfigureArdopcf(cf);
        using var us = new LiveStation(rig.Value.Card, OurConfig());

        var framesOnAir = new List<string>();
        us.WithStation(s => s.FrameTransmitted += (request, _) =>
        {
            lock (framesOnAir)
            {
                framesOnAir.Add($"{ArdopFrameType.Name(request.Type)}{(request.IsRepeat ? "(rpt)" : "")}");
            }
        });

        // Queue our data, then call: we are the ISS.
        us.WithStation(s => s.Engine.EnqueueData(payload));
        us.WithStation(s => s.Engine.ConnectRequest(Station("G8BBB"), s.NowMs).Should().BeTrue());

        us.WaitFor(s => s.Engine.IsConnected, 60_000).Should().BeTrue("our ConReq must be answered");
        cf.WaitFor(n => n.Any(x => x.StartsWith("CONNECTED M0AAA")), 30_000)
            .Should().BeTrue("ardopcf must report the session");

        cf.WaitForData(payload.Length, 120_000).Should().BeTrue("all data must reach ardopcf's host");
        cf.ArqData.Should().Equal(payload);

        // Orderly teardown from our side.
        us.WithStation(s => s.Engine.Disconnect(s.NowMs));
        cf.WaitFor(n => n.Contains("DISCONNECTED"), 60_000).Should().BeTrue();
        us.WaitFor(s => s.Engine.State == ArdopProtocolState.Disc, 60_000).Should().BeTrue();

        output.WriteLine(
            $"ours→ardopcf: connected, {payload.Length} bytes delivered byte-exact, " +
            $"clean teardown; our stats: acks={us.WithStation(s => s.Engine.Stats.DataAcksReceived)}, " +
            $"naks={us.WithStation(s => s.Engine.Stats.NaksReceived)}, " +
            $"repeats={us.WithStation(s => s.Engine.Stats.RepeatsSent)}, " +
            $"captureReopens={us.CaptureReopens}, xruns={us.CaptureXruns}");
        lock (framesOnAir)
        {
            output.WriteLine($"our frames on air: {string.Join(", ", framesOnAir)}");
        }
    }

    [SkippableFact]
    public void Ardopcf_Calls_Our_Station_And_Transfers_Data()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        byte[] payload = new byte[48];
        new Random(77).NextBytes(payload);

        using var cf = new ArdopcfHost(rig!.Value.Binary, rig.Value.Card);
        ConfigureArdopcf(cf);
        using var us = new LiveStation(rig.Value.Card, OurConfig());

        // ardopcf is the ISS: queue its data, then have it call us.
        cf.SendData(payload);
        cf.Command("ARQCALL M0AAA 5");

        us.WaitFor(s => s.Engine.IsConnected, 60_000)
            .Should().BeTrue("we must answer ardopcf's ConReq and connect");
        cf.WaitFor(n => n.Any(x => x.StartsWith("CONNECTED M0AAA")), 30_000).Should().BeTrue();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 120_000)
        {
            lock (us.Received)
            {
                if (us.Received.Count >= payload.Length)
                {
                    break;
                }
            }

            Thread.Sleep(100);
        }

        lock (us.Received)
        {
            us.Received.Should().Equal(payload, "ardopcf's data must arrive byte-exact");
        }

        // Teardown from ardopcf's side.
        cf.Command("DISCONNECT");
        us.WaitFor(s => s.Engine.State == ArdopProtocolState.Disc, 60_000).Should().BeTrue();
        cf.WaitFor(n => n.Contains("DISCONNECTED"), 60_000).Should().BeTrue();

        output.WriteLine(
            $"ardopcf→ours: connected, {payload.Length} bytes received byte-exact, clean teardown; " +
            $"our stats: naksSent={us.WithStation(s => s.Engine.Stats.NaksSent)}, " +
            $"captureReopens={us.CaptureReopens}, xruns={us.CaptureXruns}");
    }

    // ------------------------------------------------- Phase C: mixed-mode sessions

    private static bool IsPskOrQamData(byte type) =>
        ArdopFrameType.IsData(type) && ArdopFrameInfo.Get(type).Modulation != ArdopModulation.Fsk4;

    [SkippableFact]
    public void Mixed_Mode_Session_Ours_As_Iss_Climbs_To_Psk_Qam_Rungs()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        byte[] payload = new byte[4096];
        new Random(55).NextBytes(payload);

        using var cf = new ArdopcfHost(rig!.Value.Binary, rig.Value.Card);
        ConfigureArdopcf(cf, fskOnly: false, bandwidth: "2000");
        using var us = new LiveStation(rig.Value.Card, OurConfig(fskOnly: false, bandwidth: "2000"));

        var dataFramesOnAir = new List<byte>();
        us.WithStation(s => s.FrameTransmitted += (request, _) =>
        {
            if (ArdopFrameType.IsData(request.Type))
            {
                lock (dataFramesOnAir)
                {
                    dataFramesOnAir.Add(request.Type);
                }
            }
        });

        us.WithStation(s => s.Engine.EnqueueData(payload));
        us.WithStation(s => s.Engine.ConnectRequest(Station("G8BBB"), s.NowMs).Should().BeTrue());

        us.WaitFor(s => s.Engine.IsConnected, 60_000).Should().BeTrue("our ConReq must be answered");
        cf.WaitFor(n => n.Any(x => x.StartsWith("CONNECTED M0AAA 2000")), 30_000)
            .Should().BeTrue("ardopcf must report the 2000 Hz session");

        cf.WaitForData(payload.Length, 300_000).Should().BeTrue("all data must reach ardopcf's host");
        cf.ArqData.Should().Equal(payload);

        lock (dataFramesOnAir)
        {
            dataFramesOnAir.Should().Contain(t => IsPskOrQamData(t),
                "the gearshift must climb off the FSK rungs on a clean cable");
        }

        us.WithStation(s => s.Engine.Disconnect(s.NowMs));
        cf.WaitFor(n => n.Contains("DISCONNECTED"), 60_000).Should().BeTrue();
        us.WaitFor(s => s.Engine.State == ArdopProtocolState.Disc, 60_000).Should().BeTrue();

        lock (dataFramesOnAir)
        {
            output.WriteLine(
                $"ours→ardopcf mixed-mode: {payload.Length} bytes byte-exact over 2000 Hz; " +
                $"data frames on air: {string.Join(", ", dataFramesOnAir.Select(ArdopFrameType.Name))}; " +
                $"shiftUps={us.WithStation(s => s.Engine.Gearshift.ShiftUps)}, " +
                $"acks={us.WithStation(s => s.Engine.Stats.DataAcksReceived)}, " +
                $"naks={us.WithStation(s => s.Engine.Stats.NaksReceived)}, " +
                $"repeats={us.WithStation(s => s.Engine.Stats.RepeatsSent)}, " +
                $"captureReopens={us.CaptureReopens}, xruns={us.CaptureXruns}");
        }
    }

    [SkippableFact]
    public void Mixed_Mode_Session_Ardopcf_As_Iss_Sends_Us_Psk_Qam_Frames()
    {
        var rig = Rig();
        Skip.If(rig is null, "set ARDOPCF and ARDOP_ALOOP_CARD (run under sg audio) for the live leg");

        byte[] payload = new byte[4096];
        new Random(66).NextBytes(payload);

        using var cf = new ArdopcfHost(rig!.Value.Binary, rig.Value.Card);
        ConfigureArdopcf(cf, fskOnly: false, bandwidth: "2000");
        using var us = new LiveStation(rig.Value.Card, OurConfig(fskOnly: false, bandwidth: "2000"));

        var dataFramesDecoded = new List<byte>();
        us.WithStation(s => s.FrameDecoded += (frame, _) =>
        {
            if (frame.Ok && ArdopFrameType.IsData(frame.Type))
            {
                lock (dataFramesDecoded)
                {
                    dataFramesDecoded.Add(frame.Type);
                }
            }
        });

        cf.SendData(payload);
        cf.Command("ARQCALL M0AAA 5");

        us.WaitFor(s => s.Engine.IsConnected, 60_000)
            .Should().BeTrue("we must answer ardopcf's ConReq and connect");
        cf.WaitFor(n => n.Any(x => x.StartsWith("CONNECTED M0AAA 2000")), 30_000).Should().BeTrue();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 300_000)
        {
            lock (us.Received)
            {
                if (us.Received.Count >= payload.Length)
                {
                    break;
                }
            }

            Thread.Sleep(100);
        }

        lock (us.Received)
        {
            us.Received.Should().Equal(payload, "ardopcf's data must arrive byte-exact");
        }

        lock (dataFramesDecoded)
        {
            dataFramesDecoded.Should().Contain(t => IsPskOrQamData(t),
                "ardopcf's gearshift must have picked PSK/QAM rungs and we must have decoded them");
        }

        cf.Command("DISCONNECT");
        us.WaitFor(s => s.Engine.State == ArdopProtocolState.Disc, 60_000).Should().BeTrue();
        cf.WaitFor(n => n.Contains("DISCONNECTED"), 60_000).Should().BeTrue();

        lock (dataFramesDecoded)
        {
            output.WriteLine(
                $"ardopcf→ours mixed-mode: {payload.Length} bytes byte-exact over 2000 Hz; " +
                $"data frames decoded: {string.Join(", ", dataFramesDecoded.Select(ArdopFrameType.Name))}; " +
                $"naksSent={us.WithStation(s => s.Engine.Stats.NaksSent)}, " +
                $"captureReopens={us.CaptureReopens}, xruns={us.CaptureXruns}");
        }
    }
}
