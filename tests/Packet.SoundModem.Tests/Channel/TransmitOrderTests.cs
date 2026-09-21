using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Frames reach the air in the order the host handed them over, whatever the channel was doing
/// when it took them.
/// </summary>
/// <remarks>
/// <para><b>Why this is not obvious from the queue.</b> The queue is FIFO and a single KISS read
/// loop calls into it in order, so the order looks structural. It was not, because a frame did not
/// reach the queue from the call that handed it over: while
/// <see cref="SoundModemChannel.TransmitInhibit"/> was set, each frame waited on a poll of its own
/// before being queued, and a frame handed over later could be queued first. That is ordering by
/// the scheduler, and for AX.25 it means I-frames on the air with N(S) out of sequence, which puts
/// the peer into REJ or SREJ recovery or drops the link. GB7RDG runs ARDOP on one sub-channel
/// beside AX.25 on two others, so the inhibit is live there.</para>
/// <para><b>Where the order is read.</b> In the modulate callback, which the transmitter calls on
/// its own thread immediately before it writes the burst, so the sequence recorded here is the
/// sequence on the air. Deliberately NOT <c>FrameTransmitted</c>: that is raised from a
/// continuation after the enqueue task completes, and two continuations are two thread-pool work
/// items whose order is the pool's business. Reading the event instead made this test fail on a
/// loaded box with the fix in place, which is the test being wrong rather than the code.</para>
/// <para><b>Deterministic, not lucky.</b> The hold is a function of the fake clock rather than a
/// flag the test flips, so every waiter sees the same answer for the same instant however late
/// the thread pool gets round to it, and the frames are handed over at virtual times chosen so
/// that one of them has to wait on a poll and the others do not.</para>
/// </remarks>
public class TransmitOrderTests : IAsyncLifetime
{
    private const int SampleRate = 12000;

    private readonly FakeTimeProvider _time = new();
    private readonly SoundModemChannel _channel;
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));
    private readonly List<char> _onAir = [];
    private IModem? _modem;
    private Task? _transmitter;

    public TransmitOrderTests() => _channel = new SoundModemChannel(SampleRate, _time, randomSeed: 5);

    public ValueTask InitializeAsync()
    {
        _channel.AddModem(0, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _modem = _channel.Modems[0];
        _channel.Csma.Persistence = 255;          // no roll: nothing here is about carrier access
        _channel.Csma.SlotTimeMilliseconds = 10;
        _channel.Csma.TxDelayMilliseconds = 20;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Starts the transmitter. Each test starts it for itself rather than the fixture doing it,
    /// because one of them has to get a frame into the queue before anything can take it out.
    /// </summary>
    private void StartTransmitter() =>
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    /// <summary>A UI frame carrying one letter, so a row of them can be told apart.</summary>
    private static byte[] Frame(char mark)
    {
        byte[] frame = new byte[20];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        frame[16] = (byte)mark;
        return frame;
    }

    /// <summary>
    /// Hands one frame to the channel the way a sub-channel's traffic goes, recording where it
    /// lands in the order as the transmitter renders it.
    /// </summary>
    private Task Send(char mark) =>
        _channel.EnqueueTransmit(
            txDelay =>
            {
                lock (_onAir)
                {
                    _onAir.Add(mark);
                }

                return _modem!.Modulate(Frame(mark), txDelay);
            },
            // The modem is the identity a sub-channel's frames queue under, the same one
            // SendAndAnnounceAsync uses, so this is one link's traffic in one queue.
            source: _modem);

    private string OnAir()
    {
        lock (_onAir)
        {
            return string.Concat(_onAir);
        }
    }

    /// <summary>
    /// Lets whatever the last clock move woke get as far as it is going to get.
    /// </summary>
    /// <remarks>
    /// A synchronisation device and not a measurement: no assertion in this file reads the wall
    /// clock, and every instant that decides anything is a virtual one. A waiter the pool is slow
    /// to run simply wakes at a later virtual time, which never moves it earlier in the order.
    /// </remarks>
    private static async Task SettleAsync()
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Yield();
            await Task.Delay(1, CancellationToken.None);
        }
    }

    /// <summary>Runs the fake clock on until something has happened, or gives up.</summary>
    private async Task RunUntilAsync(Func<bool> done, TimeSpan upTo)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + upTo;
        while (!done() && _time.GetUtcNow() < deadline)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1, CancellationToken.None);
        }
    }

    /// <summary>
    /// A frame handed over while another service held the channel goes out BEFORE frames handed
    /// over after the hold lifted, because that is the order the host wrote them.
    /// </summary>
    /// <remarks>
    /// The timings are chosen so the answer cannot depend on scheduling. The hold lifts at 105 ms.
    /// A is handed over at 0 ms and has to wait; B at 120 ms and C at 130 ms find the channel free
    /// and need no wait at all, so their path into the queue has no await in it and cannot be
    /// delayed relative to anything. Before this was fixed, A was not in any queue while it
    /// waited, so B and C went out in front of it and A followed: on a link, N(S) 6 and 7 on the
    /// air ahead of N(S) 5. Measured on the unfixed code, every run: "BCA".
    /// </remarks>
    [Fact]
    public async Task Frames_Held_By_Another_Service_Keep_The_Order_They_Were_Handed_Over_In()
    {
        DateTimeOffset lifts = _time.GetUtcNow() + TimeSpan.FromMilliseconds(105);

        // The hold is a function of the clock, not a flag this test flips, so a waiter that runs
        // late still gets the answer for the instant it runs at and the sequence cannot slide.
        _channel.TransmitInhibit = () => _time.GetUtcNow() < lifts;

        StartTransmitter();
        Task first = Send('A');
        await SettleAsync();
        OnAir().Should().BeEmpty("the channel is held");

        // Two of A's polls, both while the hold is still on.
        _time.Advance(TimeSpan.FromMilliseconds(50));
        await SettleAsync();
        _time.Advance(TimeSpan.FromMilliseconds(50));
        await SettleAsync();
        OnAir().Should().BeEmpty("the hold does not lift until 105 ms");

        // Past the lift. These two need no wait: the very first thing either of them asks is
        // whether the channel is held, and it is not.
        _time.Advance(TimeSpan.FromMilliseconds(20));
        await SettleAsync();
        Task second = Send('B');
        await SettleAsync();

        _time.Advance(TimeSpan.FromMilliseconds(10));
        await SettleAsync();
        Task third = Send('C');
        await SettleAsync();

        // And on far enough for A's own next poll, whenever it falls.
        await RunUntilAsync(() => OnAir().Length == 3, TimeSpan.FromSeconds(10));
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(30));

        OnAir().Should().Be("ABC",
            "the host handed them over in that order, and a link's sequence numbers are on the "
            + "air in the order the frames are, not in the order the channel got round to them");
    }

    /// <summary>
    /// A frame that was already in the queue when the hold started waits for it, rather than
    /// going out in the middle of somebody else's session.
    /// </summary>
    /// <remarks>
    /// The other half of moving the hold to the transmitter, and a leak in its own right before
    /// that. The hold used to be asked once, at the enqueue, so it only ever held frames handed
    /// over WHILE it was on: anything already queued when an ARQ session started went out during
    /// the session, which is the one thing TransmitInhibit exists to prevent. Asked by the
    /// transmitter, it holds whatever is in the queue, which is what a host setting it means by
    /// it.
    /// </remarks>
    [Fact]
    public async Task A_Frame_Already_Queued_When_The_Hold_Starts_Waits_For_It()
    {
        var held = false;

        // Non-null from the start, as a station that runs ARDOP beside packet has it: the hook is
        // wired once at start-up and answers for the session, not for the wiring.
        _channel.TransmitInhibit = () => held;

        Task queued = Send('A');
        held = true;
        StartTransmitter();

        await SettleAsync();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        await SettleAsync();
        OnAir().Should().BeEmpty(
            "it was queued before the session started, which does not make it welcome during one");
        queued.IsCompleted.Should().BeFalse();

        held = false;
        await RunUntilAsync(() => OnAir().Length == 1, TimeSpan.FromSeconds(10));
        await queued.WaitAsync(TimeSpan.FromSeconds(30));
        OnAir().Should().Be("A");
    }

    /// <summary>
    /// A whole backlog handed over during a hold comes out oldest first when the hold lifts.
    /// </summary>
    /// <remarks>
    /// Five frames 30 ms apart against a 50 ms poll, so no two of them ever shared a poll instant
    /// and ordering by poll shows up as a shuffle rather than as a near miss. On the unfixed code,
    /// every run: "BDCAE".
    /// </remarks>
    [Fact]
    public async Task A_Backlog_Held_By_Another_Service_Comes_Out_Oldest_First()
    {
        DateTimeOffset lifts = _time.GetUtcNow() + TimeSpan.FromMilliseconds(400);
        _channel.TransmitInhibit = () => _time.GetUtcNow() < lifts;

        StartTransmitter();
        var queued = new List<Task>();
        foreach (char mark in "ABCDE")
        {
            queued.Add(Send(mark));
            await SettleAsync();
            _time.Advance(TimeSpan.FromMilliseconds(30));
            await SettleAsync();
        }

        OnAir().Should().BeEmpty("nothing may reach the air while another service holds the channel");

        await RunUntilAsync(() => OnAir().Length == 5, TimeSpan.FromSeconds(10));
        await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(30));

        OnAir().Should().Be("ABCDE");
    }
}
