using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.OfdmFmTool;
using Packet.SoundModem.OfdmFmTool.Ax25;
using Packet.SoundModem.Tests.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.OfdmFmTool;

/// <summary>
/// Two stations on the two-station rig, each with the AX.25 stack on top, on a stack clock that only the bench moves.
/// </summary>
/// <remarks>
/// <para><b>The rule.</b> The stack clock and the audio move only when nothing is in hand anywhere: every rig is at rest (nothing queued, nothing keyed, no end-of-transmission signal owed, as <see cref="StationRig"/> defines it) and
/// both adapters are at rest (<see cref="OfdmFmAx25Transport.AtRest"/>: every frame the modem heard has been through the stack's pump and the pump is waiting, and every report of a transmission has become the completion the stack awaits).
/// Each signal is taken before the state it describes is looked at. Frames waiting in the modem for the air are not the stack's work: the pump feeds audio so that they go.</para>
/// <para><b>The threads, and why each hop ends where the bench can see it.</b></para>
/// <list type="table">
/// <item><term>The bench's thread</term><description>Feeds a block to each rig, then moves the stack clock. <c>FakeTimeProvider.Advance</c> runs the stack's timer callbacks inline, so T1, T2 and T3 expire here, and the SDL, which runs to completion on whatever thread posts to it, reacts here too, handing its frames to the adapter before <c>Advance</c> returns.</description></item>
/// <item><term>A transmitter thread per rig</term><description>Plays a burst, feeding the other rig's channel through the air. The rig counts it until the keyup ends and the host has its end signal.</description></item>
/// <item><term>The port's delivery thread</term><description>Takes a received frame from the host's queue to the adapter. The adapter's count of datagrams the host heard against frames the stack has finished with covers the whole path.</description></item>
/// <item><term>A pool thread, the stack's pump</term><description>Reads the adapter's queue and handles each frame inline: parse, SDL, the replies handed to the adapter, the application's handlers. It is back asking for the next frame only when that is all done, which is what the adapter counts.</description></item>
/// <item><term>A pool thread, the port's completions</term><description>The modem's report of a transmission completes the port's task there, and a continuation of the adapter completes the task the stack awaits, inline, so the stack's re-arm of T1 runs in it. The adapter counts reports against completions.</description></item>
/// </list>
/// <para>The stack's own dial polls in real time (<c>ConnectAsync</c>), but only to return the session and to raise an event nobody here uses; it changes nothing in the link, and the call is followed through the link's signals instead.</para>
/// </remarks>
internal sealed class Ax25Bench : IAsyncDisposable
{
    private const int BlockMilliseconds = 20;
    private readonly Lock _timeline = new();

    public Ax25Bench()
    {
        Clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Caller = new Side("G4ABC-1", 11, Clock);
        Answerer = new Side("M0XYZ-1", 12, Clock);
        Caller.Rig.Air.Timeline = _timeline;
        Answerer.Rig.Air.Timeline = _timeline;
        Caller.Rig.Air.Radiated = chunk => Hear(Caller, Answerer, chunk);
        Answerer.Rig.Air.Radiated = chunk => Hear(Answerer, Caller, chunk);
    }

    public FakeTimeProvider Clock { get; }

    public Side Caller { get; }

    public Side Answerer { get; }

    /// <summary>Asked for each burst a station puts on the air: the station and the number of its keyup (1 is its first). True: the other station hears silence for it.</summary>
    public Func<Side, int, bool>? Lose { get; set; }

    public async Task StartAsync()
    {
        await Caller.StartAsync();
        await Answerer.StartAsync();
    }

    /// <summary>Feeds audio and moves the stack clock until <paramref name="until"/> holds. <paramref name="until"/> is looked at only when everything is at rest.</summary>
    public async Task PumpAsync(Func<bool> until, string because)
    {
        try
        {
            await PumpCoreAsync(until, because);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{because}: caller {Caller.Link.Transport.Describe()} session rest {Caller.Session.AtRest}; answerer {Answerer.Link.Transport.Describe()} session rest {Answerer.Session.AtRest}");
        }
    }

    private Task PumpCoreAsync(Func<bool> until, string because) =>
        StationRig.PumpAsync(
            [.. Caller.Rests, .. Answerer.Rests],
            () =>
            {
                if (until())
                {
                    return true;
                }

                Clock.Advance(TimeSpan.FromMilliseconds(BlockMilliseconds));
                return false;
            },
            because,
            Caller.Rig,
            Answerer.Rig);

    public async ValueTask DisposeAsync()
    {
        await Caller.DisposeAsync();
        await Answerer.DisposeAsync();
    }

    private void Hear(Side from, Side to, ReadOnlySpan<float> chunk)
    {
        if (Lose?.Invoke(from, from.Rig.Air.KeyAttempts) == true)
        {
            to.Rig.Channel.ProcessReceive(new float[chunk.Length]);
        }
        else
        {
            to.Rig.Channel.ProcessReceive(chunk);
        }
    }

    internal sealed class Side : IAsyncDisposable
    {
        private readonly List<string> _lines = [];

        public Side(string call, int seed, TimeProvider clock)
        {
            Call = call;
            Rig = new StationRig(seed: seed, cold: true);
            Log = new StampedLog(clock, line => { lock (_lines) { _lines.Add(line); } });
            Session = new ToolSession(Rig.Host, call, null, false, true, Log, clock) { Ax25Mode = true };
            Session.Start().Should().BeNull();
            Link = new Ax25Link(Rig.Host, Rig.Channel, call, clock, Log);

            // Two sources, not one combined: each signal is awaited on its own, so none collects a continuation at every look.
            Rests = [new RestSource(() => Link.Transport.AtRest, Link.Transport.Changed), new RestSource(() => Session.AtRest, Session.Changed)];
        }

        public string Call { get; }

        public StationRig Rig { get; }

        public StampedLog Log { get; }

        public ToolSession Session { get; }

        public Ax25Link Link { get; }

        public RestSource[] Rests { get; }

        public async Task StartAsync()
        {
            await Link.StartAsync();
            Rig.StartTransmitter();
        }

        public async Task<string[]> LinesAsync()
        {
            await Log.DisposeAsync();
            lock (_lines)
            {
                return [.. _lines];
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync();
            await Session.DisposeAsync();
            await Log.DisposeAsync();
            await Rig.DisposeAsync();
        }
    }
}

/// <summary>Lines for an interactive call, given by the test when everything is at rest. A line given is taken up inline: the call sends it before <see cref="Say"/> returns.</summary>
internal sealed class ScriptedReader : TextReader
{
    private readonly Queue<string> _waiting = [];
    private TaskCompletionSource<string?>? _wanted;

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        lock (_waiting)
        {
            if (_waiting.TryDequeue(out string? line))
            {
                return new ValueTask<string?>(line);
            }

            _wanted = new TaskCompletionSource<string?>();
            return new ValueTask<string?>(_wanted.Task);
        }
    }

    public void Say(string line)
    {
        TaskCompletionSource<string?>? wanted;
        lock (_waiting)
        {
            wanted = _wanted;
            _wanted = null;
            if (wanted is null)
            {
                _waiting.Enqueue(line);
                return;
            }
        }

        wanted.TrySetResult(line);
    }
}
