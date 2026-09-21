using Packet.Radio;

namespace Packet.SoundModem.CarrierSense;

/// <summary>
/// Carrier sense read from a radio's control channel, for a radio somebody else owns.
/// </summary>
/// <remarks>
/// <para><b>Why the radio and not the audio.</b> Two audio detectors were built and measured here
/// and both are wrong on an FM path; see <c>docs/carrier-sense-on-fm.md</c>. The short version is
/// that an FM receiver goes QUIET when a carrier arrives, so a detector waiting for audio to rise
/// asserts at the END of every burst instead of the start, and a detector that correctly watches
/// for the quieting needs absolute levels that differ by 22 dB between two nominally identical
/// stations. The radio, meanwhile, has a squelch and a calibrated RSSI meter and will simply tell
/// you.</para>
/// <para><b>It borrows the radio.</b> Opening, closing and configuring are the owner's business and
/// this class does none of them. That is the point: under packet.net the host owns the serial link
/// and hands its own <see cref="IRadioControl"/> in, and standalone
/// <see cref="TaitCarrierSense"/> opens one and passes it here. Any driver behind the interface
/// works, not just Tait.</para>
/// <para><b>Both mechanisms are optional and are feature-probed.</b> The interface's contract is
/// that a caller probes <see cref="IRadioControl.Capabilities"/> first, and that
/// <see cref="IRadioControl.ReadRssiDbmAsync"/> THROWS on a radio without it. DCD costs no traffic
/// because the driver maintains it from the radio's unsolicited reports; RSSI is polled, and earns
/// its keep on a data station whose squelch is held open, where DCD never asserts at all.</para>
/// </remarks>
public sealed class RadioBusySource : IChannelBusySource
{
    /// <summary>Consecutive failed reads before RSSI stops claiming to know anything.</summary>
    /// <remarks>Holding the last answer instead would risk a busy that never clears, which is the
    /// failure that can silence a station.</remarks>
    internal const int FailuresBeforeUnknown = 3;

    private readonly IRadioControl _radio;
    private readonly double? _busyAboveDbm;
    private readonly TimeSpan _pollInterval;
    private readonly bool _dcdUsable;
    private readonly bool _rssiUsable;
    private volatile bool _rssiBusy;
    private volatile bool _rssiKnown;
    private int _consecutiveFailures;

    /// <summary>Wraps a radio somebody else owns.</summary>
    /// <param name="radio">An open radio. Not disposed by this class.</param>
    /// <param name="busyAboveDbm">Call the channel busy above this RSSI, or null for DCD only.</param>
    /// <param name="pollInterval">How often to read RSSI. Ignored without a threshold.</param>
    public RadioBusySource(
        IRadioControl radio, double? busyAboveDbm = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(radio);
        _radio = radio;
        _busyAboveDbm = busyAboveDbm;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        RadioCapabilities can = radio.Capabilities;
        _dcdUsable = can.HasFlag(RadioCapabilities.CarrierSense);
        _rssiUsable = busyAboveDbm is not null && can.HasFlag(RadioCapabilities.RssiRead);

        if (busyAboveDbm is not null && !can.HasFlag(RadioCapabilities.RssiRead))
        {
            Console.Error.WriteLine(
                "ofdm-fm: carrier sense: an RSSI threshold was configured but this radio does not "
                + "report RSSI. Using DCD alone.");
        }

        if (!_dcdUsable && !_rssiUsable)
        {
            Console.Error.WriteLine(
                "ofdm-fm: carrier sense: this radio offers neither carrier sense nor a usable RSSI "
                + "threshold, so it will report no opinion and the station transmits as it did "
                + "before. Check the radio's programming and that unsolicited reporting is on.");
        }
    }

    /// <inheritdoc/>
    public bool? Busy => Combine(
        _dcdUsable ? _radio.ChannelBusy : null,
        _rssiKnown ? _rssiBusy : null);

    /// <summary>The last RSSI reading in dBm, for diagnostics. Null if not polling or not known.</summary>
    public double? LastRssiDbm { get; private set; }

    /// <summary>Whether anything at all will be polled. False leaves the loop unstarted.</summary>
    internal bool PollsRssi => _rssiUsable;

    /// <summary>
    /// How the two mechanisms combine. Busy if either says so; null only when NEITHER knows,
    /// because a mechanism that is switched off or faulted must not be able to vote "clear" and so
    /// overrule the one that is working.
    /// </summary>
    internal static bool? Combine(bool? dcd, bool? rssiAboveThreshold) =>
        (dcd, rssiAboveThreshold) switch
        {
            (null, null) => null,
            (true, _) or (_, true) => true,
            _ => false,
        };

    /// <summary>Starts RSSI polling, if there is any to do. DCD needs nothing started.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _rssiUsable ? PollLoopAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// One RSSI read and the state update behind it. Separated from the loop so that the deciding
    /// can be tested without a clock: no test here may wait on wall time.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            float dbm = await _radio.ReadRssiDbmAsync(cancellationToken).ConfigureAwait(false);
            LastRssiDbm = dbm;
            _rssiBusy = dbm > _busyAboveDbm!.Value;
            _rssiKnown = true;
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (++_consecutiveFailures == FailuresBeforeUnknown)
            {
                _rssiKnown = false;
                LastRssiDbm = null;
                Console.Error.WriteLine(
                    $"ofdm-fm: carrier sense: RSSI reads failing ({e.Message}). Reporting no "
                    + "opinion until they come back.");
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
