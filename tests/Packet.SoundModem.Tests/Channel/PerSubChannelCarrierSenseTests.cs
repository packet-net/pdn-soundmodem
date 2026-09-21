using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// Carrier sense answers for one sub-channel, not for the whole station.
/// </summary>
/// <remarks>
/// <para>The station modelled here is GB7RDG's, which is where this was measured: three afsk300
/// modems at 850, 987 and 1120 Hz and a bpsk300 modem at 2150 Hz, all inside one Flex slice but
/// on four different RF frequencies. On 2026-09-21 it could not answer a connect request for
/// 3 minutes 48 seconds, because the transmit gate was the OR across every modem: the bpsk300
/// modem carrying the traffic was busy 81.1 % of those 228 s and the union across the four was
/// busy 96.4 %, one usable gap against fourteen. See packet-net/pdn-soundmodem#526.</para>
/// <para>Every modem here is a real one, wrapped so its busy detector can be held down by hand:
/// what decides whether two sub-channels defer to each other is their measured occupied
/// bandwidth, so a test double with an invented passband would prove nothing about the station
/// that failed.</para>
/// <para>Nothing here decides anything by the wall clock. The channel runs on a
/// <see cref="FakeTimeProvider"/> a helper task advances, as in
/// <see cref="TurnaroundHoldTests"/> and <see cref="HeldForTests"/>.</para>
/// </remarks>
public class PerSubChannelCarrierSenseTests
{
    private const int SampleRate = 12000;

    // GB7RDG's own placement, in Hz. The three AFSK channels are 133 and 137 Hz apart, which is
    // well inside one passband; the BPSK channel is 1.03 kHz above the nearest of them.
    private const int Afsk850 = 0;
    private const int Afsk987 = 4;
    private const int Afsk1120 = 3;
    private const int Bpsk2150 = 2;

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

    /// <summary>Carrier sense from the radio, with an operator of this test on the switch.</summary>
    private sealed class Switch : IChannelBusySource
    {
        public bool? Busy { get; set; }
    }

    /// <summary>
    /// A real modem with a switch on its busy detector: it modulates, and so measures, exactly as
    /// the shipped modem does, and answers <see cref="IModem.ChannelBusy"/> from the test instead
    /// of from its energy detector.
    /// </summary>
    private sealed class Switched(IModem inner) : IModem
    {
        /// <summary>What this modem's own detector is saying.</summary>
        public bool Busy { get; set; }

        public string Mode => inner.Mode;

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add => inner.FrameDecoded += value;
            remove => inner.FrameDecoded -= value;
        }

        // Left alone: with no radio to ask, the rule never consults it, and holding it down keeps
        // these tests on the audio answer the change is about.
        public bool CarrierDetect => false;

        public bool ChannelBusy => Busy;

        public void Process(ReadOnlySpan<float> samples) => inner.Process(samples);

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
            inner.Modulate(ax25Frame, txDelayMilliseconds);

        public void ResetCarrierState() => inner.ResetCarrierState();
    }

    private static byte[] Broadcast()
    {
        byte[] frame = Convert.FromHexString("8E846E9EB08CE48E846EA4888E6551");
        frame[14] = 0x03; // UI, so nothing holds the turnaround after it
        return [.. frame, (byte)0xF0, (byte)0x41];
    }

    private static (SoundModemChannel Channel, FakeTimeProvider Time, Switch Radio,
        Dictionary<int, Switched> Modems) Station(bool withRadio)
    {
        var time = new FakeTimeProvider();
        var radio = new Switch { Busy = withRadio ? false : null };
        var channel = new SoundModemChannel(
            SampleRate, time, randomSeed: 42, channelBusySource: withRadio ? radio : null);

        var modems = new Dictionary<int, Switched>();
        void Add(int sub, Func<Action<byte[]>, IModem> factory)
        {
            channel.AddModem(sub, sink =>
            {
                var switched = new Switched(factory(sink));
                modems[sub] = switched;
                return switched;
            });
        }

        Add(Afsk850, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850));
        Add(Bpsk2150, sink => new BpskMultiModem(SampleRate, sink, crc: true, 2150, baud: 300, offsetPairs: 4));
        Add(Afsk1120, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 1120));
        Add(Afsk987, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 987));

        channel.Csma.Persistence = 255;  // no roll: the only thing that can hold a frame is carrier sense
        channel.Csma.SlotTimeMilliseconds = 10;
        return (channel, time, radio, modems);
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

    /// <summary>Lets the fake clock run on for a while, and says whether the frame went.</summary>
    private static async Task<bool> WentWithin(Task sent, FakeTimeProvider time, TimeSpan window)
    {
        DateTimeOffset until = time.GetUtcNow() + window;
        while (time.GetUtcNow() < until)
        {
            if (sent.IsCompleted)
            {
                return true;
            }

            await Task.Delay(5, CancellationToken.None);
        }

        return sent.IsCompleted;
    }

    /// <summary>
    /// The regression guard for the whole issue: a modem 1.03 kHz away hearing something is not a
    /// reason to hold this one's frame.
    /// </summary>
    [Fact]
    public async Task A_Frame_Goes_Out_While_A_Non_Overlapping_Modem_Is_Busy()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, Dictionary<int, Switched> modems) =
            Station(withRadio: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Every AFSK sub-channel busy, which on the old rule was 100 % of the transmit gate.
        modems[Afsk850].Busy = true;
        modems[Afsk987].Busy = true;
        modems[Afsk1120].Busy = true;

        channel.ChannelBusy.Should().BeTrue("the station really can hear something, and that has not changed");
        channel.ChannelBusyFor(Bpsk2150).Should().BeFalse(
            "nothing in the bpsk300 modem's own passband is busy, and it cannot collide with "
            + "traffic 1.03 kHz away");

        Task sent = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);
        await sent.WaitAsync(TimeSpan.FromSeconds(20));

        sent.IsCompletedSuccessfully.Should().BeTrue(
            "the modem with the traffic had a clear passband; GB7RDG had fourteen such gaps in "
            + "228 s and took none of them");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// And the other half: two AFSK modems 137 Hz apart really do share a passband, so the
    /// narrowing must not let one transmit over the other.
    /// </summary>
    [Fact]
    public async Task A_Frame_Defers_To_A_Modem_Sharing_Its_Passband()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, Dictionary<int, Switched> modems) =
            Station(withRadio: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        modems[Afsk987].Busy = true;
        channel.ChannelBusyFor(Afsk850).Should().BeTrue(
            "850 and 987 Hz are 137 Hz apart and their measured bands overlap outright");

        Task sent = channel.EnqueueTransmit(Afsk850, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        (await WentWithin(sent, time, TimeSpan.FromSeconds(10))).Should().BeFalse(
            "the neighbouring modem is hearing something in this frame's own passband");

        modems[Afsk987].Busy = false;
        await sent.WaitAsync(TimeSpan.FromSeconds(20));

        sent.IsCompletedSuccessfully.Should().BeTrue("the passband cleared");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// The radio is a report on the receiver rather than on a waveform, so its opinion is still
    /// the station's.
    /// </summary>
    [Fact]
    public async Task A_Radio_Saying_Busy_Still_Holds_Every_Sub_Channel_Off()
    {
        (SoundModemChannel channel, FakeTimeProvider time, Switch radio, _) = Station(withRadio: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        radio.Busy = true;
        channel.ChannelBusyFor(Bpsk2150).Should().BeTrue();
        channel.ChannelBusyFor(Afsk850).Should().BeTrue();
        channel.ChannelBusyFor(Afsk1120).Should().BeTrue();

        Task sent = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        (await WentWithin(sent, time, TimeSpan.FromSeconds(10))).Should().BeFalse(
            "no modem is busy, but the station's radio says the channel is occupied and that "
            + "still decides for everybody");

        radio.Busy = false;
        await sent.WaitAsync(TimeSpan.FromSeconds(20));
        sent.IsCompletedSuccessfully.Should().BeTrue("the radio let go");

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>Half duplex: keying makes every receiver on the station deaf, not just one.</summary>
    [Fact]
    public async Task Our_Own_Transmission_Is_Busy_For_Every_Sub_Channel()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, _) = Station(withRadio: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        bool? busyForAnother = null;
        bool? busyForItself = null;
        bool? stationWide = null;
        channel.TransmittingChanged += on =>
        {
            if (!on || busyForAnother is not null)
            {
                return;
            }

            busyForAnother = channel.ChannelBusyFor(Afsk850);
            busyForItself = channel.ChannelBusyFor(Bpsk2150);
            stationWide = channel.ChannelBusy;
        };

        Task sent = channel.EnqueueTransmit(Bpsk2150, Broadcast());
        Task transmitter = await StartAsync(channel, time, cancellation.Token);
        await sent.WaitAsync(TimeSpan.FromSeconds(20));

        busyForAnother.Should().BeTrue(
            "a sub-channel 1.03 kHz away cannot be transmitted on while we are keyed either: one "
            + "PA, one antenna, and every receiver on the station is deaf for the keyup");
        busyForItself.Should().BeTrue();
        stationWide.Should().BeTrue();

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// A transmitter that is not a modem - paging, the CW ident, the operator's test transmission
    /// - has no passband anything here knows, and takes the station-wide answer it always had.
    /// </summary>
    [Fact]
    public async Task A_Transmitter_That_Is_Not_A_Modem_Still_Defers_To_The_Whole_Station()
    {
        (SoundModemChannel channel, FakeTimeProvider time, _, Dictionary<int, Switched> modems) =
            Station(withRadio: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        modems[Afsk850].Busy = true;

        var ident = new object();
        Task sent = channel.EnqueueTransmit(_ => new float[SampleRate / 10], source: ident);
        Task transmitter = await StartAsync(channel, time, cancellation.Token);

        (await WentWithin(sent, time, TimeSpan.FromSeconds(10))).Should().BeFalse(
            "nothing here knows what part of the band a paging or ident transmitter occupies, "
            + "and the conservative answer for an unknown passband is the station's");

        modems[Afsk850].Busy = false;
        await sent.WaitAsync(TimeSpan.FromSeconds(20));
        sent.IsCompletedSuccessfully.Should().BeTrue();

        await cancellation.CancelAsync();
        await Ignore(transmitter);
    }

    /// <summary>
    /// The geometry the rule rests on, measured rather than asserted from the mode names: which
    /// of GB7RDG's sub-channels share a passband and which do not.
    /// </summary>
    [Fact]
    public void The_Station_Geometry_Puts_The_Afsk_Cluster_Together_And_The_Bpsk_Apart()
    {
        ModemPassband Band(IModem modem) =>
            ModemPassband.Measure(modem, SampleRate)
            ?? throw new InvalidOperationException("every mode here renders the probe frame");

        ModemPassband afsk850 = Band(new Afsk300MultiModem(SampleRate, _ => { }, Afsk300Framing.Il2pCrc, 850));
        ModemPassband afsk987 = Band(new Afsk300MultiModem(SampleRate, _ => { }, Afsk300Framing.Il2pCrc, 987));
        ModemPassband afsk1120 = Band(new Afsk300MultiModem(SampleRate, _ => { }, Afsk300Framing.Il2pCrc, 1120));
        ModemPassband bpsk2150 = Band(
            new BpskMultiModem(SampleRate, _ => { }, crc: true, 2150, baud: 300, offsetPairs: 4));

        // Measured 2026-09-21 at 12 kHz: 680-1020, 820-1148, 961-1289 and 1980-2320 Hz.
        afsk850.HighHz.Should().BeInRange(950, 1100);
        afsk1120.HighHz.Should().BeInRange(1220, 1360);
        bpsk2150.LowHz.Should().BeInRange(1900, 2060);

        afsk850.Overlaps(afsk987).Should().BeTrue("137 Hz apart, and they overlap with no guard at all");
        afsk987.Overlaps(afsk1120).Should().BeTrue("133 Hz apart");
        afsk850.Overlaps(afsk1120).Should().BeTrue("270 Hz apart, still inside one passband");

        bpsk2150.Overlaps(afsk1120).Should().BeFalse(
            "691 Hz of measured clear air between them, which is 191 Hz more than the guard "
            + "spends from both sides");
        bpsk2150.Overlaps(afsk987).Should().BeFalse();
        bpsk2150.Overlaps(afsk850).Should().BeFalse();

        // Symmetric, because "would transmitting here collide with that" has to be.
        afsk1120.Overlaps(bpsk2150).Should().Be(bpsk2150.Overlaps(afsk1120));
    }

    /// <summary>
    /// A modem whose passband could not be measured keeps its station-wide effect: carrier sense
    /// that does not know has to defer.
    /// </summary>
    [Fact]
    public void An_Unmeasured_Modem_Still_Holds_Everything_Off()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        Silent? silent = null;
        channel.AddModem(Afsk850, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850));
        channel.AddModem(Bpsk2150, _ => silent = new Silent());

        silent!.Busy = true;
        channel.ChannelBusyFor(Afsk850).Should().BeTrue(
            "a modem that will not render a probe frame could be anywhere in the band, and an "
            + "unknown passband is one that might be this one's");
    }

    /// <summary>A modem that refuses to modulate at all, so nothing can measure it.</summary>
    private sealed class Silent : IModem
    {
        public bool Busy { get; set; }

        public string Mode => "silent";

#pragma warning disable CS0067 // nothing decodes here
        public event Action<byte[], FrameQuality>? FrameDecoded;
#pragma warning restore CS0067

        public bool CarrierDetect => false;

        public bool ChannelBusy => Busy;

        public void Process(ReadOnlySpan<float> samples)
        {
        }

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
            throw new ArgumentException("this mode does not transmit", nameof(ax25Frame));

        public void ResetCarrierState()
        {
        }
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
