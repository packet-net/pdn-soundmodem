using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The channel reports how long it held each transmission before putting it on the air.
/// </summary>
/// <remarks>
/// <para>A KISS host cannot measure this. It writes a frame to a socket, the write returns, and
/// nothing afterwards tells it whether the frame left immediately or four minutes later - so its
/// own retry timers run from the write. On 2026-09-21 GB7RDG-2 answered a connect request 3 m 48 s
/// late because carrier sense held the UA that long, and by then LinBPQ had queued four more
/// identical polls behind it, all of which went out in one keyup with the UA. Nothing in the
/// station's journal, frame log or page said the wait had happened, so the only way to find it was
/// to subtract timestamps by hand.</para>
/// <para>Every test here runs on a <see cref="VirtualAir.Clock"/>: the figure under test is a
/// duration, and a duration checked against the wall clock is a test that fails on a loaded CI
/// box and passes everywhere else. The clock moves only while the transmitter is parked on it, so
/// twenty seconds of carrier sense cost no real time and cannot be stretched by a slow box.</para>
/// </remarks>
public class HeldForTests
{
    private const int SampleRate = 12000;

    private sealed class Sink(int sampleRate) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
        }

        public void Drain()
        {
        }
    }

    /// <summary>Carrier sense an operator of this test holds the switch for.</summary>
    private sealed class Switch : IChannelBusySource
    {
        public bool? Busy { get; set; }
    }

    private static byte[] Broadcast()
    {
        byte[] frame = Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");
        frame[14] = 0x03; // UI, so nothing holds the turnaround after it
        return [.. frame, (byte)0xF0, (byte)0x41];
    }

    private static (SoundModemChannel Channel, VirtualAir.Clock Time, Switch Busy) Station()
    {
        var time = new VirtualAir.Clock();
        var busy = new Switch { Busy = false };
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42, channelBusySource: busy);
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;   // no roll: the only thing that can hold a frame here is the switch
        channel.Csma.SlotTimeMilliseconds = 10;
        return (channel, time, busy);
    }

    private static Task Start(SoundModemChannel channel, CancellationToken cancellation) =>
        channel.RunTransmitterAsync(new Sink(SampleRate), new RecordingPtt(), cancellation);

    [Fact]
    public async Task A_Frame_On_A_Clear_Channel_Reports_Almost_No_Wait()
    {
        (SoundModemChannel channel, VirtualAir.Clock time, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TimeSpan? held = null;
        channel.FrameTransmittedWithReport += (_, _, report) => held = report.HeldFor;

        Task sent = channel.EnqueueTransmit(2, Broadcast());
        Task transmitter = Start(channel, cancellation.Token);
        await time.RunUntilAsync(sent);

        held.Should().NotBeNull();
        held!.Value.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "nothing held it: the channel was clear and the roll always wins at persistence 255");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task A_Busy_Channel_Reports_The_Whole_Wait()
    {
        (SoundModemChannel channel, VirtualAir.Clock time, Switch busy) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        TimeSpan? held = null;
        channel.FrameTransmittedWithReport += (_, _, report) => held = report.HeldFor;

        busy.Busy = true;
        DateTimeOffset queued = time.GetUtcNow();
        Task sent = channel.EnqueueTransmit(2, Broadcast());
        Task transmitter = Start(channel, cancellation.Token);

        // Hold the channel for a good while on the clock, then let go.
        await time.AdvanceToAsync(queued + TimeSpan.FromSeconds(20));
        sent.IsCompleted.Should().BeFalse("carrier sense says the channel is occupied");
        TimeSpan shut = time.GetUtcNow() - queued;

        busy.Busy = false;
        await time.RunUntilAsync(sent);

        held.Should().NotBeNull();
        held!.Value.Should().BeCloseTo(shut, VirtualAir.Tolerance,
            "the frame sat behind carrier sense for twenty seconds of the channel's own clock, "
            + "and that is the number nothing in the station used to write down");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task A_Frame_The_Modem_Refuses_Reports_Nothing()
    {
        (SoundModemChannel channel, VirtualAir.Clock time, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reports = new List<TimeSpan>();
        channel.FrameTransmittedWithReport += (_, _, report) => reports.Add(report.HeldFor);

        // Oversize for the mode: the modulator refuses it, so it never reaches the air.
        Task refused = channel.EnqueueTransmit(2, [.. Broadcast(), .. new byte[8192]]);
        Task transmitter = Start(channel, cancellation.Token);
        await Ignore(time.RunUntilAsync(refused));

        reports.Should().BeEmpty(
            "a frame that was never transmitted did not wait for the channel in any sense an "
            + "operator is asking about, and a held time on it would be a row for a frame that "
            + "does not exist");

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
