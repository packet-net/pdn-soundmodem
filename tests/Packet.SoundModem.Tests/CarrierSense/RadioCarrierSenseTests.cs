using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.CarrierSense;

using Packet.Radio;

/// <summary>
/// Carrier sense taken from the radio's control channel rather than from the audio.
/// </summary>
/// <remarks>
/// <para>The serial conversation itself belongs to Packet.Radio and is tested there against a real
/// TM8110. What belongs here is the deciding: what this modem concludes from what a radio tells
/// it, and in particular what it concludes when the radio tells it nothing.</para>
/// <para>No test here waits on wall time. The poll loop's scheduling is separated from its one
/// read for exactly that reason, and these drive the read.</para>
/// </remarks>
public class RadioCarrierSenseTests
{
    /// <summary>A radio the test drives by hand.</summary>
    private sealed class FakeRadio : IRadioControl
    {
        private readonly Queue<float> _rssi = new();

        public RadioCapabilities Capabilities { get; set; } =
            RadioCapabilities.CarrierSense | RadioCapabilities.RssiRead;

        public bool? ChannelBusy { get; set; }

        public int RssiReads { get; private set; }

        public bool RssiThrows { get; set; }

#pragma warning disable CS0067 // Nothing here subscribes: this source reads ChannelBusy, which the
                              // driver maintains from the same edges.
        public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;
#pragma warning restore CS0067

        public void WillRead(params float[] dbm)
        {
            foreach (float d in dbm)
            {
                _rssi.Enqueue(d);
            }
        }

        public ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default)
        {
            RssiReads++;
            if (RssiThrows)
            {
                throw new IOException("the cable fell out");
            }

            return ValueTask.FromResult(_rssi.Count > 0 ? _rssi.Dequeue() : -120f);
        }

        public ValueTask SetTransmitterAsync(bool on, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        // The interface is IAsyncDisposable, and the fake records whether anybody disposed it:
        // this source BORROWS the radio and closing a host's serial link would be a real bug.
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static RadioBusySource Source(FakeRadio radio, double? busyAbove = null) =>
        new(radio, busyAbove);

    [Fact]
    public void The_Radios_Squelch_Alone_Answers_The_Question()
    {
        var radio = new FakeRadio();
        RadioBusySource source = Source(radio);

        Assert.Null(source.Busy);          // before the radio's first report
        radio.ChannelBusy = true;
        Assert.True(source.Busy);
        radio.ChannelBusy = false;
        Assert.False(source.Busy);
    }

    [Fact]
    public async Task An_Rssi_Threshold_Can_Hear_What_An_Open_Squelch_Cannot()
    {
        // The case this exists for: a data station runs squelch open, so DCD never asserts and
        // only a level reading can tell anybody a carrier is there.
        var radio = new FakeRadio { ChannelBusy = false };
        RadioBusySource source = Source(radio, busyAbove: -110);

        radio.WillRead(-95f);
        await source.PollOnceAsync(CancellationToken.None);
        Assert.True(source.Busy);
        Assert.Equal(-95, source.LastRssiDbm);

        radio.WillRead(-118f);
        await source.PollOnceAsync(CancellationToken.None);
        Assert.False(source.Busy);
    }

    [Fact]
    public async Task A_Faulting_Rssi_Read_Stops_Claiming_To_Know_Rather_Than_Latching()
    {
        // Holding the last answer would risk a busy that never clears, and a busy that never
        // clears silences the station. Reverting to "no opinion" costs carrier sense instead.
        var radio = new FakeRadio { ChannelBusy = null };
        RadioBusySource source = Source(radio, busyAbove: -110);

        radio.WillRead(-90f);
        await source.PollOnceAsync(CancellationToken.None);
        Assert.True(source.Busy);

        radio.RssiThrows = true;
        for (int i = 0; i < RadioBusySource.FailuresBeforeUnknown; i++)
        {
            await source.PollOnceAsync(CancellationToken.None);
        }

        Assert.Null(source.Busy);
        Assert.Null(source.LastRssiDbm);

        radio.RssiThrows = false;
        radio.WillRead(-90f);
        await source.PollOnceAsync(CancellationToken.None);
        Assert.True(source.Busy);          // and it recovers when the reads come back
    }

    [Fact]
    public async Task A_Mechanism_That_Does_Not_Know_Cannot_Vote_Clear()
    {
        var radio = new FakeRadio { ChannelBusy = true };
        RadioBusySource source = Source(radio, busyAbove: -110);

        radio.WillRead(-118f);             // RSSI says quiet, the squelch says occupied
        await source.PollOnceAsync(CancellationToken.None);
        Assert.True(source.Busy);
    }

    [Fact]
    public void Capabilities_Are_Probed_Before_Either_Mechanism_Is_Believed()
    {
        // The interface's contract is that ReadRssiDbmAsync THROWS on a radio without RssiRead,
        // and that ChannelBusy is meaningless without CarrierSense. A radio that advertises
        // neither has to produce no opinion, not a confident false.
        var radio = new FakeRadio { Capabilities = RadioCapabilities.None, ChannelBusy = true };
        RadioBusySource source = Source(radio, busyAbove: -110);

        Assert.Null(source.Busy);
        Assert.False(source.PollsRssi);
    }

    [Fact]
    public async Task A_Radio_Without_Rssi_Is_Not_Polled_Even_When_A_Threshold_Is_Configured()
    {
        var radio = new FakeRadio { Capabilities = RadioCapabilities.CarrierSense };
        RadioBusySource source = Source(radio, busyAbove: -110);

        Assert.False(source.PollsRssi);
        await source.StartAsync(CancellationToken.None);
        Assert.Equal(0, radio.RssiReads);
    }

    [Fact]
    public async Task Dcd_Only_Starts_Nothing_And_Costs_No_Serial_Traffic()
    {
        var radio = new FakeRadio();
        RadioBusySource source = Source(radio);

        await source.StartAsync(CancellationToken.None);
        Assert.Equal(0, radio.RssiReads);
    }

    [Fact]
    public async Task The_Radio_Is_Borrowed_And_Never_Closed()
    {
        // Under packet.net the host owns this serial link. Closing it from in here would take the
        // host's radio control down with it.
        var radio = new FakeRadio();
        RadioBusySource source = Source(radio, busyAbove: -110);
        radio.WillRead(-90f);
        await source.PollOnceAsync(CancellationToken.None);

        Assert.False(radio.Disposed);
    }

    [Fact]
    public void A_Host_Supplied_Source_Wins_Over_Opening_A_Port_Here()
    {
        // The in-process case: packet.net already holds the port, so nothing here may open one.
        var radio = new FakeRadio { ChannelBusy = true };
        try
        {
            ChannelBusySources.Host = Source(radio);
            Assert.Same(ChannelBusySources.Host, ChannelBusySources.Resolve());
            Assert.True(ChannelBusySources.Resolve()!.Busy);
        }
        finally
        {
            ChannelBusySources.Host = null;
        }
    }

    [Fact]
    public void With_Neither_A_Host_Nor_A_Station_File_Nothing_Touches_A_Serial_Port()
    {
        ChannelBusySources.Host = null;
        Assert.Null(TaitCarrierSense.ForStation(null));
        Assert.Null(TaitCarrierSense.ForStation(new StationRadio()));
        Assert.Null(TaitCarrierSense.ForStation(new StationRadio(TaitPort: "   ")));
    }

    /// <summary>A source the test sets by hand, standing in for a radio.</summary>
    private sealed class StubSource(bool? busy) : IChannelBusySource
    {
        public bool? Busy { get; set; } = busy;
    }

    /// <summary>
    /// A station carrying one OFDM-FM modem, with whatever carrier sense the test supplies.
    /// </summary>
    /// <remarks>
    /// The station and not the modem, because that is where the answer is decided since #522. A
    /// modem's own <c>ChannelBusy</c> is its audio opinion and no longer gates anything by itself.
    /// </remarks>
    private static SoundModemChannel Station(IChannelBusySource? source)
    {
        var channel = new SoundModemChannel(
            OfdmFmParameters.Synthetic.SampleRate, channelBusySource: source);
        channel.AddModem(
            0, sink => new OfdmFmModem("ofdm-fm:test", OfdmFmParameters.Synthetic, sink));
        return channel;
    }

    [Fact]
    public void A_Configured_Radio_Decides_Whether_The_Station_May_Transmit()
    {
        var source = new StubSource(false);
        SoundModemChannel station = Station(source);
        Assert.False(station.ChannelBusy);

        source.Busy = true;
        Assert.True(station.ChannelBusy);
    }

    [Fact]
    public void A_Radio_With_No_Opinion_Leaves_The_Station_Free_To_Transmit()
    {
        // Fails open at the station too, not just inside the source.
        Assert.False(Station(new StubSource(null)).ChannelBusy);
    }

    [Fact]
    public void Without_A_Radio_The_Shipped_Audio_Behaviour_Is_Untouched()
    {
        // The energy detector is dropped only for a station that has something better to ask.
        // Everyone else keeps exactly what they had, defects and all: see
        // docs/dev/carrier-sense.md.
        Assert.False(Station(null).ChannelBusy);
    }

    [Fact]
    public void A_Host_Source_Reaches_A_Station_That_Was_Given_None()
    {
        // The one-line integration for an in-process host: register the radio, build the channel.
        // If this ever stops working, packet.net loses carrier sense silently.
        try
        {
            ChannelBusySources.Host = new StubSource(true);
            Assert.True(Station(null).ChannelBusy);
        }
        finally
        {
            ChannelBusySources.Host = null;
        }
    }

    [Fact]
    public void A_Radio_That_Is_Not_There_Costs_Carrier_Sense_And_Nothing_Else()
    {
        // The path that protects daemon start-up. A modem is constructed while the daemon starts,
        // so an unopenable port must produce a source with no opinion, never an exception: the
        // cost of a wrong answer here is not a missing feature, it is a station that will not run.
        var config = new StationRadio(TaitPort: $"/dev/nonexistent-{Guid.NewGuid():N}");

        TaitCarrierSense? sense = TaitCarrierSense.ForStation(config);

        Assert.NotNull(sense);
        Assert.Null(sense.Busy);
        Assert.Null(sense.LastRssiDbm);
        sense.Dispose();
    }

    [Fact]
    public void A_Station_File_Naming_A_Missing_Port_Still_Builds_A_Station()
    {
        var config = new StationRadio(TaitPort: $"/dev/nonexistent-{Guid.NewGuid():N}");
        try
        {
            ChannelBusySources.Host = TaitCarrierSense.ForStation(config);
            Assert.False(Station(ChannelBusySources.Resolve()).ChannelBusy);
        }
        finally
        {
            ChannelBusySources.Host = null;
        }
    }

    [Fact]
    public void Knowing_Nothing_At_All_Is_Null_So_The_Gate_Fails_Open()
    {
        // A station whose radio link dies keeps transmitting with no carrier sense, which is where
        // it was before this feature. Failing closed would let a pulled USB cable silence it.
        Assert.Null(RadioBusySource.Combine(dcd: null, rssiAboveThreshold: null));
    }
}
