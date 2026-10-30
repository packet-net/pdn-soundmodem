using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Telemetry;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Counting the times transmit audio reaches a self-pacing output late (issue #748).
/// </summary>
/// <remarks>
/// A Flex's DAX sink sends each packet when it falls due and sleeps until the next one, so the
/// radio holds almost nothing ahead. Three MS110D WN13 bursts from GB7RDG on 2026-10-30 each had a
/// 25 to 38 ms hole of band noise in Sweden, with the rest of the burst shifted late by the same
/// amount, and nothing on the station said so. These tests run the watch over a sink that behaves
/// as the DAX one does, on a fake clock: it sleeps to each packet's due time, and after a stall it
/// sends without sleeping until it has caught up.
/// </remarks>
public class TransmitStallWatchTests
{
    private const int Rate = 24000;
    private const int Packet = 128;

    [Fact]
    public void A_Sender_That_Keeps_Up_Reports_Nothing()
    {
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time);
        var watch = new TransmitStallWatch(sink, Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        watch.Write(new float[Rate * 7]);
        watch.Drain();

        reports.Should().BeEmpty();
        watch.Snapshot.Should().Be(new TransmitStallSnapshot(0, 0));
    }

    [Fact]
    public void A_Held_Up_Sender_Is_One_Stall_The_Size_Of_The_Hold()
    {
        // The shape of the 06:42 burst: 7 s of audio with the sender held up for 38 ms at 1.2 s.
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time) { HoldAtPacket = 1.2 * Rate / Packet, Hold = TimeSpan.FromMilliseconds(38) };
        var watch = new TransmitStallWatch(sink, Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;
        DateTimeOffset keyedAt = time.GetUtcNow();

        watch.Write(new float[Rate * 7]);
        watch.Drain();

        // Counted once although the sink takes several packets to catch up, each still behind.
        TransmitStallReport report = reports.Should().ContainSingle().Subject;
        report.Stalls.Should().Be(1);
        report.Worst.Should().Be(TimeSpan.FromMilliseconds(38));
        report.KeyedAt.Should().Be(keyedAt);
        // The sender catches up, so the keyup ends on time here; it is the radio that runs late.
        report.Length.Should().BeCloseTo(TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(10));
        watch.Snapshot.Stalls.Should().Be(1);
        watch.Snapshot.Seconds.Should().BeApproximately(0.038, 1e-9);
    }

    [Fact]
    public void A_Hold_Shorter_Than_The_Threshold_Is_Not_A_Stall()
    {
        // A wake-up a few milliseconds late is the pacing's ordinary jitter, not a hole on the air.
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time) { HoldAtPacket = 100, Hold = TimeSpan.FromMilliseconds(8) };
        var watch = new TransmitStallWatch(sink, Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        watch.Write(new float[Rate * 2]);
        watch.Drain();

        reports.Should().BeEmpty();
    }

    [Fact]
    public void Two_Separate_Holds_In_One_Keyup_Are_Two_Stalls_In_One_Report()
    {
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time)
        {
            HoldAtPacket = 50,
            Hold = TimeSpan.FromMilliseconds(25),
            SecondHoldAtPacket = 500,
            SecondHold = TimeSpan.FromMilliseconds(40),
        };
        var watch = new TransmitStallWatch(sink, Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        watch.Write(new float[Rate * 4]);
        watch.Drain();

        TransmitStallReport report = reports.Should().ContainSingle("one line per keyup").Subject;
        report.Stalls.Should().Be(2);
        report.Worst.Should().Be(TimeSpan.FromMilliseconds(40));
        watch.Snapshot.Seconds.Should().BeApproximately(0.065, 1e-9);
    }

    [Fact]
    public void A_Gap_Between_Two_Writes_In_One_Keyup_Counts()
    {
        // Rendering the next frame of an unpacked keyup between writes: the radio is owed audio
        // then too, and has none.
        var time = new FakeTimeProvider();
        var watch = new TransmitStallWatch(new PacedSink(time), Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        watch.Write(new float[Packet * 100]);
        time.Advance(TimeSpan.FromMilliseconds(30));
        watch.Write(new float[Packet * 100]);
        watch.Drain();

        reports.Should().ContainSingle().Which.Worst.Should().Be(TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public void Each_Keyup_Starts_Its_Own_Clock()
    {
        // The idle time between two keyups is not lateness: the second keyup must start level.
        var time = new FakeTimeProvider();
        var watch = new TransmitStallWatch(new PacedSink(time), Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        watch.Write(new float[Rate]);
        watch.Drain();
        time.Advance(TimeSpan.FromMinutes(5));
        watch.Write(new float[Rate]);
        watch.Drain();

        reports.Should().BeEmpty();
    }

    [Fact]
    public void A_Write_That_Fails_Does_Not_Carry_Its_Clock_Into_The_Next_Keyup()
    {
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time) { FailAtPacket = 10 };
        var watch = new TransmitStallWatch(sink, Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        Action failing = () => watch.Write(new float[Rate]);
        failing.Should().Throw<IOException>();
        time.Advance(TimeSpan.FromSeconds(30));
        sink.FailAtPacket = null;
        watch.Write(new float[Rate]);
        watch.Drain();

        reports.Should().BeEmpty();
    }

    [Fact]
    public void A_Drain_That_Fails_Does_Not_Carry_Its_Clock_Into_The_Next_Keyup()
    {
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time) { FailDrain = true };
        var watch = new TransmitStallWatch(sink, Packet, time);
        var reports = new List<TransmitStallReport>();
        watch.KeyupStalled += reports.Add;

        watch.Write(new float[Rate]);
        Action failing = watch.Drain;
        failing.Should().Throw<IOException>();
        time.Advance(TimeSpan.FromSeconds(30));
        sink.FailDrain = false;
        watch.Write(new float[Rate]);
        watch.Drain();

        reports.Should().BeEmpty();
        watch.Snapshot.Stalls.Should().Be(0);
    }

    [Fact]
    public void The_Audio_Reaches_The_Sink_Unchanged_And_In_Order()
    {
        var time = new FakeTimeProvider();
        var sink = new PacedSink(time) { Keep = true };
        var watch = new TransmitStallWatch(sink, Packet, time);
        float[] audio = [.. Enumerable.Range(0, 1000).Select(i => i / 1000f)];

        watch.Write(audio.AsSpan(0, 300));
        watch.Write(audio.AsSpan(300));
        watch.Drain();

        sink.Kept.Should().Equal(audio);
        sink.Calls.Should().OnlyContain(n => n <= Packet, "the sink is fed a packet at a time");
        sink.Drains.Should().Be(1);
        watch.SampleRate.Should().Be(Rate);
    }

    /// <summary>
    /// Paces as <c>FlexAudioOutput</c> does: after each packet's worth it sleeps until that much
    /// audio is due, and when it is already late it does not sleep at all, which is how it catches
    /// up after a stall. A hold is the sending thread not being run.
    /// </summary>
    private sealed class PacedSink(FakeTimeProvider time) : IAudioOutput
    {
        private long _start = -1;
        private long _sent;
        private int _packets;
        private int _pending;

        public double? HoldAtPacket { get; init; }

        public TimeSpan Hold { get; init; }

        public int? SecondHoldAtPacket { get; init; }

        public TimeSpan SecondHold { get; init; }

        public int? FailAtPacket { get; set; }

        public bool FailDrain { get; set; }

        public bool Keep { get; init; }

        public List<float> Kept { get; } = [];

        public List<int> Calls { get; } = [];

        public int Drains { get; private set; }

        public int SampleRate => Rate;

        public void Write(ReadOnlySpan<float> samples)
        {
            Calls.Add(samples.Length);
            if (Keep)
            {
                Kept.AddRange(samples.ToArray());
            }

            _pending += samples.Length;
            while (_pending >= Packet)
            {
                _pending -= Packet;
                Send();
            }
        }

        public void Drain()
        {
            Drains++;
            if (FailDrain)
            {
                _start = -1;
                _sent = 0;
                _pending = 0;
                throw new IOException("the radio went away");
            }

            if (_pending > 0)
            {
                _pending = 0;
                Send();
            }

            _start = -1;
            _sent = 0;
        }

        private void Send()
        {
            if (FailAtPacket == _packets)
            {
                throw new IOException("the radio went away");
            }

            if (_start < 0)
            {
                _start = time.GetTimestamp();
            }

            _sent += Packet;
            TimeSpan due = TimeSpan.FromTicks(_sent * TimeSpan.TicksPerSecond / Rate);
            TimeSpan ahead = due - time.GetElapsedTime(_start);
            if (ahead > TimeSpan.Zero)
            {
                time.Advance(ahead);
            }

            // Held up on the way out of the sleep: a thread that spends nearly all of a keyup
            // asleep is most likely to be kept waiting when it should wake.
            if (HoldAtPacket is double at && _packets == (int)at)
            {
                time.Advance(Hold);
            }

            if (SecondHoldAtPacket == _packets)
            {
                time.Advance(SecondHold);
            }

            _packets++;
        }
    }
}
