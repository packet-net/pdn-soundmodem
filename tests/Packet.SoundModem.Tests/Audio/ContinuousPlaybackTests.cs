using AwesomeAssertions;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// A playback stream that is kept running on silence between transmissions (issue #828),
/// against a card that plays on a clock the test owns.
/// </summary>
/// <remarks>
/// <para>The reason is a measurement: a CM108B's playback clock, restarted after a pause, ran
/// more than 300 ppm slow through the first burst in 112 rounds of 120, against 5 of 120 with the
/// stream left running (2026-11-23). The stream used to be drained and prepared again after every
/// transmission, so every transmission began on a restarted one.</para>
/// <para>What must hold while it is kept running: silence goes in only when nothing is being
/// transmitted, and never between two writes of one transmission; the drain still returns when
/// the last transmitted frame has played, since releasing PTT waits on it, and no later; and an
/// underrun in the silence is not reported as audio the transmitter did not send.</para>
/// <para>And a transmission must not wait behind the silence. Its first write takes back what
/// the card has not yet taken, less a guard (on the bench CM108B that leaves 13 to 19 ms ahead
/// of 60), the count of frames written goes back with it so the drain is still exact, a card
/// that will not rewind is simply written behind the silence, and a transmitter that was
/// promised a lead finds no less than that ahead of its audio.</para>
/// <para>The guard is kept clear of what the card's driver has taken, not of the play point.
/// The first version rewound to the driver's position whenever that was the nearer limit, which
/// on a USB card is always: the driver came for its next piece, found nothing, and about one
/// burst in ten on the rig began with an underrun. The guard is 7 ms because that piece is
/// 6 ms.</para>
/// </remarks>
public class ContinuousPlaybackTests
{
    private const int Rate = 48000;
    private const int BufferFrames = 5760;
    private const int QueueFrames = Rate * ContinuousPlayback.QueueMilliseconds / 1000;
    private const int GuardFrames = Rate * ContinuousPlayback.GuardMilliseconds / 1000;
    private const int Feed = ContinuousPlayback.FeedMilliseconds;

    /// <summary>What the card's driver has already taken and will not give back: 6 to 12 ms
    /// on the CM108B, 12 here.</summary>
    private const int Taken = 576;

    /// <summary>What is left ahead of a transmission on such a card: that, and the guard.</summary>
    private const int Ahead = Taken + GuardFrames;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_Stream_Is_Started_On_Silence_When_It_Is_Opened()
    {
        var card = new ClockedCard();

        _ = Open(card);

        card.Running.Should().BeTrue("the first transmission must not be the one that starts the clock");
        card.Written.Should().Equal(('s', QueueFrames));
    }

    [Fact]
    public void Idle_The_Queue_Is_Topped_Up_To_Sixty_Milliseconds_And_No_Deeper()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        for (int look = 0; look < 200; look++)
        {
            card.Advance(Feed);
            playback.Feed();
            card.Queued.Should().Be(QueueFrames);
        }

        card.Running.Should().BeTrue();
        card.Calls.Should().NotContain("recover", "a stream that is fed does not underrun");
        playback.IdleUnderruns.Should().Be(0);
    }

    [Fact]
    public void Nothing_Is_Fed_Between_The_Writes_Of_One_Transmission()
    {
        // The transmitter renders the next frame between two writes. Silence let in there would
        // be a hole in a burst the far end expects to be continuous.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        playback.Write(480, out _).Should().Be(480);
        playback.Feed();
        card.Advance(10);
        playback.Feed();
        playback.Write(480, out _).Should().Be(480);
        playback.Drain();

        card.Written.Take(4).Should().Equal(
            ('s', QueueFrames), ('r', QueueFrames - Ahead), ('a', 480), ('a', 480));
        card.Xruns.Should().Be(0);
    }

    [Fact]
    public void A_Drain_Returns_When_The_Last_Transmitted_Frame_Has_Played_And_Leaves_The_Stream_Running()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        // 146 ms and a bit: longer than the room in the buffer, so the write blocks as a real one
        // does, and not a whole number of periods or of milliseconds.
        playback.Write(7000, out int failure).Should().Be(7000);
        failure.Should().Be(0);
        card.AudioQueued.Should().BeGreaterThan(0, "the write returns with most of the burst still to play");

        playback.Drain();

        card.AudioQueued.Should().Be(0, "PTT is released on the strength of this");
        card.SilencePlayedSinceAudio.Should().BeLessThan(
            Rate / 1000, "and it is not held for longer than the audio took");
        card.Running.Should().BeTrue("the stream is not stopped at the end of a transmission");
        card.Queued.Should().BeGreaterThan(0, "silence is already behind the transmission when it ends");
        card.Calls.Should().NotContain("recover").And.NotContain("prepare");
        card.Xruns.Should().Be(0);
        playback.IdleUnderruns.Should().Be(0);
    }

    [Fact]
    public void A_Transmission_Takes_Back_The_Queued_Silence_The_Card_Has_Not_Yet_Taken()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);
        card.Advance(Feed);
        playback.Feed();

        playback.Write(4800, out _);

        card.SilenceAheadOfAudio.Should().Be(
            Ahead, "the guard is left between the frames about to be written and what the driver has taken");
        playback.LeadFrames.Should().Be(Ahead, "and the transmitter can be told what was left");
        card.Order.SkipWhile(call => call != "rewind").Take(2).Should().Equal(
            ["rewind", "transfer"], "nothing is asked of the card between the rewind and the first frames");

        // The frames taken back were never played, so they come off the count the drain works
        // from. Left on it, the drain would come back that much early, with PTT.
        playback.Drain();
        card.AudioQueued.Should().Be(0);
        card.SilencePlayedSinceAudio.Should().BeLessThan(Rate / 1000);
        card.Pauses.Sum().Should().Be((Ahead + 4800) * 1000 / Rate);
        card.Xruns.Should().Be(0);
        playback.IdleUnderruns.Should().Be(0);
    }

    [Fact]
    public void A_Guard_Is_Left_Ahead_Of_A_Transmission_Where_The_Card_Would_Give_Everything_Back()
    {
        // Not a USB card, say: nothing in flight, and the play point itself on offer.
        var card = new ClockedCard { Unrewindable = 0 };
        ContinuousPlayback playback = Open(card);

        playback.Write(4800, out _);

        card.SilenceAheadOfAudio.Should().Be(GuardFrames);
    }

    [Fact]
    public void A_Card_With_Less_Than_The_Guard_To_Give_Back_Is_Not_Rewound()
    {
        var card = new ClockedCard { Unrewindable = QueueFrames - (GuardFrames / 2) };
        ContinuousPlayback playback = Open(card);

        playback.Write(2400, out _);

        card.Written.Should().Equal(('s', QueueFrames), ('a', 2400));
        card.SilenceAheadOfAudio.Should().Be(QueueFrames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-38)] // ENOSYS
    [InlineData(-22)] // EINVAL
    public void A_Card_That_Will_Not_Rewind_Is_Written_Behind_The_Silence_As_Before(int rewindable)
    {
        // A plug layer that resamples offers nothing. That is not a fault: the transmission
        // waits behind the queue, and everything else is as it was.
        var card = new ClockedCard { RewindableAnswer = rewindable };
        ContinuousPlayback playback = Open(card);

        playback.PromiseLead().Should().Be(QueueFrames, "all of it stays ahead, and can be counted on");
        playback.Write(2400, out int failure).Should().Be(2400);
        failure.Should().Be(0);

        card.Written.Should().Equal(('s', QueueFrames), ('a', 2400));
        card.SilenceAheadOfAudio.Should().Be(QueueFrames);
        playback.LeadFrames.Should().Be(QueueFrames);
        playback.Drain();
        card.AudioQueued.Should().Be(0);
        card.SilencePlayedSinceAudio.Should().BeLessThan(Rate / 1000);
        card.Xruns.Should().Be(0);
        playback.IdleUnderruns.Should().Be(0);
    }

    [Fact]
    public void A_Rewind_That_Takes_Back_Less_Than_It_Was_Asked_For_Is_Counted_As_What_It_Took()
    {
        var card = new ClockedCard { RewindShortfall = 300 };
        ContinuousPlayback playback = Open(card);

        playback.Write(2400, out _);

        card.SilenceAheadOfAudio.Should().Be(Ahead + 300);
        playback.LeadFrames.Should().Be(Ahead + 300);
        playback.Drain();
        card.AudioQueued.Should().Be(0, "the drain waited for the silence that was not taken back too");
        card.SilencePlayedSinceAudio.Should().BeLessThan(Rate / 1000, "and for no more than that");
    }

    [Fact]
    public void A_Promised_Lead_Is_What_Is_Ahead_When_The_Transmission_Starts()
    {
        // Asked before the audio is rendered, which takes time the feeder has to cover: nothing
        // is rewound until the first frame is there to be written.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        long promised = playback.PromiseLead();
        card.Written.Should().Equal([('s', QueueFrames)], "a promise takes nothing back");
        card.Advance(Feed);
        playback.Feed();
        card.Advance(Feed);
        playback.Feed();
        playback.Write(4800, out _);

        promised.Should().Be(Ahead);
        card.SilenceAheadOfAudio.Should().Be(promised);
    }

    [Fact]
    public void A_Promised_Lead_Is_Kept_When_The_Card_Would_Since_Give_More_Back()
    {
        // The transmitter has already left that much silence out of its audio.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        long promised = playback.PromiseLead();
        card.Unrewindable = 0;
        playback.Write(4800, out _);

        card.SilenceAheadOfAudio.Should().Be(promised, "not the guard alone, which is less");
    }

    [Fact]
    public void A_Promised_Lead_Is_Exceeded_Rather_Than_Broken_When_The_Card_Gives_Less_Back()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        long promised = playback.PromiseLead();
        card.Unrewindable = 864;
        playback.Write(2400, out _);

        card.SilenceAheadOfAudio.Should().Be(864 + GuardFrames);
        card.SilenceAheadOfAudio.Should().BeGreaterThan(promised);
    }

    [Fact]
    public void A_Promised_Lead_Is_Made_Good_With_Silence_After_The_Queue_Ran_Dry()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        long promised = playback.PromiseLead();
        card.Advance(200);
        card.Running.Should().BeFalse("the card ran dry before the first frame");
        playback.Write(480, out int failure).Should().Be(480);

        failure.Should().Be(0);
        card.SilenceAheadOfAudio.Should().Be(promised);
        card.Xruns.Should().Be(0, "nothing that was transmitted was lost");
        playback.IdleUnderruns.Should().Be(1);
        playback.Drain();
        card.AudioQueued.Should().Be(0);
    }

    [Fact]
    public void No_Lead_Is_Promised_Inside_A_Transmission_And_Nothing_Is_Taken_Back_There()
    {
        // The next frames go straight behind the last, and what is queued is the transmission.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);
        playback.Write(480, out _);

        playback.PromiseLead().Should().Be(0);
        playback.Write(480, out _);

        card.Written.Count(run => run.Kind == 'r').Should().Be(1, "only the first write rewinds");
        card.AudioQueued.Should().Be(960);
    }

    [Fact]
    public void An_Underrun_Between_The_Rewind_And_The_First_Frames_Is_Not_Lost_Audio()
    {
        // The rewind leaves little ahead, and the card can get through it before the frames
        // land. Nothing of the transmission had been written, so nothing of it is missing.
        var card = new ClockedCard { RunsDryBeforeNextTransfer = true };
        ContinuousPlayback playback = Open(card);

        playback.Write(480, out int failure).Should().Be(480);

        failure.Should().Be(0);
        card.Xruns.Should().Be(0);
        playback.IdleUnderruns.Should().Be(1);
        playback.Drain();
        card.AudioQueued.Should().Be(0);
    }

    [Fact]
    public void A_Drain_With_Nothing_Transmitted_Returns_At_Once()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        playback.Drain();

        playback.Write(480, out _);
        playback.Drain();
        int waited = card.Pauses.Count;
        playback.Drain();

        card.Pauses.Should().HaveCount(waited, "the second drain has nothing left to wait for");
        card.Running.Should().BeTrue();
    }

    [Fact]
    public void Silence_Is_Fed_Again_As_Soon_As_A_Transmission_Has_Been_Drained()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);
        playback.Write(480, out _);
        playback.Drain();

        for (int look = 0; look < 20; look++)
        {
            card.Advance(Feed);
            playback.Feed();
        }

        card.Running.Should().BeTrue();
        card.Queued.Should().Be(QueueFrames);
        card.Calls.Should().NotContain("recover");
    }

    [Fact]
    public void An_Underrun_In_The_Silence_Is_Recovered_And_Is_Not_Counted_As_Lost_Audio()
    {
        // The feeder was not scheduled for longer than the queue lasts. Nothing was being
        // transmitted, so nothing the transmitter sent is missing.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);
        card.Advance(200);
        card.Running.Should().BeFalse("the card ran dry");

        playback.Feed();

        card.Running.Should().BeTrue();
        card.Queued.Should().Be(QueueFrames);
        card.Xruns.Should().Be(0, "Xruns means audio the transmitter did not send");
        playback.IdleUnderruns.Should().Be(1);
    }

    [Fact]
    public void An_Underrun_Met_By_A_Transmissions_First_Frames_Is_Not_Lost_Audio_Either()
    {
        // Run dry in the silence, and found by the transmitter before the feeder found it. The
        // stream is then prepared and holds less than the start threshold, which is the buffer:
        // the drain has to start it, as snd_pcm_drain used to.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);
        card.Advance(200);

        playback.Write(480, out int failure).Should().Be(480);
        failure.Should().Be(0);
        card.Running.Should().BeFalse("480 frames do not reach the start threshold");
        playback.Drain();

        card.AudioQueued.Should().Be(0, "the drain started the stream and waited for the audio");
        card.Running.Should().BeTrue();
        card.Xruns.Should().Be(0);
        playback.IdleUnderruns.Should().Be(1);
    }

    [Fact]
    public void An_Underrun_Inside_A_Transmission_Is_Counted_As_It_Always_Was()
    {
        // A transmitter that takes too long between two writes. The feeder leaves the stream
        // alone, the card runs dry, and that is a hole in what was transmitted.
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);

        playback.Write(480, out _);
        card.Advance(100);
        playback.Feed();
        card.Advance(100);
        playback.Feed();
        card.Running.Should().BeFalse("nothing was fed in the gap, so the card ran dry");
        playback.Write(480, out int failure).Should().Be(480);
        playback.Drain();

        failure.Should().Be(0);
        card.Xruns.Should().Be(1);
        playback.IdleUnderruns.Should().Be(0);
        card.Written.Take(4).Should().Equal(
            [('s', QueueFrames), ('r', QueueFrames - Ahead), ('a', 480), ('a', 480)],
            "no silence went in until the drain");
        card.AudioQueued.Should().Be(0);
        card.Running.Should().BeTrue();
    }

    [Fact]
    public void A_Card_That_Stops_Playing_Does_Not_Hold_The_Drain_For_Ever()
    {
        var card = new ClockedCard();
        ContinuousPlayback playback = Open(card);
        playback.Write(480, out _);
        int owed = (Ahead + 480) * 1000 / Rate;
        card.Stalled = true;

        playback.Drain();

        card.Pauses.Sum().Should().BeGreaterThanOrEqualTo(owed, "the audio is given the time it needs");
        card.Pauses.Sum().Should().BeLessThanOrEqualTo(owed + ContinuousPlayback.DrainSlackMilliseconds);
    }

    [Fact]
    public void A_Top_Up_Never_Asks_For_More_Than_The_Buffer_Has_Room_For()
    {
        // The feeder writes with the gate held, so a write that blocked would hold the
        // transmitter off the card. A buffer no bigger than the queue is the tightest case.
        var card = new ClockedCard(bufferFrames: QueueFrames);
        ContinuousPlayback playback = Open(card, bufferFrames: QueueFrames);

        playback.QueueFrames.Should().Be(QueueFrames / 2, "half the buffer at most");
        for (int look = 0; look < 50; look++)
        {
            card.Advance(Feed);
            playback.Feed();
        }

        playback.Write(4800, out _).Should().Be(4800);
        playback.Drain();
        card.AudioQueued.Should().Be(0);
    }

    [Fact]
    public void The_Feeder_Thread_Feeds_While_Idle_And_Not_At_All_During_A_Transmission()
    {
        var card = new SinkCard();
        var playback = new ContinuousPlayback(card, Rate, BufferFrames);
        try
        {
            WaitFor(() => card.SilenceWrites >= 3).Should().BeTrue("the feeder runs on its own");

            // A write that blocks in the card, as a long burst does.
            card.Hold.Reset();
            var transmitter = new Thread(() => playback.Write(480, out _)) { IsBackground = true };
            transmitter.Start();
            card.InTransfer.Wait(Patience, TestContext.Current.CancellationToken).Should().BeTrue();
            int before = card.SilenceWrites;
            Thread.Sleep(10 * Feed);
            card.SilenceWrites.Should().Be(before, "nothing is fed while the transmitter is writing");

            // And then a transmitter that is rendering its next frame.
            card.Hold.Set();
            transmitter.Join(Patience).Should().BeTrue();
            Thread.Sleep(10 * Feed);
            card.SilenceWrites.Should().Be(before, "nor between two writes of one transmission");

            playback.Drain();
            WaitFor(() => card.SilenceWrites >= before + 3).Should().BeTrue("the drain lets it back in");
            card.Overlaps.Should().Be(0, "the handle is never used by two threads at once");
        }
        finally
        {
            playback.Stop();
        }
    }

    [Fact]
    public void Stopping_Ends_The_Feeder()
    {
        var card = new SinkCard();
        var playback = new ContinuousPlayback(card, Rate, BufferFrames);
        WaitFor(() => card.SilenceWrites >= 3).Should().BeTrue();

        playback.Stop().Should().BeTrue("the feeder is never far from the gate");

        int after = card.SilenceWrites;
        Thread.Sleep(10 * Feed);
        card.SilenceWrites.Should().Be(after, "the handle is about to be closed");
        playback.Feed();
        playback.Drain();
        card.SilenceWrites.Should().Be(after);
    }

    [Fact]
    public void Stopping_Ends_A_Feeder_That_Is_Waiting_Out_A_Transmission()
    {
        // Disposed mid-keyup: the feeder is parked until a drain that is never going to come.
        var card = new SinkCard();
        var playback = new ContinuousPlayback(card, Rate, BufferFrames);
        playback.Write(480, out _);

        playback.Stop().Should().BeTrue();
    }

    private static ContinuousPlayback Open(ClockedCard card, int bufferFrames = BufferFrames) =>
        new(card, Rate, bufferFrames, startFeeder: false);

    private static bool WaitFor(Func<bool> condition) => SpinWait.SpinUntil(condition, Patience);

    /// <summary>
    /// A card with a buffer and a clock. It plays only when the test moves the clock on, or when
    /// the code under test waits or blocks in a write, and it underruns when it runs dry.
    /// </summary>
    private sealed class ClockedCard(int bufferFrames = BufferFrames) : IPlaybackPcm
    {
        private const int Epipe = -32;
        private const int Ebadfd = -77;

        private readonly Queue<(char Kind, long Frames)> _queue = new();
        private State _state = State.Prepared;

        private enum State
        {
            Prepared,
            Running,
            Xrun,
        }

        internal List<string> Calls { get; } = [];

        internal List<int> Pauses { get; } = [];

        /// <summary>Everything written, in order: runs of silence ('s') and of audio ('a'), one
        /// entry per call, and what was taken back again ('r').</summary>
        internal List<(char Kind, long Frames)> Written { get; } = [];

        /// <summary>Queued frames the card's driver has already taken and will not give back.</summary>
        internal int Unrewindable { get; set; } = Taken;

        /// <summary>The calls that touch what is queued, in order.</summary>
        internal List<string> Order { get; } = [];

        /// <summary>Set to answer snd_pcm_rewindable with this whatever is queued: 0 or an errno
        /// from a layer that cannot rewind.</summary>
        internal int? RewindableAnswer { get; init; }

        /// <summary>A rewind takes back this many frames fewer than it was asked for.</summary>
        internal int RewindShortfall { get; init; }

        /// <summary>The card gets through everything queued just before the next frames land.</summary>
        internal bool RunsDryBeforeNextTransfer { get; set; }

        internal int Xruns { get; private set; }

        /// <summary>A card whose clock has stopped: queued audio stays queued.</summary>
        internal bool Stalled { get; set; }

        internal bool Running => _state == State.Running;

        internal long Queued => _queue.Sum(run => run.Frames);

        internal long AudioQueued => _queue.Where(run => run.Kind == 'a').Sum(run => run.Frames);

        /// <summary>Silence queued in front of the first audio frame.</summary>
        internal long SilenceAheadOfAudio => _queue.TakeWhile(run => run.Kind == 's').Sum(run => run.Frames);

        /// <summary>Silence played since the last audio frame was: how long a drain overstayed.</summary>
        internal long SilencePlayedSinceAudio { get; private set; }

        public bool IsCapture => false;

        /// <summary>Moves the card's clock on without the code under test knowing.</summary>
        internal void Advance(int milliseconds) => Play((long)milliseconds * Rate / 1000);

        public long Delay()
        {
            Order.Add("delay");
            return _state == State.Xrun ? Epipe : Queued;
        }

        public long WriteSilence(int frames)
        {
            if (_state == State.Xrun)
            {
                return Epipe;
            }

            if (frames > bufferFrames - Queued)
            {
                throw new InvalidOperationException("a top-up that does not fit would block");
            }

            Written.Add(('s', frames));
            Order.Add("silence");
            _queue.Enqueue(('s', frames));
            return frames;
        }

        public long Rewindable()
        {
            Order.Add("rewindable");
            return CanGiveBack;
        }

        private long CanGiveBack =>
            RewindableAnswer ?? (_state == State.Xrun ? Epipe : Math.Max(0, Queued - Unrewindable));

        public long Rewind(long frames)
        {
            long taken = Math.Min(frames, CanGiveBack) - RewindShortfall;
            if (taken <= 0)
            {
                return 0;
            }

            // Off the end of what was written, newest first.
            var kept = new Stack<(char Kind, long Frames)>(_queue);
            _queue.Clear();
            long left = taken;
            while (left > 0)
            {
                (char kind, long held) = kept.Pop();
                if (kind != 's')
                {
                    throw new InvalidOperationException("transmitted audio was taken back");
                }

                long gone = Math.Min(held, left);
                left -= gone;
                if (gone < held)
                {
                    kept.Push((kind, held - gone));
                }
            }

            foreach ((char, long) run in kept.Reverse())
            {
                _queue.Enqueue(run);
            }

            Written.Add(('r', taken));
            Order.Add("rewind");
            return taken;
        }

        public long Transfer(int frameOffset, int frames)
        {
            Order.Add("transfer");
            if (RunsDryBeforeNextTransfer)
            {
                RunsDryBeforeNextTransfer = false;
                Play(Queued);
            }

            if (_state == State.Xrun)
            {
                return Epipe;
            }

            Written.Add(('a', frames));
            long left = frames;
            while (left > 0)
            {
                long room = bufferFrames - Queued;
                if (room == 0)
                {
                    // A full buffer is the start threshold, and a blocked write is the card
                    // playing.
                    _state = State.Running;
                    Play(Rate / 1000);
                    continue;
                }

                long taken = Math.Min(room, left);
                _queue.Enqueue(('a', taken));
                left -= taken;
            }

            if (_state == State.Prepared && Queued >= bufferFrames)
            {
                _state = State.Running;
            }

            return frames;
        }

        public int Recover(int error)
        {
            Calls.Add("recover");
            _queue.Clear();
            _state = State.Prepared;
            return 0;
        }

        public int Prepare()
        {
            Calls.Add("prepare");
            _queue.Clear();
            _state = State.Prepared;
            return 0;
        }

        public int Start()
        {
            if (_state != State.Prepared || Queued == 0)
            {
                return Ebadfd;
            }

            Calls.Add("start");
            _state = State.Running;
            return 0;
        }

        public void CountXrun() => Xruns++;

        public void Pause(int milliseconds)
        {
            Pauses.Add(milliseconds);
            Advance(milliseconds);
        }

        private void Play(long frames)
        {
            if (_state != State.Running || Stalled)
            {
                return;
            }

            while (frames > 0 && _queue.Count > 0)
            {
                (char kind, long held) = _queue.Dequeue();
                long played = Math.Min(held, frames);
                frames -= played;
                SilencePlayedSinceAudio = kind == 'a' ? 0 : SilencePlayedSinceAudio + played;
                if (played < held)
                {
                    Requeue((kind, held - played));
                }
            }

            if (_queue.Count == 0)
            {
                _state = State.Xrun;
            }
        }

        private void Requeue((char Kind, long Frames) head)
        {
            (char, long)[] rest = [.. _queue];
            _queue.Clear();
            _queue.Enqueue(head);
            foreach ((char, long) run in rest)
            {
                _queue.Enqueue(run);
            }
        }
    }

    /// <summary>
    /// A card that takes whatever it is given at once, for the tests that run the real feeder
    /// thread: it counts what the feeder wrote, can hold a transmitter's write open, and notices
    /// two threads inside it at the same time.
    /// </summary>
    private sealed class SinkCard : IPlaybackPcm
    {
        private int _busy;
        private int _overlaps;
        private int _silenceWrites;

        internal ManualResetEventSlim Hold { get; } = new(initialState: true);

        internal ManualResetEventSlim InTransfer { get; } = new(initialState: false);

        internal int SilenceWrites => Volatile.Read(ref _silenceWrites);

        internal int Overlaps => Volatile.Read(ref _overlaps);

        public bool IsCapture => false;

        public long Delay()
        {
            Enter();
            Leave();
            return 0;
        }

        public long WriteSilence(int frames)
        {
            Enter();
            Interlocked.Increment(ref _silenceWrites);
            Leave();
            return frames;
        }

        public long Transfer(int frameOffset, int frames)
        {
            Enter();
            InTransfer.Set();
            Hold.WaitHandle.WaitOne(Patience);
            Leave();
            return frames;
        }

        public int Recover(int error) => 0;

        public int Prepare() => 0;

        public int Start()
        {
            Enter();
            Leave();
            return 0;
        }

        public long Rewindable() => 0;

        public long Rewind(long frames) => 0;

        public void CountXrun()
        {
        }

        public void Pause(int milliseconds) => Thread.Sleep(milliseconds);

        private void Enter()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
            {
                Interlocked.Increment(ref _overlaps);
            }
        }

        private void Leave() => Volatile.Write(ref _busy, 0);
    }
}
