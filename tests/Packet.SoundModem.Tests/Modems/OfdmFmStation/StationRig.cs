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

    /// <summary>How many radios are keyed on each timeline.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Lock, System.Runtime.CompilerServices.StrongBox<int>> KeyedOn = new();

    private readonly Lock _gate = new();
    private readonly List<Keyup> _keyups = [];
    private List<float> _audio = [];
    private long _startCount;
    private int _keys;
    private int _failKeys;
    private int _failAttempt;
    private volatile bool _keyed;
    private volatile bool _overlapped;

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

    /// <summary>
    /// True once this radio keyed while another on its timeline was keyed. The two transmitters' writes then take turns on <see cref="Timeline"/> in whatever order the threads reach it, so the audio
    /// each channel hears is the thread scheduler's choice and the run cannot be reproduced. <see cref="StationRig.PumpAsync(Func{bool}, string, StationRig[])"/> fails on it rather than carry on.
    /// </summary>
    public bool Overlapped => _overlapped;

    /// <summary>How many times the PTT was taken down, counting the ones that threw.</summary>
    public int KeyAttempts => Volatile.Read(ref _keys);

    /// <summary>Makes the next <paramref name="times"/> key-ups throw.</summary>
    public void FailNextKeys(int times) => Volatile.Write(ref _failKeys, times);

    /// <summary>Makes the key-up with this number (1 is the first the radio is asked for, counting the ones that threw) throw. Deterministic where the order of keyups is.</summary>
    public void FailKeyAttempt(int attempt) => Volatile.Write(ref _failAttempt, attempt);

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
        int attempt = Interlocked.Increment(ref _keys);
        if (attempt == Volatile.Read(ref _failAttempt))
        {
            throw new InvalidOperationException("the PTT did not key");
        }

        if (Volatile.Read(ref _failKeys) > 0)
        {
            Interlocked.Decrement(ref _failKeys);
            throw new InvalidOperationException("the PTT did not key");
        }

        _startCount = count();
        _audio = [];
        _keyed = true;
        if (Interlocked.Increment(ref KeyedOn.GetValue(Timeline, _ => new()).Value) > 1)
        {
            _overlapped = true;
        }

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

        Interlocked.Decrement(ref KeyedOn.GetValue(Timeline, _ => new()).Value);
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
    private TaskCompletionSource _keyupEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StationRig(
        bool blockAtKey = false, FakeRadio? radio = null, bool audioFallback = false, int subChannel = 0, int seed = 1, int txDelayMilliseconds = 100, int txTailMilliseconds = 20,
        Action<bool?>? afterExternalBusy = null, Func<IStationClockInput, IStationClockInput>? clockInput = null, OfdmFmStationHostOptions? morse = null,
        TaskScheduler? signalScheduler = null)
    {
        Clock = new VirtualAir.Clock();
        Channel = new SoundModemChannel(48000, Clock, randomSeed: 3, channelBusySource: radio, audioFallback: audioFallback);
        Channel.Csma.Persistence = 255;
        Channel.Csma.TxDelayMilliseconds = txDelayMilliseconds;
        Channel.Csma.TxTailMilliseconds = txTailMilliseconds;
        Host = new OfdmFmStationHost(Channel, new Random(seed), new OfdmFmStationHostOptions { RunJobsInline = true, SubChannel = subChannel, AfterExternalBusy = afterExternalBusy, ClockInputWrapper = clockInput, MorseToneHz = morse?.MorseToneHz ?? 1500, MorseWordsPerMinute = morse?.MorseWordsPerMinute ?? 20, MorseAmplitude = morse?.MorseAmplitude ?? 0.8, SignalScheduler = signalScheduler });
        Air = new StationAir(Channel, Clock, () => Host.SampleCount) { BlockAtKey = blockAtKey };
        Channel.KeyupEnded += KeyupEnded;
        Channel.TransmitRejected += (_, _, _) => KeyupEnded();
        Host.SignalPosted += KeyupEnded;
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
        Host.ModemSent += e =>
        {
            lock (_gate)
            {
                _modemSent.Add(e);
            }
        };
        Host.Identified += e =>
        {
            lock (_gate)
            {
                _identified.Add(e);
            }
        };
        Host.MorseIdentSent += text =>
        {
            lock (_gate)
            {
                _morseSent.Add(text);
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
    private readonly List<ModemSentEvent> _modemSent = [];
    private readonly List<IdentifiedEvent> _identified = [];
    private readonly List<string> _morseSent = [];

    /// <summary>What the Morse ident keys with the default options and the rig's key-up delay and tail: silence, the callsign at 1500 Hz, 20 words a minute, 0.8, then the tail.</summary>
    public static float[] ExpectedMorse(string callsign = "G4ABC", int delayMilliseconds = 100, int tailMilliseconds = 20)
    {
        float[] tone = Packet.SoundModem.Ident.MorseGenerator.Real(callsign, 1500, 0.8, 20, 48000);
        int lead = delayMilliseconds * 48000 / 1000;
        var audio = new float[lead + tone.Length + (tailMilliseconds * 48000 / 1000)];
        tone.CopyTo(audio, lead);
        return audio;
    }

    /// <summary>True if the keyup is the default Morse ident of <paramref name="callsign"/>.</summary>
    public static bool IsMorse(Keyup keyup, string callsign = "G4ABC") => keyup.Audio.AsSpan().SequenceEqual(ExpectedMorse(callsign));

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

    public ModemSentEvent[] ModemSent { get { lock (_gate) { return [.. _modemSent]; } } }

    public IdentifiedEvent[] Identified { get { lock (_gate) { return [.. _identified]; } } }

    public string[] MorseSent { get { lock (_gate) { return [.. _morseSent]; } } }

    public string[] Notices { get { lock (_gate) { return [.. _notices]; } } }

    /// <summary>Starts the channel's transmitter on a thread of its own.</summary>
    public void StartTransmitter() => _transmitter = Task.Run(() => Channel.RunTransmitterAsync(Air, Air, _stop.Token));

    /// <summary>
    /// Completes the signal the next <see cref="IdleAsync"/> look is waiting on: a keyup has ended, a queued frame has been turned away, or the host has posted the signal that one of its transmissions is over,
    /// which are the ways the queue, the keyed state and the host's owed signals change without the test doing it.
    /// </summary>
    private void KeyupEnded()
    {
        TaskCompletionSource ended;
        lock (_gate)
        {
            ended = _keyupEnded;
            _keyupEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        ended.TrySetResult();
    }

    /// <summary>
    /// Waits until the station is at rest: no keyup is queued or running, and the host has every signal its transmissions owe it. A keyup is the transmitter's thread feeding this channel; the caller
    /// must not feed it meanwhile. A finished transmission's signal is posted from a thread-pool continuation after the keyup has ended, and it decides what the host does at the next block (its Morse
    /// ident waits for it, and its identification burst for the Morse ident's), so a block fed before it lands puts that decision wherever the pool happened to be, which is not a reproducible run.
    /// Woken by the channel's own end-of-keyup signal (<see cref="SoundModemChannel.KeyupEnded"/>, raised after receive has reopened) and the host's <see cref="OfdmFmStationHost.SignalPosted"/>, never by looking again after a pause.
    /// </summary>
    public async Task IdleAsync()
    {
        while (WaitForTransmitter)
        {
            Task changed = Changed();
            if (AtRest)
            {
                return;
            }

            await Eventually.SignalAsync(changed, "the keyup to finish and the host to have its signals");
        }
    }

    /// <summary>The signal for the next change. Taken before the state is read, so a change in between is not lost: it completes this one.</summary>
    private Task Changed()
    {
        lock (_gate)
        {
            return _keyupEnded.Task;
        }
    }

    /// <summary>Nothing queued, nothing keyed, nothing owed by the host. Read after <see cref="Changed"/>.</summary>
    private bool AtRest => !WaitForTransmitter || (!Channel.TransmitQueued && !Channel.ChannelBusy && !Host.SignalsOwed);

    /// <summary>
    /// Waits until every rig is at rest at once. One pass waiting on each in turn is not enough: while it waits out one rig's keyup, that keyup radiates into the others, and one of those can queue a keyup
    /// of its own after it was looked at. So a rig found busy is waited for and the look starts again from the first, and the wait ends only on a pass that found every rig at rest.
    /// </summary>
    private static async Task AllAtRestAsync(StationRig[] rigs)
    {
        while (true)
        {
            Task[] changed = [.. rigs.Select(r => r.Changed())];
            int busy = Array.FindIndex(rigs, r => !r.AtRest);
            if (busy < 0)
            {
                return;
            }

            await Eventually.SignalAsync(changed[busy], "every rig's keyup to finish and its host to have its signals");
        }
    }

    /// <summary>Feeds <paramref name="blocks"/> quiet blocks, waiting out any keyup in between. For a rig on its own: where rigs radiate into each other use <see cref="PumpAsync(Func{bool}, string, StationRig[])"/>.</summary>
    public async Task FeedAsync(int blocks)
    {
        for (int i = 0; i < blocks; i++)
        {
            await IdleAsync();
            Channel.ProcessReceive(Quiet);
        }
    }

    /// <summary>Feeds quiet blocks, waiting out any keyup in between, until the station's own count reaches <paramref name="count"/>. It ends because every block moves the count; no outcome depends on how long it took.</summary>
    public async Task FeedToCountAsync(long count)
    {
        while (Host.SampleCount < count)
        {
            await IdleAsync();
            Channel.ProcessReceive(Quiet);
        }
    }

    /// <summary>
    /// Feeds quiet blocks to every rig in turn until <paramref name="until"/> holds. With more than one rig, after each block fed to any of them the pump waits until every rig is at rest at once: a block can make a station queue a keyup,
    /// whose thread then radiates into the other rigs' channels, and feeding one of those meanwhile would put two threads in one channel's receive path (<c>StationRigPumpTests</c> builds that case and the case of a host signal still to land).
    /// With one rig the keyup is waited out before each block instead, so a test can stop on a keyup being queued. The block count is a limit that turns "never" into a failure, ten minutes of audio; no outcome depends on it.
    /// </summary>
    public static async Task PumpAsync(Func<bool> until, string because, params StationRig[] rigs)
    {
        const int GiveUpBlocks = 10 * 60 * 50;
        for (int i = 0; i < GiveUpBlocks && !until(); i++)
        {
            if (rigs.Length == 1)
            {
                await rigs[0].IdleAsync();
                rigs[0].Channel.ProcessReceive(Quiet);
                continue;
            }

            foreach (StationRig rig in rigs)
            {
                rig.Channel.ProcessReceive(Quiet);
                await AllAtRestAsync(rigs);
                if (rigs.Any(r => r.Air.Overlapped))
                {
                    throw new InvalidOperationException(
                        "two radios on one timeline were keyed at once: the order of their writes is the thread scheduler's, so this run is not reproducible");
                }
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
