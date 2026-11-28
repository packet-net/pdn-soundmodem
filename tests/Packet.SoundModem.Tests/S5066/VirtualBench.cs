using System.Net;
using System.Text;
using System.Text.Json;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.S5066;

/// <summary>
/// Two STANAG 5066 stations and the air between them, on one fake clock that only the test moves.
/// </summary>
/// <remarks>
/// <para>Each station is a <see cref="SoundModemChannel"/> at 48 kHz with an afsk1200 modem, as the
/// daemon builds one, its transmitter loop, and an <see cref="S5066Station"/> on it. Both share one
/// <see cref="FakeTimeProvider"/>. <see cref="StepAsync"/> moves the clock one millisecond, and every
/// 20 ms first feeds each station one block of what the other has put on the air, silence and a
/// little noise when nothing is. The sound card is <see cref="Sink"/>: a write hands its samples to
/// the air at once and then blocks until the clock has passed the time they take to play, so a
/// keyup lasts the burst's virtual duration, as it does on a real card.</para>
/// <para><b>Nothing here waits on real time.</b> The clock moves only when every thread is at rest,
/// and rest is read from state and signals, never guessed. See <see cref="AtRest"/>.</para>
/// </remarks>
internal sealed class VirtualBench : IAsyncDisposable
{
    internal const int Rate = 48000;
    internal const int BlockMs = 20;
    private const int BlockSamples = Rate * BlockMs / 1000;

    // Only turns a hang into a failure: no correct run waits anywhere near this for one signal.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // Ten virtual minutes of air: a limit that turns "never" into a failure.
    private const long GiveUpMs = 10 * 60 * 1000;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _start;
    private readonly Station[] _stations = new Station[2];
    private readonly Signal _changed = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly StringBuilder _journal = new();
    private readonly Random _noise = new(5);
    private long _ms;
    private int _sent;
    private int _heard;

    public VirtualBench()
    {
        _start = _clock.GetUtcNow();
        for (int i = 0; i < 2; i++)
        {
            _stations[i] = new Station(this, i);
        }
    }

    /// <summary>Virtual milliseconds since the bench started.</summary>
    public long VirtualMs => Volatile.Read(ref _ms);

    /// <summary>Keyups started, both stations.</summary>
    public int Keyups => _stations.Sum(s => Volatile.Read(ref s.KeyupsStarted));

    /// <summary>Bursts the stations reported sent, for the journal and the frame log.</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>Bursts the stations reported heard with decoded bytes.</summary>
    public int Heard => Volatile.Read(ref _heard);

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
        Station st = _stations[station];
        void Note(string line)
        {
            lock (_journal)
            {
                _journal.AppendLine($"[{station}] t={VirtualMs} ms {line}");
            }
        }

        S5066Station started = S5066Station.Start(
            st.Channel, config, IPAddress.Loopback, Note,
            sent: burst =>
            {
                Interlocked.Increment(ref _sent);
                Note($"sent {burst.Waveform} {burst.Bytes.Length} bytes");
            },
            heard: burst =>
            {
                if (burst.Bytes.Length > 0)
                {
                    Interlocked.Increment(ref _heard);
                }

                Note($"heard {burst.Waveform} {burst.Bytes.Length} bytes, SNR {burst.SnrDb}, {burst.Ended}");
            },
            time: _clock,
            audio: inner => new CountingAudio(inner, st));

        // Subscribed after Start. Nothing is missed: the node is idle until a client speaks to it,
        // and the first park of its loop is read from Idle, not from the event.
        st.Host = started.Host;
        started.Host.Waiting += _ => _changed.Fire();
        started.Host.OutputProduced += output =>
        {
            Interlocked.Increment(ref st.Outputs);
            if (output is Transmit)
            {
                Interlocked.Increment(ref st.Asked);
            }

            _changed.Fire();
        };
        return started;
    }

    public string Journal()
    {
        lock (_journal)
        {
            return _journal.ToString();
        }
    }

    /// <summary>Blocks until every thread of both stations is at rest.</summary>
    public async Task SettleAsync()
    {
        while (true)
        {
            // Taken before the state is read, so a change in between is not lost.
            Task changed = _changed.Next();
            if (AtRest())
            {
                return;
            }

            await WaitAsync(changed);
        }
    }

    /// <summary>Blocks until a station's node has produced an output after the <paramref name="before"/>th, then settles.</summary>
    public async Task OutputAfterAsync(int station, int before)
    {
        while (true)
        {
            Task changed = _changed.Next();
            if (Volatile.Read(ref _stations[station].Outputs) > before)
            {
                break;
            }

            await WaitAsync(changed);
        }

        await SettleAsync();
    }

    /// <summary>How many outputs a station's node has produced.</summary>
    public int OutputsOf(int station) => Volatile.Read(ref _stations[station].Outputs);

    /// <summary>
    /// One virtual millisecond. On a block boundary each station is first fed a block of the other's
    /// air, both taken before either is fed so that what one station does in answer cannot change what
    /// the other hears in the same block; then the clock moves by one millisecond, the timers due fire,
    /// and the step ends once everything is at rest again.
    /// </summary>
    public async Task StepAsync()
    {
        if (_ms >= GiveUpMs)
        {
            throw new TimeoutException($"{GiveUpMs / 1000} s of virtual time passed and the exchange did not finish; journal:\n{Journal()}");
        }

        if (_ms % BlockMs == 0)
        {
            var blocks = new float[2][];
            for (int i = 0; i < 2; i++)
            {
                blocks[i] = _stations[1 - i].Air.Take(BlockSamples);
                for (int s = 0; s < blocks[i].Length; s++)
                {
                    // A little noise, so nothing downstream sees digital silence.
                    blocks[i][s] += (float)((_noise.NextDouble() - 0.5) * 0.002);
                }
            }

            for (int i = 0; i < 2; i++)
            {
                _stations[i].Channel.ProcessReceive(blocks[i]);
            }

            await SettleAsync();
        }

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Interlocked.Increment(ref _ms);
        await SettleAsync();
    }

    public Task<T> Guard<T>(Task<T> task) => task.WaitAsync(HangGuard);

    private static Task WaitAsync(Task changed) => changed.WaitAsync(HangGuard);

    /// <summary>
    /// True when no thread of either station is doing anything, so that moving the clock or feeding
    /// audio cannot race it. Per station, every thread in the path and how it is known to be at rest:
    /// <list type="bullet">
    /// <item>The node's loop: <see cref="M0LTE.S5066.Host.NodeHost.Idle"/>, which is true only while the loop is
    /// waiting and nothing is queued or due.</item>
    /// <item>The modulation task and the channel's transmitter: a burst asked of the modem
    /// (<c>Transmit</c> on <c>OutputProduced</c>) is in the pipeline until its <c>played</c> callback has
    /// returned (counted by <see cref="CountingAudio"/>), which is after the node's transmit-done is
    /// posted. While the transmitter is parked in a write (<see cref="Sink"/>) it is at rest, and the burst
    /// itself is still owed its <c>played</c> until the write after it. Otherwise it must have ended its
    /// keyup (<see cref="SoundModemChannel.KeyupEnded"/>, raised after receive reopens).</item>
    /// <item>The thread that runs <c>played</c> after a burst: the same count.</item>
    /// <item>Decoding, the busy poll and the fake timers' callbacks run on the thread that feeds audio or
    /// moves the clock, so are at rest whenever it is.</item>
    /// </list>
    /// </summary>
    private bool AtRest()
    {
        foreach (Station st in _stations)
        {
            if (st.Host is null || !st.Host.Idle)
            {
                return false;
            }

            int asked = Volatile.Read(ref st.Asked);
            int played = Volatile.Read(ref st.PlayedReturned);
            if (st.Sink.IsParked)
            {
                // In the burst's own write the burst is owed its played; in the tail's it is not.
                int owed = Volatile.Read(ref st.Announced) <= 1 ? 1 : 0;
                if (asked - played != owed)
                {
                    return false;
                }

                continue;
            }

            if (asked != played || Volatile.Read(ref st.KeyupsStarted) != Volatile.Read(ref st.KeyupsEnded))
            {
                return false;
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (Station st in _stations)
        {
            try
            {
                await st.Transmitter;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    /// <summary>One change-notification: <see cref="Next"/> is completed by the next <see cref="Fire"/>.</summary>
    private sealed class Signal
    {
        private readonly object _gate = new();
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Next()
        {
            lock (_gate)
            {
                return _next.Task;
            }
        }

        public void Fire()
        {
            TaskCompletionSource done;
            lock (_gate)
            {
                done = _next;
                _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            done.TrySetResult();
        }
    }

    /// <summary>Samples one station has put on the air and the other has not yet heard.</summary>
    private sealed class AirBuffer
    {
        private readonly object _gate = new();
        private readonly Queue<float[]> _chunks = new();
        private int _offset;

        public void Add(float[] samples)
        {
            lock (_gate)
            {
                _chunks.Enqueue(samples);
            }
        }

        /// <summary>The next <paramref name="count"/> samples, padded with silence.</summary>
        public float[] Take(int count)
        {
            var block = new float[count];
            lock (_gate)
            {
                int filled = 0;
                while (filled < count && _chunks.Count > 0)
                {
                    float[] chunk = _chunks.Peek();
                    int n = Math.Min(count - filled, chunk.Length - _offset);
                    Array.Copy(chunk, _offset, block, filled, n);
                    filled += n;
                    _offset += n;
                    if (_offset == chunk.Length)
                    {
                        _chunks.Dequeue();
                        _offset = 0;
                    }
                }
            }

            return block;
        }
    }

    private sealed class Station
    {
        public readonly SoundModemChannel Channel;
        public readonly AirBuffer Air = new();
        public readonly Sink Sink;
        public readonly Task Transmitter;
        public M0LTE.S5066.Host.NodeHost? Host;
        public int Outputs;
        public int Asked;
        public int PlayedReturned;
        public int KeyupsStarted;
        public int KeyupsEnded;
        public int Announced;
        public readonly Action Notify;

        public Station(VirtualBench bench, int index)
        {
            Notify = bench._changed.Fire;
            Channel = new SoundModemChannel(Rate, bench._clock, randomSeed: 11 + (18 * index));
            Channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", Rate, sink));
            Sink = new Sink(bench, this);
            Channel.TransmittingChanged += keyed =>
            {
                if (keyed)
                {
                    Interlocked.Exchange(ref Announced, 0);
                    Interlocked.Increment(ref KeyupsStarted);
                    bench._changed.Fire();
                }
            };
            Channel.TransmittedAudio += _ => Interlocked.Increment(ref Announced);
            Channel.KeyupEnded += () =>
            {
                Interlocked.Increment(ref KeyupsEnded);
                bench._changed.Fire();
            };
            Transmitter = Channel.RunTransmitterAsync(Sink, Sink, bench._stop.Token);
        }
    }

    /// <summary>
    /// The sound card and PTT. A write puts its samples on the air at once, then blocks until the fake
    /// clock reaches the end of everything written in this keyup, taken from the keyup's start and the
    /// running sample count and rounded up to a whole millisecond, so rounding does not accumulate. The
    /// block is a wait on a semaphore a fake timer releases.
    /// </summary>
    private sealed class Sink(VirtualBench bench, Station station) : IAudioOutput, IPttControl
    {
        private readonly object _gate = new();
        private long _keyStartMs;
        private long _written;
        private volatile bool _parked;

        public int SampleRate => Rate;

        /// <summary>True while the transmitter's thread is blocked in a write.</summary>
        public bool IsParked => _parked;

        public void Key()
        {
            _keyStartMs = bench.VirtualMs;
            _written = 0;
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            station.Air.Add(samples.ToArray());
            _written += samples.Length;
            long deadline = _keyStartMs + ((_written * 1000) + Rate - 1) / Rate;
            long wait = deadline - bench.VirtualMs;
            if (wait <= 0)
            {
                return;
            }

            using var release = new SemaphoreSlim(0);
            bool fired = false;
            using ITimer timer = bench._clock.CreateTimer(
                _ =>
                {
                    lock (_gate)
                    {
                        fired = true;
                        _parked = false;
                    }

                    release.Release();
                },
                null, TimeSpan.FromMilliseconds(wait), Timeout.InfiniteTimeSpan);
            lock (_gate)
            {
                _parked = !fired;
            }

            bench._changed.Fire();
            release.Wait(bench._stop.Token);
        }

        public void Drain()
        {
        }

        public void Unkey()
        {
        }
    }

    /// <summary>
    /// The channel's audio as the modem sees it, counting each burst until its <c>played</c> callback has
    /// returned, which is after the node's transmit-done has been posted.
    /// </summary>
    private sealed class CountingAudio(IAudioChannel inner, Station station) : IAudioChannel
    {
        public void Start(Action<ReadOnlyMemory<float>> received) => inner.Start(received);

        public void Play(float[] samples, Action played) =>
            inner.Play(
                samples,
                () =>
                {
                    played();
                    Interlocked.Increment(ref station.PlayedReturned);
                    station.Notify();
                });
    }
}
