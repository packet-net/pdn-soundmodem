using System.Diagnostics;
using AwesomeAssertions;
using M0LTE.Ardop.Host;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Ardop;

/// <summary>
/// Two pdn stations holding a real ARQ session with each other over a channel they share with a
/// packet modem, at a centre ARDOP itself cannot reach — the arrangement <c>becbdde</c> built and
/// could until now only test in pieces (the shift measured on a two-tone signal, the transmit
/// inhibit against a synthetic predicate).
/// </summary>
/// <remarks>
/// <para><b>Why pdn↔pdn.</b> ardopcf has no frequency-shift capability, so it cannot be the far
/// end of a shifted session by construction: the shift is a convention between two pdn stations,
/// and two pdn stations agreeing on it is exactly what has to work. <see cref="ArdopHostLiveTests"/>
/// remains the interop ground truth against ardopcf, at 1500 Hz.</para>
/// <para><b>What is real here.</b> Each station is wired as <c>Daemon/Program.cs</c> wires the one
/// it runs: an <see cref="ArdopHostTnc"/> whose <c>Transmitter</c> is the channel's
/// inhibit-bypassing transmit path, whose receive comes off
/// <see cref="SoundModemChannel.AddReceiveTap"/>, whose audio is moved to the configured centre by
/// <see cref="ArdopChannelShift"/> going out and back coming in, and which sets
/// <c>TransmitInhibit</c> to the engine's <c>IsConnected || IsPending</c>. The channel keeps its
/// shipped CSMA parameters, so ARDOP's bursts contend for the channel exactly as they do on air.
/// The only substitution is the sound card, and it is substituted faithfully: transmission is
/// paced at real time in 20 ms blocks and capture delivers 20 ms blocks continuously, silence
/// included — the modems' busy detectors are sample-clocked and a bench that only fed them audio
/// during bursts would leave them latched (which is how the first cut of this bench failed).</para>
/// <para><b>What the bench found.</b> ARDOP's own bursts bypass <c>TransmitInhibit</c> but still
/// go through the channel's p-persistent CSMA, so they wait on the packet modems' channel-busy
/// state — and a shifted centre puts ARDOP's signal inside the packet modem's passband, where its
/// energy busy detector sees it. It survives (the detector releases ~100 ms after the signal
/// stops), but a session at 950 Hz alongside an afsk1200 modem measured ~20 % slower at the
/// shipped p=63 than with the roll disabled. Nothing here asserts on that; it is why the bench
/// keeps the shipped CSMA settings rather than tuning them out.</para>
/// <para><b>Cost.</b> Each case runs in the airtime an ARQ session actually takes, around 25 s.
/// A sped-up bench clock was tried (the ARQ engine takes an injectable clock and the channel an
/// injectable <see cref="TimeProvider"/>, so every timer can be scaled together) and rejected on
/// measurement: at 2× and 4× the sessions became intermittent, because the bench's own thread
/// scheduling is the one thing that does not scale and it eats the turnaround margin. A slow test
/// that always passes beats a quick one that sometimes does. Even at real time the bench is
/// load-sensitive — one run in seven stalled a transfer past its wait on a box busy with another
/// build — so the waits are patient (90 s against a ~25 s session, ARQTIMEOUT at its 240 s
/// maximum): the assertions are unchanged, the bench is just allowed to ride out a stall.</para>
/// </remarks>
public class ArdopSharedChannelSessionTests(ITestOutputHelper output)
{
    private const int SampleRate = 12000;

    /// <summary>The 20 ms block the daemon captures and plays in.</summary>
    private const int BlockSamples = SampleRate / 50;

    private const int BlockMs = 20;

    /// <summary>Where the packet modem sits; ARDOP shares the same passband with it.</summary>
    private const int PacketSubChannel = 0;

    /// <summary>The centre from the worked config in <c>becbdde</c> — ARDOP low in the passband
    /// with packet modes above it.</summary>
    private const double ShiftedCentreHz = 950;

    // ------------------------------------------------------------------------------ the bench

    /// <summary>
    /// The playback device: hands each 20 ms block to the air as it plays and returns when the
    /// audio has really left, which is what the ARQ engine takes as the end of playout — the
    /// instant its repeat window opens. Paced against a deadline rather than by summing sleeps so
    /// a late thread-pool wake-up does not accumulate into drift.
    /// </summary>
    private sealed class PacedSink(int sampleRate, Action<float[]> onBlock) : IAudioOutput, IPttControl
    {
        public int SampleRate => sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            var elapsed = Stopwatch.StartNew();
            long played = 0;
            for (int i = 0; i < samples.Length; i += BlockSamples)
            {
                int count = Math.Min(BlockSamples, samples.Length - i);
                onBlock(samples.Slice(i, count).ToArray());
                played += count;
                int wait = (int)((played * 1000 / sampleRate) - elapsed.ElapsedMilliseconds);
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

    /// <summary>One station: a channel carrying a packet modem and an ARDOP TNC, wired the way the
    /// daemon wires them.</summary>
    private sealed class Station : IAsyncDisposable
    {
        private readonly ArdopChannelShift _shift;
        private readonly Task _transmitter;
        private readonly Task _poll;
        private readonly CancellationTokenSource _stopTransmitter = new();
        private readonly CancellationTokenSource _stopPoll = new();
        private readonly Queue<float[]> _inbound = new();

        public Station(string call, double? centreHz, int csmaSeed, Action<float[]> onBlock)
        {
            Call = call;
            // Independent p-persistence seeds: two real stations do not roll in lock step, and
            // sharing a seed here made them collide in step.
            Channel = new SoundModemChannel(SampleRate, randomSeed: csmaSeed);
            Channel.AddModem(PacketSubChannel, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
            Channel.FrameReceived += (_, frame) =>
            {
                lock (PacketFrames)
                {
                    PacketFrames.Add(frame);
                }
            };

            _shift = ArdopChannelShift.For(centreHz, SampleRate);
            Tnc = new ArdopHostTnc(captureDevice: "bench", playbackDevice: "bench")
            {
                // Program.cs's transmitter: ARDOP's own bursts bypass the inhibit they set.
                Transmitter = audio => Channel.EnqueueTransmit(
                    _ =>
                    {
                        var floats = new float[audio.Length];
                        for (int i = 0; i < audio.Length; i++)
                        {
                            floats[i] = audio[i] / 32768f;
                        }

                        return _shift.Transmit(floats);
                    },
                    rejected: null,
                    bypassInhibit: true),
            };
            Channel.AddReceiveTap(samples => Tnc.ProcessReceive(_shift.Receive(samples)));
            Channel.TransmitInhibit = () => Tnc.Engine.IsConnected || Tnc.Engine.IsPending;

            Tnc.CommandToHost += line =>
            {
                lock (Notifications)
                {
                    Notifications.Add(line);
                }
            };
            Tnc.DataToHost += (tag, data) =>
            {
                if (tag == "ARQ")
                {
                    lock (Received)
                    {
                        Received.AddRange(data);
                    }
                }
            };

            _transmitter = Channel.RunTransmitterAsync(
                new PacedSink(SampleRate, onBlock), new PacedSink(SampleRate, onBlock), _stopTransmitter.Token);
            _poll = PollAsync(_stopPoll.Token);
        }

        public string Call { get; }

        public SoundModemChannel Channel { get; }

        public ArdopHostTnc Tnc { get; }

        public List<string> Notifications { get; } = [];

        public List<byte> Received { get; } = [];

        public List<byte[]> PacketFrames { get; } = [];

        public bool IsConnected => Tnc.Engine.IsConnected;

        /// <summary>The station configuration Pat sets up, minus the dial.</summary>
        public void Configure(bool listen)
        {
            string[] commands =
            [
                "INITIALIZE", "PROTOCOLMODE ARQ", $"MYCALL {Call}", "GRIDSQUARE IO91WM",
                "ARQBW 500MAX", "ARQTIMEOUT 240", "FSKONLY TRUE",
                listen ? "LISTEN TRUE" : "LISTEN FALSE",
            ];
            foreach (string command in commands)
            {
                Tnc.ProcessCommand(command);
            }
        }

        /// <summary>Audio arriving from the other station, queued for the capture clock.</summary>
        public void Deliver(float[] block)
        {
            lock (_inbound)
            {
                _inbound.Enqueue(block);
            }
        }

        /// <summary>One capture block: what the other station is playing, or silence.</summary>
        public float[]? NextBlock()
        {
            lock (_inbound)
            {
                return _inbound.Count > 0 ? _inbound.Dequeue() : null;
            }
        }

        public byte[] ReceivedBytes()
        {
            lock (Received)
            {
                return [.. Received];
            }
        }

        public int PacketFrameCount()
        {
            lock (PacketFrames)
            {
                return PacketFrames.Count;
            }
        }

        public byte[] FirstPacketFrame()
        {
            lock (PacketFrames)
            {
                return PacketFrames[0];
            }
        }

        public string Transcript()
        {
            lock (Notifications)
            {
                return $"{Call}=[" + string.Join(" | ", Notifications.Where(n => !n.StartsWith("PTT ", StringComparison.Ordinal)).TakeLast(10)) + "]";
            }
        }

        private async Task PollAsync(CancellationToken cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    Tnc.Poll();
                    await Task.Delay(10, cancellation);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Order matters: stop asking the TNC to do anything, let it finish what it has (its
            // transmit worker awaits the channel, so the channel must outlive it), then stop the
            // channel.
            await _stopPoll.CancelAsync();
            try
            {
                await _poll;
            }
            catch (OperationCanceledException)
            {
            }

            await Tnc.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

            await _stopTransmitter.CancelAsync();
            try
            {
                await _transmitter;
            }
            catch (OperationCanceledException)
            {
            }

            _stopPoll.Dispose();
            _stopTransmitter.Dispose();
        }
    }

    /// <summary>Two stations and the air between them, with the capture clock that carries it.</summary>
    private sealed class Bench : IAsyncDisposable
    {
        private readonly Station[] _stations;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _capture;

        public Bench(double? centreHz)
        {
            Station[] stations = new Station[2];
            stations[0] = new Station("M0AAA", centreHz, 11, block => stations[1].Deliver(block));
            stations[1] = new Station("G8BBB", centreHz, 29, block => stations[0].Deliver(block));
            _stations = stations;
            _capture = CaptureAsync(_stop.Token);
        }

        public Station Caller => _stations[0];

        public Station Listener => _stations[1];

        /// <summary>
        /// The capture clock: every station receives one 20 ms block per 20 ms of wall time,
        /// whether or not anything is transmitting. Driven off elapsed time rather than a sleep
        /// count so a stalled thread pool makes the bench catch up rather than fall behind.
        /// </summary>
        private async Task CaptureAsync(CancellationToken cancellation)
        {
            var elapsed = Stopwatch.StartNew();
            var quiet = new float[BlockSamples];
            long delivered = 0;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(BlockMs, cancellation);
                    long due = elapsed.ElapsedMilliseconds / BlockMs;
                    while (delivered < due)
                    {
                        foreach (Station station in _stations)
                        {
                            station.Channel.ProcessReceive(station.NextBlock() ?? quiet);
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
            foreach (Station station in _stations)
            {
                await station.DisposeAsync();
            }

            await _stop.CancelAsync();
            try
            {
                await _capture;
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return predicate();
    }

    /// <summary>An AX.25 UI frame — the packet traffic that must stay off the air during a
    /// session.</summary>
    private static byte[] PacketFrame()
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(77).NextBytes(frame.AsSpan(16));
        return frame;
    }

    // ------------------------------------------------------------------------------- the tests

    [Theory]
    // The control: ARDOP's own centre, nothing in the path but a pass-through. If this fails too,
    // the bench is at fault rather than the shift.
    [InlineData(null)]
    [InlineData(ShiftedCentreHz)]
    public async Task An_Arq_Session_Carries_Data_Both_Ways_And_Holds_Packet_Traffic_Until_It_Ends(double? centreHz)
    {
        await using var bench = new Bench(centreHz);
        bench.Caller.Configure(listen: false);
        bench.Listener.Configure(listen: true);

        byte[] outbound = new byte[16];
        new Random(11).NextBytes(outbound);
        byte[] inbound = new byte[16];
        new Random(12).NextBytes(inbound);

        bench.Caller.Tnc.AcceptHostData(outbound);
        bench.Caller.Tnc.ProcessCommand($"ARQCALL {bench.Listener.Call} 10");

        (await WaitUntil(() => bench.Caller.IsConnected && bench.Listener.IsConnected, 90_000))
            .Should().BeTrue($"the session must connect. {bench.Caller.Transcript()} {bench.Listener.Transcript()}");
        int sessionBandwidth = bench.Caller.Tnc.Engine.SessionBandwidthHz;

        (await WaitUntil(() => bench.Listener.ReceivedBytes().Length >= outbound.Length, 90_000))
            .Should().BeTrue($"the caller's payload must arrive. {bench.Listener.Transcript()}");
        bench.Listener.ReceivedBytes().Should().Equal(outbound, "byte-exact ARQ delivery, caller to listener");

        // The other direction, which makes the listener take the channel over (BREAK) and become
        // the sending station.
        bench.Listener.Tnc.AcceptHostData(inbound);
        (await WaitUntil(() => bench.Caller.ReceivedBytes().Length >= inbound.Length, 90_000))
            .Should().BeTrue($"the listener's payload must come back. {bench.Caller.Transcript()}");
        bench.Caller.ReceivedBytes().Should().Equal(inbound, "byte-exact ARQ delivery, listener to caller");

        // With the session up and quiet, offer the packet modem a frame. TransmitInhibitTests
        // proves the mechanism against a synthetic predicate; this is it against the thing the
        // daemon actually points it at — a live ARQ engine, mid-session, on a channel the packet
        // modem really is sharing.
        bench.Listener.PacketFrameCount().Should().Be(0, "no packet traffic has been offered yet");
        Task packet = bench.Caller.Channel.EnqueueTransmit(PacketSubChannel, PacketFrame());

        // Long enough to span several of the session's own keyups, and far short of
        // TransmitInhibitTimeout's 30 s, past which a held frame is rejected instead.
        await Task.Delay(3000);
        bench.Caller.IsConnected.Should().BeTrue("the session is still up, so the hold is under test");
        packet.IsCompleted.Should().BeFalse("a packet frame must wait while the ARQ session holds the channel");
        bench.Listener.PacketFrameCount().Should().Be(0, "and none of it may reach the air");

        bench.Caller.Tnc.ProcessCommand("DISCONNECT");
        (await WaitUntil(() => !bench.Caller.IsConnected && !bench.Listener.IsConnected, 90_000))
            .Should().BeTrue($"the disconnect handshake must complete. {bench.Caller.Transcript()}");

        await packet.WaitAsync(TimeSpan.FromSeconds(60));
        (await WaitUntil(() => bench.Listener.PacketFrameCount() > 0, 60_000))
            .Should().BeTrue("the held frame must go out once the session releases the channel");
        bench.Listener.FirstPacketFrame().Should().Equal(PacketFrame(), "and arrive intact at the far station");

        output.WriteLine(
            $"centre {centreHz?.ToString("F0") ?? "1500 (native)"} Hz: connected at {sessionBandwidth} Hz, "
            + $"{outbound.Length} bytes out and {inbound.Length} back, both byte-exact; a packet frame "
            + "offered mid-session waited the session out and was decoded at the far station after it; "
            + "orderly disconnect.");
    }
}
