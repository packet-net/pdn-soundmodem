using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A held time says where it went: a busy channel and a frame behind this station's own window
/// are the same number and opposite diagnoses, and the channel is the only party that can tell
/// them apart.
/// </summary>
/// <remarks>
/// <para>GB7RDG-2 on 2026-09-21 reported 8.3 s held on one I-frame. Read on its own that is a
/// frequency nobody can use; read with the breakdown it is the third frame of a MAXFRAME=3
/// window, 1.5 s of channel access and 6.8 s of this station's own two earlier bursts, on a
/// channel that was working normally. Nothing in the station distinguished the two, and an
/// operator cannot, because a KISS host cannot see the wait at all.</para>
/// <para>Every test here runs on a <see cref="FakeTimeProvider"/> with
/// <see cref="VirtualAir.PacedSink"/> for a sound card, so a keyup costs the clock what it would
/// cost the air. That matters more here than anywhere else: with a free write there is no own
/// airtime for a frame to be behind, and the case this exists to separate would not arise.</para>
/// </remarks>
public class TransmitWaitsTests
{
    private const int SampleRate = 12000;

    private const int Afsk850 = 0;
    private const int Bpsk2150 = 2;

    /// <summary>Carrier sense from the radio, with an operator of this test on the switch.</summary>
    private sealed class Switch : IChannelBusySource
    {
        public bool? Busy { get; set; }
    }

    /// <summary>
    /// A real modem with a switch on its busy detector, as in
    /// <see cref="PerSubChannelCarrierSenseTests"/>: it modulates and measures its passband
    /// exactly as the shipped modem does, and answers <see cref="IModem.ChannelBusy"/> from the
    /// test instead of from its energy detector. Which sub-channels overlap is the whole point of
    /// the figures below, so an invented passband would prove nothing.
    /// </summary>
    private sealed class Switched(IModem inner) : IModem
    {
        public bool Busy { get; set; }

        public string Mode => inner.Mode;

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add => inner.FrameDecoded += value;
            remove => inner.FrameDecoded -= value;
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => Busy;

        public void Process(ReadOnlySpan<float> samples) => inner.Process(samples);

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
            inner.Modulate(ax25Frame, txDelayMilliseconds);

        public void ResetCarrierState() => inner.ResetCarrierState();
    }

    /// <summary>A UI frame: nothing expects an answer, so no turnaround hold confuses the sums.</summary>
    private static byte[] Broadcast(byte marker = 0x41)
    {
        byte[] frame = Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");
        frame[14] = 0x03;
        return [.. frame, (byte)0xF0, marker];
    }

    /// <summary>An RR poll: an answer is owed, so the turnaround hold runs after it.</summary>
    private static byte[] Poll() => Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");

    private static (SoundModemChannel Channel, FakeTimeProvider Time, Switch Radio,
        Dictionary<int, Switched> Modems) Station(bool withRadio = true)
    {
        var time = new FakeTimeProvider();
        var radio = new Switch { Busy = withRadio ? false : null };
        var channel = new SoundModemChannel(
            SampleRate, time, randomSeed: 42, channelBusySource: withRadio ? radio : null);

        var modems = new Dictionary<int, Switched>();
        void Add(int sub, Func<Action<byte[]>, IModem> factory) =>
            channel.AddModem(sub, sink =>
            {
                var switched = new Switched(factory(sink));
                modems[sub] = switched;
                return switched;
            });

        Add(Afsk850, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850));
        Add(Bpsk2150, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));

        channel.Csma.Persistence = 255;   // no roll unless a test asks for one
        channel.Csma.SlotTimeMilliseconds = 10;
        channel.Csma.TxDelayMilliseconds = 300;
        channel.Csma.TxTailMilliseconds = 0;
        return (channel, time, radio, modems);
    }

    private static async Task<Task> StartAsync(
        SoundModemChannel channel, FakeTimeProvider time, CancellationToken cancellation)
    {
        Task transmitter = channel.RunTransmitterAsync(
            new VirtualAir.PacedSink(SampleRate, time), new RecordingPtt(), cancellation);
        VirtualAir.Pump(time, cancellation);
        await Task.Yield();
        return transmitter;
    }

    /// <summary>Everything the station said about the frames it sent, in the order it sent them.</summary>
    private static List<TransmitReport> Watch(SoundModemChannel channel)
    {
        var reports = new List<TransmitReport>();
        channel.FrameTransmittedWithReport += (_, _, report) => reports.Add(report);
        return reports;
    }

    /// <summary>
    /// The invariant the whole design rests on: whatever the causes, the parts add up to the
    /// figure the station already publishes as <c>held_ms</c>.
    /// </summary>
    private static void PartsAddUp(TransmitWaits waits)
    {
        TimeSpan sum = waits.ChannelBusy + waits.Backoff + waits.TurnaroundHold
            + waits.TransmitInhibit + waits.OurTransmission + waits.OurTurn + waits.Unattributed;
        sum.Should().Be(waits.Total,
            "a breakdown that does not reconcile with the total an operator already has is worse "
            + "than no breakdown");
    }

    /// <summary>
    /// The case that started this: three frames of one window, one keyup, and the last of them
    /// blames this station's own airtime rather than the channel.
    /// </summary>
    [Fact]
    public async Task A_Frame_Behind_Our_Own_Window_Blames_Our_Own_Transmissions()
    {
        (SoundModemChannel channel, FakeTimeProvider time, Switch radio, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        List<TransmitReport> sent = Watch(channel);

        radio.Busy = true;
        DateTimeOffset queued = time.GetUtcNow();
        Task a = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task b = channel.EnqueueTransmit(Bpsk2150, Broadcast(0x42));
        Task c = channel.EnqueueTransmit(Bpsk2150, Broadcast(0x43));
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await VirtualAir.AdvanceToAsync(time, queued + TimeSpan.FromMilliseconds(800));
        TimeSpan shut = time.GetUtcNow() - queued;
        radio.Busy = false;
        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(60));

        sent.Should().HaveCount(3);
        foreach (TransmitReport report in sent)
        {
            PartsAddUp(report.Waits);
        }

        // The first frame of the window is the one that actually waited for the channel.
        sent[0].Waits.ChannelBusy.Should().BeCloseTo(shut, VirtualAir.Tolerance);
        sent[0].Waits.OurTransmission.Should().BeLessThan(VirtualAir.Tolerance,
            "nothing of ours was on the air before it");
        sent[0].Waits.Dominant.Should().Be(TransmitWaitCause.ChannelBusy);

        // The third inherits that same wait - the channel was busy for it too, and it was going
        // nowhere either - and adds the two bursts it sat behind.
        sent[2].Waits.ChannelBusy.Should().BeCloseTo(shut, VirtualAir.Tolerance,
            "a frame queued behind another inherits what was holding the one in front of it");
        sent[2].Waits.OurTransmission.Should().BeGreaterThan(TimeSpan.FromSeconds(2),
            "two whole bursts of ours went out in front of it, under the same PTT");
        sent[2].Waits.Dominant.Should().Be(TransmitWaitCause.OurTransmission,
            "8.3 s behind our own window is a station working normally, and it must not read as "
            + "8.3 s of somebody else occupying the frequency");
        sent[2].Waits.Describe().Should().Contain("behind our own transmissions");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// A frame held by carrier sense says which sub-channel asserted it - the diagnostic for
    /// whether the per-sub-channel rule is deferring to the right thing.
    /// </summary>
    [Fact]
    public async Task A_Frame_Held_By_Carrier_Sense_Names_The_Sub_Channel()
    {
        // No radio, so the answer comes from the modems' own detectors and can be narrowed.
        (SoundModemChannel channel, FakeTimeProvider time, _, Dictionary<int, Switched> modems) =
            Station(withRadio: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        List<TransmitReport> sent = Watch(channel);

        modems[Bpsk2150].Busy = true;
        DateTimeOffset queued = time.GetUtcNow();
        Task held = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await VirtualAir.AdvanceToAsync(time, queued + TimeSpan.FromMilliseconds(1500));
        TimeSpan shut = time.GetUtcNow() - queued;
        modems[Bpsk2150].Busy = false;
        await held.WaitAsync(TimeSpan.FromSeconds(60));

        sent.Should().HaveCount(1);
        TransmitWaits waits = sent[0].Waits;
        PartsAddUp(waits);
        waits.ChannelBusy.Should().BeCloseTo(shut, VirtualAir.Tolerance);
        waits.BusiestSubChannel.Should().Be(Bpsk2150, "its own modem is what heard the channel occupied");
        waits.SubChannelList().Should().Be("2");
        waits.RadioSaidBusy.Should().BeFalse("this station has no radio to ask");
        waits.Describe().Should().Contain("channel busy on ch2");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// The station's radio, rather than any one modem, can be what said the channel was busy -
    /// and then no sub-channel is named, because none of them is the reason.
    /// </summary>
    [Fact]
    public async Task A_Radios_Answer_Is_Not_Blamed_On_A_Sub_Channel()
    {
        (SoundModemChannel channel, FakeTimeProvider time, Switch radio, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        List<TransmitReport> sent = Watch(channel);

        radio.Busy = true;
        DateTimeOffset queued = time.GetUtcNow();
        Task held = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await VirtualAir.AdvanceToAsync(time, queued + TimeSpan.FromSeconds(1));
        radio.Busy = false;
        await held.WaitAsync(TimeSpan.FromSeconds(60));

        TransmitWaits waits = sent[0].Waits;
        PartsAddUp(waits);
        waits.RadioSaidBusy.Should().BeTrue();
        waits.SubChannelList().Should().BeEmpty(
            "the radio answers for the whole station, and blaming a modem for it would be an "
            + "invented diagnosis");
        waits.Describe().Should().Contain("the radio said so");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// A frame held by another service before it ever reached a queue reports that, not a channel
    /// that was clear the whole time.
    /// </summary>
    [Fact]
    public async Task A_Frame_Held_By_Transmit_Inhibit_Says_So()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        List<TransmitReport> sent = Watch(channel);

        var inhibited = true;
        channel.TransmitInhibit = () => inhibited;
        DateTimeOffset queued = time.GetUtcNow();
        Task waiting = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await VirtualAir.AdvanceToAsync(time, queued + TimeSpan.FromSeconds(2));
        TimeSpan shut = time.GetUtcNow() - queued;
        inhibited = false;
        await waiting.WaitAsync(TimeSpan.FromSeconds(60));

        TransmitWaits waits = sent[0].Waits;
        PartsAddUp(waits);
        waits.TransmitInhibit.Should().BeGreaterThan(shut - TimeSpan.FromMilliseconds(200),
            "the frame was not on any queue for that stretch, so nothing else was counting it");
        waits.Dominant.Should().Be(TransmitWaitCause.TransmitInhibit);
        waits.Describe().Should().Contain("another service held the channel");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// One modem's frame waiting out the turnaround hold another modem's poll started blames the
    /// hold, which is the station's own doing, rather than a busy channel.
    /// </summary>
    [Fact]
    public async Task A_Frame_Waiting_Out_A_Turnaround_Hold_Says_So()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        List<TransmitReport> sent = Watch(channel);
        channel.QuietAfterTransmit = (_, frame) =>
            Ax25ReplyExpectation.ExpectsReply(frame) ? channel.TurnaroundHold : null;

        Task poll = channel.EnqueueTransmit(Bpsk2150, Poll());   // an answer is owed after this
        Task other = channel.EnqueueTransmit(Afsk850, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        await poll.WaitAsync(TimeSpan.FromSeconds(60));
        await other.WaitAsync(TimeSpan.FromSeconds(60));

        sent.Should().HaveCount(2);
        TransmitWaits waits = sent[1].Waits;
        PartsAddUp(waits);
        waits.TurnaroundHold.Should().BeGreaterThan(TimeSpan.FromMilliseconds(400),
            "the hold is two TXDELAYs and a slot, and this frame sat out all of it");
        waits.OurTransmission.Should().BeGreaterThan(TimeSpan.FromMilliseconds(400),
            "before the hold it waited out the poll's own keyup, which is our airtime");
        waits.ChannelBusy.Should().Be(TimeSpan.Zero, "nobody else was on the channel at any point");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// With no wait at all there is nothing to name, and the line that reports it says nothing
    /// rather than picking the largest of six numbers that are all about zero.
    /// </summary>
    [Fact]
    public async Task A_Frame_That_Did_Not_Wait_Names_No_Cause()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        List<TransmitReport> sent = Watch(channel);

        Task straight = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);
        await straight.WaitAsync(TimeSpan.FromSeconds(30));

        TransmitWaits waits = sent[0].Waits;
        PartsAddUp(waits);
        waits.Total.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        waits.Dominant.Should().Be(TransmitWaitCause.None);
        waits.Describe().Should().BeNull();

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    private static async Task Ignore(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e) when (e is OperationCanceledException or ArgumentException or InvalidOperationException)
        {
        }
    }
}
