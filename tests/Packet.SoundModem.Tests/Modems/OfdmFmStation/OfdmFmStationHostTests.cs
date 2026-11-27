using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems.OfdmFmStation;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The host's receive half over a real <see cref="SoundModemChannel"/>. No clock is read anywhere: the only time is the count of samples the test feeds,
/// and the only waits are hang guards through <see cref="Eventually"/>.
/// </summary>
public class OfdmFmStationHostTests
{
    private const int Rate = 48000;
    private const int Block = 960;

    private static SoundModemChannel Channel(int rate = Rate) => new(rate, randomSeed: 3);

    private static OfdmFmStationHost Inline(SoundModemChannel channel) =>
        new(channel, new Random(1), new OfdmFmStationHostOptions { RunJobsInline = true });

    private static OfdmFmStationHost Workers(SoundModemChannel channel, Action? beforeJob = null) =>
        new(channel, new Random(1), new OfdmFmStationHostOptions { WorkerCount = 2, BeforeJob = beforeJob });

    /// <summary>A data burst to everyone at the first-contact format, carrying one datagram, with a lead-in and a second of quiet each side.</summary>
    private static float[] Burst(byte[] datagram)
    {
        int format = StandardFormats.FirstContact;
        var from = new byte[PayloadCodec.RunBytes];
        PayloadCodec.WriteRun(0x01020304, 0, 1, from);
        ExtensionItem[] items = [new ExtensionItem(PayloadCodec.FromTag, from)];
        int symbols = PayloadCodec.SymbolsFor(format, 2 + datagram.Length, PayloadCodec.AddressBlockSize(1, false))
            ?? throw new InvalidOperationException("the datagram does not fit a burst");
        var payload = new byte[PayloadCodec.Capacity(format, symbols)];
        int length = PayloadCodec.Pack(null, items, [datagram], payload);
        length.Should().BeGreaterThan(0);

        var renderer = new BurstRenderer();
        var burst = new float[renderer.SamplesFor(format, symbols)];
        renderer.RenderData(BurstHeader.Data(5, 0, format, symbols, 0), payload.AsSpan(0, length), burst);

        var all = new float[Rate + burst.Length + (2 * Rate)];
        burst.CopyTo(all, Rate);
        return all;
    }

    private static byte[] Datagram(int seed, int length)
    {
        var d = new byte[length];
        new Random(seed).NextBytes(d);
        return d;
    }

    private static void Feed(SoundModemChannel channel, ReadOnlySpan<float> samples)
    {
        for (int at = 0; at < samples.Length; at += Block)
        {
            channel.ProcessReceive(samples.Slice(at, Math.Min(Block, samples.Length - at)));
        }
    }

    private static void Quiet(SoundModemChannel channel, int blocks)
    {
        var silence = new float[Block];
        for (int i = 0; i < blocks; i++)
        {
            channel.ProcessReceive(silence);
        }
    }

    /// <summary>
    /// Keys the channel for real (a transmitter that plays nothing) and feeds <paramref name="blocks"/> blocks into it while it is keyed, as the capture loop does,
    /// so the channel raises its keyed-block event for each. Returns when the channel is unkeyed again and the transmitter has ended.
    /// </summary>
    private static async Task KeyedAsync(SoundModemChannel channel, int blocks)
    {
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 0;
        channel.Csma.TxTailMilliseconds = 0;
        var rig = new GatedRig(Rate);
        rig.Admit(100);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fed = false;
        void OnChanged(bool transmitting)
        {
            if (transmitting && !fed)
            {
                fed = true;
                for (int i = 0; i < blocks; i++)
                {
                    channel.ProcessReceive(new float[Block]);
                }
            }
            else if (!transmitting)
            {
                ended.TrySetResult();
            }
        }

        channel.TransmittingChanged += OnChanged;
        using var stop = new CancellationTokenSource();
        Task sent = channel.EnqueueTransmit(_ => new float[Block]);
        Task transmitter = Task.Run(() => channel.RunTransmitterAsync(rig, rig, stop.Token));
        await Eventually.SignalAsync(ended.Task, "the transmitter to key and unkey");
        await Eventually.SignalAsync(sent, "the transmission to be reported sent");
        await stop.CancelAsync();
        // The transmitter has finished its own bookkeeping once it has stopped: the channel is unkeyed.
        await Eventually.CompletesAsync(transmitter, "the transmitter to stop");
        channel.TransmittingChanged -= OnChanged;
        fed.Should().BeTrue("the channel was keyed");
    }

    private static List<byte[]> Collect(OfdmFmStationHost host)
    {
        var got = new List<byte[]>();
        host.Received += e =>
        {
            lock (got)
            {
                got.Add(e.Datagram.ToArray());
            }
        };
        return got;
    }

    private static int Count(List<byte[]> got)
    {
        lock (got)
        {
            return got.Count;
        }
    }

    /// <summary>Feeds quiet blocks until <paramref name="count"/> datagrams have arrived: results are applied on the receive thread by the next block, so the audio has to keep coming.</summary>
    private static void FeedUntilReceived(SoundModemChannel channel, List<byte[]> got, int count)
    {
        var silence = new float[Block];
        Eventually.Until(() =>
        {
            channel.ProcessReceive(silence);
            return Count(got) >= count;
        }, $"{count} datagram(s) to be delivered");
    }

    [Fact]
    public async Task A_burst_with_keyed_blocks_each_side_is_delivered_once_with_jobs_inline()
    {
        SoundModemChannel channel = Channel();
        using OfdmFmStationHost host = Inline(channel);
        List<byte[]> got = Collect(host);
        byte[] datagram = Datagram(1, 60);
        float[] burst = Burst(datagram);
        long fed = 0;

        await KeyedAsync(channel, 10);
        fed += 10 * Block;
        Feed(channel, burst);
        fed += burst.Length;
        await KeyedAsync(channel, 10);
        fed += 10 * Block;
        Quiet(channel, 50);
        fed += 50 * Block;

        got.Should().HaveCount(1);
        got[0].Should().Equal(datagram);
        host.SampleCount.Should().Be(fed, "keyed blocks count as samples too");
    }

    [Fact]
    public async Task A_burst_with_keyed_blocks_each_side_is_delivered_once_with_worker_threads()
    {
        SoundModemChannel channel = Channel();
        using OfdmFmStationHost host = Workers(channel);
        List<byte[]> got = Collect(host);
        byte[] datagram = Datagram(2, 60);
        float[] burst = Burst(datagram);

        await KeyedAsync(channel, 10);
        Feed(channel, burst);
        await KeyedAsync(channel, 10);
        FeedUntilReceived(channel, got, 1);
        Quiet(channel, 50);

        got.Should().HaveCount(1);
        got[0].Should().Equal(datagram);
    }

    [Fact]
    public async Task A_second_burst_after_a_run_of_keyed_blocks_is_still_found_and_the_count_is_exact()
    {
        SoundModemChannel channel = Channel();
        using OfdmFmStationHost host = Inline(channel);
        List<byte[]> got = Collect(host);
        byte[] first = Datagram(3, 40);
        byte[] second = Datagram(4, 80);
        float[] burstOne = Burst(first);
        float[] burstTwo = Burst(second);

        Feed(channel, burstOne);
        await KeyedAsync(channel, 300);
        Feed(channel, burstTwo);
        Quiet(channel, 50);

        got.Should().HaveCount(2);
        got[0].Should().Equal(first);
        got[1].Should().Equal(second);
        host.SampleCount.Should().Be(burstOne.Length + (300L * Block) + burstTwo.Length + (50L * Block),
            "the station's clock counted the keyed stretch, so the second burst sits where it really was");
    }

    [Fact]
    public async Task Disposing_with_a_job_in_flight_waits_for_it_and_nothing_is_raised_afterwards()
    {
        SoundModemChannel channel = Channel();
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        int hooks = 0;
        OfdmFmStationHost host = Workers(channel, () =>
        {
            Interlocked.Increment(ref hooks);
            entered.Set();
            if (!gate.Wait(Eventually.HangGuard))
            {
                throw new TimeoutException("the gate was never opened");
            }
        });
        int events = 0;
        host.Received += _ => Interlocked.Increment(ref events);
        host.ChannelBusy += _ => Interlocked.Increment(ref events);
        host.LinkState += _ => Interlocked.Increment(ref events);
        float[] burst = Burst(Datagram(5, 50));

        Feed(channel, burst);
        await Eventually.SignalAsync(Task.Run(entered.Wait), "a worker to take a job");
        long countBefore = host.SampleCount;

        Task disposing = Task.Run(host.Dispose);
        gate.Set();
        await Eventually.SignalAsync(disposing, "Dispose to return");
        int eventsAtDispose = Volatile.Read(ref events);
        int hooksAtDispose = Volatile.Read(ref hooks);

        host.Dispose();
        Feed(channel, burst);
        Quiet(channel, 50);

        Volatile.Read(ref events).Should().Be(eventsAtDispose, "nothing is raised once Dispose has returned");
        Volatile.Read(ref hooks).Should().Be(hooksAtDispose, "no worker runs a job once Dispose has returned");
        host.SampleCount.Should().Be(countBefore, "the station is not given audio after Dispose");
    }

    [Fact]
    public void Disposing_twice_is_harmless_and_a_host_that_never_heard_anything_disposes_at_once()
    {
        SoundModemChannel channel = Channel();
        OfdmFmStationHost workers = Workers(channel);
        OfdmFmStationHost inline = Inline(Channel());

        workers.Dispose();
        workers.Dispose();
        inline.Dispose();
        inline.Dispose();
    }

    [Fact]
    public void Disposing_from_inside_a_handler_stops_the_rest_of_that_call()
    {
        SoundModemChannel channel = Channel();
        OfdmFmStationHost host = Inline(channel);
        int busy = 0;
        host.ChannelBusy += _ =>
        {
            busy++;
            host.Dispose();
        };

        Feed(channel, Burst(Datagram(6, 30)));

        busy.Should().Be(1, "the first event disposed the host, so no later one was raised");
    }

    [Fact]
    public void Lost_audio_is_counted_once_for_each_occasion_and_not_after_dispose()
    {
        SoundModemChannel channel = Channel();
        OfdmFmStationHost host = Inline(channel);
        host.AudioLostCount.Should().Be(0);

        channel.NoteReceiveAudioLost();
        host.AudioLostCount.Should().Be(1);
        Quiet(channel, 2);
        channel.NoteReceiveAudioLost();
        channel.NoteReceiveAudioLost();
        host.AudioLostCount.Should().Be(3);

        host.Dispose();
        channel.NoteReceiveAudioLost();
        host.AudioLostCount.Should().Be(3);
    }

    [Fact]
    public void Nothing_is_dropped_on_an_idle_host()
    {
        using OfdmFmStationHost host = Inline(Channel());

        host.BurstsDropped.Should().Be(0);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(12000)]
    public void A_channel_not_at_48000_Hz_is_refused_in_one_line(int rate)
    {
        SoundModemChannel channel = Channel(rate);

        Action act = () => _ = new OfdmFmStationHost(channel, new Random(1));

        act.Should().Throw<ArgumentException>()
            .WithMessage($"The adaptive OFDM-FM station needs a 48000 Hz channel; this one runs at {rate} Hz.");
    }
}
