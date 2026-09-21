using Microsoft.Extensions.Time.Testing;
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
/// <para>Every test here drives a <see cref="FakeTimeProvider"/>: the figure under test is a
/// duration, and a duration checked against the wall clock is a test that fails on a loaded CI
/// box and passes everywhere else.</para>
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

    private static (SoundModemChannel Channel, FakeTimeProvider Time, Switch Busy) Station()
    {
        var time = new FakeTimeProvider();
        var busy = new Switch { Busy = false };
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42, channelBusySource: busy);
        channel.AddModem(2, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        channel.Csma.Persistence = 255;   // no roll: the only thing that can hold a frame here is the switch
        channel.Csma.SlotTimeMilliseconds = 10;
        return (channel, time, busy);
    }

    private static async Task<Task> StartAsync(
        SoundModemChannel channel, FakeTimeProvider time, CancellationToken cancellation)
    {
        Task transmitter = channel.RunTransmitterAsync(new Sink(SampleRate), new RecordingPtt(), cancellation);
        _ = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                time.Advance(TimeSpan.FromMilliseconds(10));
                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);
        await Task.Yield();
        return transmitter;
    }

    [Fact]
    public async Task A_Frame_On_A_Clear_Channel_Reports_Almost_No_Wait()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TimeSpan? held = null;
        channel.FrameTransmittedWithReport += (_, _, report) => held = report.HeldFor;

        Task sent = channel.EnqueueTransmit(2, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);
        await sent.WaitAsync(TimeSpan.FromSeconds(15));

        held.Should().NotBeNull();
        held!.Value.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            "nothing held it: the channel was clear and the roll always wins at persistence 255");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task A_Busy_Channel_Reports_The_Whole_Wait()
    {
        (SoundModemChannel channel, FakeTimeProvider time, Switch busy) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        TimeSpan? held = null;
        channel.FrameTransmittedWithReport += (_, _, report) => held = report.HeldFor;

        busy.Busy = true;
        Task sent = channel.EnqueueTransmit(2, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        // Hold the channel for a good while on the fake clock, then let go.
        DateTimeOffset releaseAfter = time.GetUtcNow() + TimeSpan.FromSeconds(20);
        while (time.GetUtcNow() < releaseAfter)
        {
            sent.IsCompleted.Should().BeFalse("carrier sense says the channel is occupied");
            await Task.Delay(5, CancellationToken.None);
        }

        busy.Busy = false;
        await sent.WaitAsync(TimeSpan.FromSeconds(30));

        held.Should().NotBeNull();
        held!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(15),
            "the frame sat behind carrier sense for twenty seconds of the channel's own clock, "
            + "and that is the number nothing in the station used to write down");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    [Fact]
    public async Task A_Frame_The_Modem_Refuses_Reports_Nothing()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _) = Station();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reports = new List<TimeSpan>();
        channel.FrameTransmittedWithReport += (_, _, report) => reports.Add(report.HeldFor);

        // Oversize for the mode: the modulator refuses it, so it never reaches the air.
        Task refused = channel.EnqueueTransmit(2, [.. Broadcast(), .. new byte[8192]]);
        Task transmitter = await StartAsync(channel, time, cancellation.Token);
        await Ignore(refused);

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
