using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.UberSdr;
using ZstdSharp;

using DaemonStation = Packet.SoundModem.Daemon.Station;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// What an <c>ubersdr:</c> device counts as lost audio, against a receiver on this machine that
/// speaks the instance's wire format: the counter the daemon reads per block to mark the channel
/// audio stream's gaps. A session picked up again leaves a hole, and so does audio dropped
/// because nothing drained the buffer; a Read that comes back empty does not, and that rule was
/// the 2026-10-11 bug (dozens of false gaps a slot, each one resetting a reader's modem).
/// </summary>
public sealed class UberSdrDiscontinuityTests
{
    /// <summary>One packet of IQ: 20 ms at 48 kHz.</summary>
    private const int PairsPerFrame = 960;

    /// <summary>Audio samples at 12 kHz in one packet.</summary>
    private const int AudioPerFrame = PairsPerFrame / 4;

    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(30);

    private static UberSdrTuning Tuning => new()
    {
        FrequencyHz = 7_052_000,
        OutputRate = 12000,
        StartupGuardMs = 0,
    };

    [Fact]
    public async Task A_Reconnect_Counts_One_Discontinuity()
    {
        using var receiver = new FakeReceiver([new Session(Frames: 25, Hold: false), new Session(Frames: 25, Hold: true)]);
        var clock = new FakeTimeProvider();
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, log: null, CancellationToken.None, clock);

        await DrainAsync(input, 20 * AudioPerFrame);
        input.Discontinuities.Should().Be(0, "the first session follows nothing");

        // The receiver closed the first session after half a second; the input backs off on its
        // clock before asking again.
        await Eventually.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return receiver.Sessions >= 2;
        }, "the input reconnects");
        await DrainAsync(input, 20 * AudioPerFrame);

        input.Discontinuities.Should().Be(1, "the time between the two sessions was not heard");
        input.DroppedSamples.Should().Be(0);
    }

    [Fact]
    public async Task Each_Reconnect_Is_A_New_Session_So_An_Instance_That_Refuses_An_Ended_One_Still_Serves_It()
    {
        // The instance times max_session_time from the first sight of a user_session_id and then
        // refuses that id for good; the start-up id used to be reused on every reconnect, which
        // locked a continuous station out three hours after each start (#736).
        using var receiver = new FakeReceiver(
            [new Session(Frames: 25, Hold: false), new Session(Frames: 25, Hold: true)],
            refuseEndedSessions: true);
        var clock = new FakeTimeProvider();
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, log: null, CancellationToken.None, clock);

        await DrainAsync(input, 20 * AudioPerFrame);
        await Eventually.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return receiver.Sessions >= 2;
        }, "the input reconnects");
        await DrainAsync(input, 20 * AudioPerFrame);

        receiver.Refused.Should().Be(0, "no session id was offered twice");
        receiver.SocketIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        receiver.PreflightIds.Should().Contain(receiver.SocketIds, "every session is asked about before it opens");
    }

    [Fact]
    public async Task Samples_Dropped_Because_The_Buffer_Filled_Count()
    {
        // Ten seconds of IQ into an eight-second ring that nobody reads. The receiver closes the
        // session after the last packet, and the input takes the packets and the close in order:
        // once it has said the session ended, every packet has been through the converter and
        // into the ring, and the drop count has stopped moving. Its clock is not moved, so it
        // stays in the wait before the next attempt.
        var journal = new List<string>();
        using var receiver = new FakeReceiver([new Session(Frames: 500, Hold: false)]);
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, line => { lock (journal) { journal.Add(line); } },
            CancellationToken.None, new FakeTimeProvider());

        await Eventually.UntilAsync(
            () =>
            {
                lock (journal)
                {
                    return journal.Any(l => l.Contains("reconnecting to", StringComparison.Ordinal)
                        || l.Contains("the session ended after", StringComparison.Ordinal));
                }
            },
            "the input to say the session ended, after taking all 500 packets");
        input.DroppedSamples.Should().BeGreaterThan(0, "ten seconds went into an eight-second ring");

        input.Discontinuities.Should().Be(0, "the count is as of the last Read, and nothing has been read");
        input.Read(new float[1200]).Should().Be(1200);
        input.Discontinuities.Should().Be(input.DroppedSamples,
            "every dropped sample is lost audio, and all of it came before the samples just read");
    }

    [Fact]
    public async Task A_Reconnect_Reaches_The_Channel_As_Lost_Audio_And_Empty_Reads_Do_Not()
    {
        using var receiver = new FakeReceiver([new Session(Frames: 25, Hold: false), new Session(Frames: 25, Hold: true)]);
        var clock = new FakeTimeProvider();
        using UberSdrAudioInput input = await UberSdrAudioInput.OpenAsync(
            receiver.Endpoint, Tuning, log: null, CancellationToken.None, clock);

        var channel = new SoundModemChannel(12000);
        var reads = new EmptyReadCounter(input);
        int losses = 0;
        long delivered = 0;
        long lostAt = -1;
        channel.ReceiveAudioLost += () =>
        {
            Interlocked.Increment(ref losses);
            Interlocked.Exchange(ref lostAt, Interlocked.Read(ref delivered));
        };
        channel.AddReceiveTap(samples => Interlocked.Add(ref delivered, samples.Length));
        using var stopping = new CancellationTokenSource();

        // Wired as UberSdrStationDevice wires it. The station runs on the real clock: the fake one
        // is the input's, for its reconnect backoff, and the starvation watch is left out of it.
        using var station = new DaemonStation(
            new StationOptions
            {
                Channel = channel,
                Input = reads,
                DspRate = 12000,
                Journal = new StationJournal("", _ => { }, _ => { }),
                DeviceKind = DeadFeedDevice.UberSdr,
                TimeProvider = TimeProvider.System,
                InputLossCount = () => input.Discontinuities,
            },
            stopping.Token);
        Task running = Task.Factory.StartNew(
            station.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Eventually.UntilAsync(() => Interlocked.Read(ref delivered) >= 20 * AudioPerFrame, "the first session is heard");

        // Empty reads while the receiver is between sessions: the loop turns, nothing is lost. The
        // station has been round on empty reads a few times once the counter says so; the input's
        // clock is not moved, so it cannot reconnect before then.
        await Eventually.UntilAsync(() => reads.EmptyReads >= 3, "the station to read the empty input a few times");
        Volatile.Read(ref losses).Should().Be(0, "a Read that comes back empty has lost nothing");

        long before = Interlocked.Read(ref delivered);
        await Eventually.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return receiver.Sessions >= 2;
        }, "the input reconnects");
        await Eventually.UntilAsync(() => Interlocked.Read(ref delivered) >= before + (20 * AudioPerFrame), "the second session is heard");

        Volatile.Read(ref losses).Should().Be(1, "one hole, between the two sessions");
        Interlocked.Read(ref lostAt).Should().Be(before,
            "the mark goes on the first block after the hole: every sample of the first session "
            + "came before it and none of the second");

        await stopping.CancelAsync();
        await Eventually.SignalAsync(running, "running to complete");
    }

    [Fact]
    public async Task The_Test_Receiver_Lets_Its_Connections_Finish_Before_It_Closes_Its_Listener()
    {
        // The teardown race of #754, held still. Closing the listener closes every connection it
        // still holds, and a connection that is at that moment letting go of its own socket makes
        // that Close throw ObjectDisposedException. The connection here is held part way through
        // letting go, on a gate; the receiver is disposed meanwhile. It has to wait for the
        // connection, with the listener still open, rather than close the listener over it.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new FakeReceiver([new Session(Frames: 1, Hold: true)])
        {
            BeforeConnectionTeardown = async () =>
            {
                parked.TrySetResult();
                await release.Task;
            },
        };
        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{receiver.Endpoint.Port}/ws?user_session_id=held"), CancellationToken.None);
        await client.ReceiveAsync(new byte[64 * 1024], CancellationToken.None);

        Task disposing = Task.Run(receiver.Dispose);
        try
        {
            // Either the receiver is waiting for its connections, or it has already closed the
            // listener without: both are reached by construction, whichever way it is written.
            await Eventually.SignalAsync(Task.WhenAny(receiver.WaitingForConnections, receiver.ListenerClosed), "Task.WhenAny(receiver.WaitingForConnections, receiver.ListenerClosed) to complete");
            await Eventually.SignalAsync(parked.Task, "parked.Task to complete");
            receiver.ListenerOpen.Should().BeTrue("a connection is still letting go of its socket");
        }
        finally
        {
            release.TrySetResult();
        }

        await Eventually.SignalAsync(disposing, "disposing to complete");
        receiver.ListenerOpen.Should().BeFalse("once the connection has finished the listener is closed");
    }

    private static async Task DrainAsync(UberSdrAudioInput input, int samples)
    {
        var buffer = new float[1200];
        int taken = 0;
        using var giveUp = new CancellationTokenSource(GiveUp);
        while (taken < samples)
        {
            giveUp.Token.ThrowIfCancellationRequested();
            taken += input.Read(buffer);
            await Task.Yield();
        }
    }

    /// <summary>The input the station reads from, counting the reads that come back empty after
    /// it has delivered something, which is what a receiver between sessions looks like.</summary>
    private sealed class EmptyReadCounter(UberSdrAudioInput inner) : IAudioInput
    {
        private long _empty;
        private long _delivered;

        public long EmptyReads => Interlocked.Read(ref _empty);

        public int SampleRate => inner.SampleRate;

        public int Read(Span<float> destination)
        {
            int got = inner.Read(destination);
            if (got > 0)
            {
                Interlocked.Add(ref _delivered, got);
            }
            else if (Interlocked.Read(ref _delivered) > 0)
            {
                Interlocked.Increment(ref _empty);
            }

            return got;
        }
    }

    /// <summary>One WebSocket session the fake receiver serves: so many packets, then either a
    /// close (the instance's session limit) or a held-open socket.</summary>
    private sealed record Session(int Frames, bool Hold);

    /// <summary>
    /// An UberSDR instance on loopback: the pre-flight, no description, and an IQ WebSocket that
    /// serves one scripted session per connection in zstd-wrapped "PC"/"PM" packets of a tone.
    /// </summary>
    private sealed class FakeReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Session[] _script;
        private readonly bool _refuseEndedSessions;
        private readonly Task _serving;
        private readonly List<Task> _connections = [];
        private readonly TaskCompletionSource _waitingForConnections = new();
        private readonly TaskCompletionSource _listenerClosed = new();
        private bool _closing;
        private readonly List<string> _preflightIds = [];
        private readonly List<string> _socketIds = [];
        private int _sessions;
        private int _framesSent;
        private int _refused;

        /// <param name="script">One session per connection; the last one repeats.</param>
        /// <param name="refuseEndedSessions">Close a socket at once when its user_session_id has
        /// been served before, as an instance does to an id past max_session_time.</param>
        public FakeReceiver(Session[] script, bool refuseEndedSessions = false)
        {
            _script = script;
            _refuseEndedSessions = refuseEndedSessions;
            int port = FreePorts.Next();
            Endpoint = new UberSdrEndpoint("127.0.0.1", port, Ssl: false);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public UberSdrEndpoint Endpoint { get; }

        /// <summary>WebSocket sessions opened so far.</summary>
        public int Sessions => Volatile.Read(ref _sessions);

        /// <summary>IQ packets sent so far, over every session.</summary>
        public int FramesSent => Volatile.Read(ref _framesSent);

        /// <summary>Sockets closed at once because their session id had been served before.</summary>
        public int Refused => Volatile.Read(ref _refused);

        /// <summary>Test seam: runs on a connection's own task once its session is over and before
        /// it lets go of its socket, so a test can hold a connection part way through tearing down.</summary>
        public Func<Task>? BeforeConnectionTeardown { get; set; }

        /// <summary>Whether the listener is still accepting and holding connections.</summary>
        public bool ListenerOpen => _listener.IsListening;

        /// <summary>Completes when <see cref="Dispose"/> has told the connections to finish and is
        /// waiting for them, with the listener still open.</summary>
        public Task WaitingForConnections => _waitingForConnections.Task;

        /// <summary>Completes when <see cref="Dispose"/> has closed the listener.</summary>
        public Task ListenerClosed => _listenerClosed.Task;

        /// <summary>The user_session_id of every pre-flight, in order.</summary>
        public IReadOnlyList<string> PreflightIds
        {
            get
            {
                lock (_preflightIds)
                {
                    return [.. _preflightIds];
                }
            }
        }

        /// <summary>The user_session_id of every socket served, in order.</summary>
        public IReadOnlyList<string> SocketIds
        {
            get
            {
                lock (_socketIds)
                {
                    return [.. _socketIds];
                }
            }
        }

        public void Dispose()
        {
            // Connections first, listener last. Closing the listener closes every connection it still
            // holds, writing the end of the response to each; a connection whose own task is at that
            // moment letting go of its socket (the session was just cancelled) has its stream disposed
            // between the listener's "can I write" check and the write, and the listener's Close throws
            // ObjectDisposedException on the test's thread. So the connections finish, and unhook
            // themselves, before the listener is closed. Seen once on CI (#754).
            _stop.Cancel();
            Task[] serving;
            lock (_connections)
            {
                _closing = true;
                serving = [.. _connections];
            }

            _waitingForConnections.TrySetResult();
            try
            {
                Task.WaitAll(serving, TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // A connection that failed on its way out has nothing left to tell the test.
            }

            try
            {
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // A keep-alive connection nobody serves a task for can still be hanging up on its own
                // thread as the listener closes it; the connection being gone is what is wanted.
            }

            _listenerClosed.TrySetResult();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            var running = new List<Task>();
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException
                                            or InvalidOperationException)
                {
                    break;
                }

                lock (_connections)
                {
                    if (_closing)
                    {
                        // Left for the listener to close: Dispose is no longer waiting for new tasks.
                        continue;
                    }

                    Task connection = Task.Run(() => AnswerAsync(context));
                    _connections.Add(connection);
                    running.Add(connection);
                }
            }

            await Eventually.SignalAsync(Task.WhenAll(running), "Task.WhenAll(running) to complete").ConfigureAwait(false);
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            string path = context.Request.Url!.AbsolutePath;
            if (path == "/connection")
            {
                using (var reader = new StreamReader(context.Request.InputStream))
                {
                    string request = await reader.ReadToEndAsync();
                    string? id = System.Text.Json.Nodes.JsonNode.Parse(request)?["user_session_id"]?.GetValue<string>();
                    lock (_preflightIds)
                    {
                        _preflightIds.Add(id ?? "");
                    }
                }

                byte[] body = Encoding.UTF8.GetBytes(
                    """{"allowed": true, "max_session_time": 3600, "allowed_iq_modes": ["iq48"]}""");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
                return;
            }

            if (path != "/ws" || !context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            string sessionId = context.Request.QueryString["user_session_id"] ?? "";
            bool seen;
            lock (_socketIds)
            {
                seen = _socketIds.Contains(sessionId);
                if (!seen)
                {
                    _socketIds.Add(sessionId);
                }
            }

            if (seen && _refuseEndedSessions)
            {
                // What the instance does to a kicked id: accept the upgrade, then drop it.
                Interlocked.Increment(ref _refused);
                HttpListenerWebSocketContext refused = await context.AcceptWebSocketAsync(null);
                refused.WebSocket.Abort();
                refused.WebSocket.Dispose();
                return;
            }

            int index = Interlocked.Increment(ref _sessions) - 1;
            HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(null);
            using WebSocket socket = socketContext.WebSocket;
            Session session = _script[Math.Min(index, _script.Length - 1)];
            try
            {
                using var compressor = new Compressor();
                ulong gpsNanos = 1_000_000_000UL * (ulong)(index + 1) * 1000;
                for (int f = 0; f < session.Frames; f++)
                {
                    byte[] frame = Frame(gpsNanos, full: f == 0);
                    await socket.SendAsync(compressor.Wrap(frame).ToArray(), WebSocketMessageType.Binary, true, _stop.Token);
                    Interlocked.Increment(ref _framesSent);
                    gpsNanos += 20_000_000UL;
                }

                if (session.Hold)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session limit", CancellationToken.None);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }

            if (BeforeConnectionTeardown is { } beforeTeardown)
            {
                await beforeTeardown();
            }
        }

        /// <summary>One packet: a 1 kHz tone as big-endian I/Q, behind a full or minimal header.</summary>
        private static byte[] Frame(ulong gpsNanos, bool full)
        {
            int header = full ? 29 : 13;
            var frame = new byte[header + (PairsPerFrame * 4)];
            if (full)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x5043); // "PC"
                frame[2] = 1;
                frame[3] = 2;
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(4), gpsNanos);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20), 48000);
                frame[24] = 2;
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x504D); // "PM"
                frame[2] = 1;
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(3), gpsNanos);
            }

            for (int n = 0; n < PairsPerFrame; n++)
            {
                double phase = 2 * Math.PI * 1000 * n / 48000.0;
                BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(header + (n * 4)), (short)(8000 * Math.Cos(phase)));
                BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(header + (n * 4) + 2), (short)(8000 * Math.Sin(phase)));
            }

            return frame;
        }
    }
}
