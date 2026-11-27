using M0LTE.OfdmFm;
using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems.OfdmFmStation;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>A radio carrier source a test drives. Null: does not know.</summary>
internal sealed class FakeRadio : IChannelBusySource
{
    private volatile int _state = -1;

    public bool? Busy => _state switch { 0 => false, 1 => true, _ => null };

    public void Set(bool? busy) => _state = busy is null ? -1 : busy.Value ? 1 : 0;
}

/// <summary>One keyup as the sound card and the PTT saw it, and the receive count at each edge.</summary>
internal sealed record Keyup(long StartCount, long EndCount, float[] Audio);

/// <summary>
/// A sound card and PTT whose every sample written is also, at once and on the same thread, a keyed receive block on its own channel, as the capture loop would deliver them while the radio is keyed,
/// and the audio the far end hears. So the receive side advances exactly as the transmitter plays, with no clock and no waiting: the test feeds quiet blocks, and while a keyup is running it feeds none.
/// </summary>
internal sealed class StationAir(SoundModemChannel channel, VirtualAir.Clock clock, Func<long> count) : IAudioOutput, IPttControl
{
    public const int Block = 960;

    private static readonly float[] Silence = new float[Block];
    private readonly Lock _gate = new();
    private readonly List<Keyup> _keyups = [];
    private List<float> _audio = [];
    private long _startCount;
    private int _keys;
    private int _failKeys;
    private volatile bool _keyed;

    /// <summary>Where a block of what this radio radiates goes (the far station's receiver). Null: nowhere. Set before the first keyup.</summary>
    public Action<ReadOnlySpan<float>>? Radiated { get; set; }

    /// <summary>Held for the whole of every write, so that two radios in one test cannot play at once: there is one timeline.</summary>
    public Lock Timeline { get; set; } = new();

    /// <summary>True: the capture loop delivers a keyed block as soon as the PTT is down, before any audio is written (as it does on a real card, where blocks are never late).</summary>
    public bool BlockAtKey { get; set; }

    /// <summary>Called on the transmitter's thread when the PTT goes down, before anything else.</summary>
    public Action? OnKey { get; set; }

    /// <summary>Called on the transmitter's thread when the PTT goes up, before the keyup is recorded.</summary>
    public Action? OnUnkey { get; set; }

    public int SampleRate => channel.SampleRate;

    /// <summary>True from key to unkey.</summary>
    public bool Keyed => _keyed;

    /// <summary>How many times the PTT was taken down, counting the ones that threw.</summary>
    public int KeyAttempts => Volatile.Read(ref _keys);

    /// <summary>Makes the next <paramref name="times"/> key-ups throw.</summary>
    public void FailNextKeys(int times) => Volatile.Write(ref _failKeys, times);

    /// <summary>Every completed keyup, in order.</summary>
    public IReadOnlyList<Keyup> Keyups
    {
        get
        {
            lock (_gate)
            {
                return [.. _keyups];
            }
        }
    }

    public void Key()
    {
        Interlocked.Increment(ref _keys);
        if (Volatile.Read(ref _failKeys) > 0)
        {
            Interlocked.Decrement(ref _failKeys);
            throw new InvalidOperationException("the PTT did not key");
        }

        _startCount = count();
        _audio = [];
        _keyed = true;
        OnKey?.Invoke();
        if (BlockAtKey)
        {
            channel.ProcessReceive(Silence);
            Radiated?.Invoke(Silence);
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (Timeline)
        {
            _audio.AddRange(samples.ToArray());
            clock.Elapse(TimeSpan.FromTicks(samples.Length * TimeSpan.TicksPerSecond / SampleRate));
            for (int at = 0; at < samples.Length; at += Block)
            {
                ReadOnlySpan<float> chunk = samples.Slice(at, Math.Min(Block, samples.Length - at));
                channel.ProcessReceive(Silence.AsSpan(0, chunk.Length));
                Radiated?.Invoke(chunk);
            }
        }
    }

    public void Drain()
    {
    }

    public void Unkey()
    {
        OnUnkey?.Invoke();
        lock (_gate)
        {
            _keyups.Add(new Keyup(_startCount, count(), [.. _audio]));
        }

        _keyed = false;
    }
}

/// <summary>
/// One station on a real <see cref="SoundModemChannel"/> with a virtual clock, its transmitter, a <see cref="StationAir"/> for a sound card and everything the host raised, recorded.
/// The test is the capture loop: it feeds quiet blocks with <see cref="Pump"/> and waits for the keyup that is running (a keyup is the transmitter's thread feeding the keyed blocks, and two threads must not feed one channel).
/// </summary>
internal sealed class StationRig : IAsyncDisposable
{
    private static readonly float[] Quiet = new float[StationAir.Block];
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task? _transmitter;

    public StationRig(
        bool blockAtKey = false, FakeRadio? radio = null, bool audioFallback = false, int subChannel = 0, int seed = 1, int txDelayMilliseconds = 100, int txTailMilliseconds = 20,
        Action<bool?>? afterExternalBusy = null)
    {
        Clock = new VirtualAir.Clock();
        Channel = new SoundModemChannel(48000, Clock, randomSeed: 3, channelBusySource: radio, audioFallback: audioFallback);
        Channel.Csma.Persistence = 255;
        Channel.Csma.TxDelayMilliseconds = txDelayMilliseconds;
        Channel.Csma.TxTailMilliseconds = txTailMilliseconds;
        Host = new OfdmFmStationHost(Channel, new Random(seed), new OfdmFmStationHostOptions { RunJobsInline = true, SubChannel = subChannel, AfterExternalBusy = afterExternalBusy });
        Air = new StationAir(Channel, Clock, () => Host.SampleCount) { BlockAtKey = blockAtKey };
        Host.StationForTests.TransmitRequest += e =>
        {
            lock (_gate)
            {
                _requests.Add((e.Id, e.Samples.ToArray()));
            }
        };
        Host.StationForTests.WithdrawRequest += e =>
        {
            lock (_gate)
            {
                _withdrawn.Add(e.Id);
            }
        };
        Host.Transmitted += e =>
        {
            lock (_gate)
            {
                _transmitted.Add(e);
            }
        };
        Host.Received += e =>
        {
            lock (_gate)
            {
                _received.Add((e.Datagram.ToArray(), e.FromKey, e.Addressing));
            }
        };
        Host.Notice += line =>
        {
            lock (_gate)
            {
                _notices.Add(line);
            }
        };
    }

    private readonly List<(long Id, float[] Samples)> _requests = [];
    private readonly List<long> _withdrawn = [];
    private readonly List<TransmittedEvent> _transmitted = [];
    private readonly List<(byte[] Datagram, uint FromKey, Addressing Addressing)> _received = [];
    private readonly List<string> _notices = [];

    public VirtualAir.Clock Clock { get; }

    public SoundModemChannel Channel { get; }

    public OfdmFmStationHost Host { get; }

    public StationAir Air { get; }

    /// <summary>False: do not wait for the transmitter between blocks (it is not running, or the channel reads busy for another reason).</summary>
    public bool WaitForTransmitter { get; set; } = true;

    public (long Id, float[] Samples)[] Requests { get { lock (_gate) { return [.. _requests]; } } }

    public long[] Withdrawn { get { lock (_gate) { return [.. _withdrawn]; } } }

    public TransmittedEvent[] Transmitted { get { lock (_gate) { return [.. _transmitted]; } } }

    public (byte[] Datagram, uint FromKey, Addressing Addressing)[] Received { get { lock (_gate) { return [.. _received]; } } }

    public string[] Notices { get { lock (_gate) { return [.. _notices]; } } }

    /// <summary>Starts the channel's transmitter on a thread of its own.</summary>
    public void StartTransmitter() => _transmitter = Task.Run(() => Channel.RunTransmitterAsync(Air, Air, _stop.Token));

    /// <summary>Waits until no keyup is queued or running. A keyup is the transmitter's thread feeding this channel; the caller must not feed it meanwhile.</summary>
    public async Task IdleAsync()
    {
        if (WaitForTransmitter && (Channel.TransmitQueued || Channel.ChannelBusy))
        {
            await Eventually.UntilAsync(() => !Channel.TransmitQueued && !Channel.ChannelBusy, "the keyup to finish");
        }
    }

    /// <summary>Feeds <paramref name="blocks"/> quiet blocks, waiting out any keyup in between.</summary>
    public async Task FeedAsync(int blocks)
    {
        for (int i = 0; i < blocks; i++)
        {
            await IdleAsync();
            Channel.ProcessReceive(Quiet);
        }
    }

    /// <summary>Feeds quiet blocks to every rig in turn, waiting out keyups, until <paramref name="until"/> holds. The block count is a limit that turns "never" into a failure, ten minutes of audio; no outcome depends on it.</summary>
    public static async Task PumpAsync(Func<bool> until, string because, params StationRig[] rigs)
    {
        const int GiveUpBlocks = 10 * 60 * 50;
        for (int i = 0; i < GiveUpBlocks && !until(); i++)
        {
            foreach (StationRig rig in rigs)
            {
                await rig.IdleAsync();
            }

            foreach (StationRig rig in rigs)
            {
                rig.Channel.ProcessReceive(Quiet);
            }
        }

        if (!until())
        {
            throw new TimeoutException($"ten minutes of audio were fed and this never happened: {because}");
        }
    }

    public Task PumpAsync(Func<bool> until, string because) => PumpAsync(until, because, this);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        Host.Dispose();
        _stop.Dispose();
    }
}
