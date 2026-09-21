using Packet.Radio.Tait;

namespace Packet.SoundModem.CarrierSense;

/// <summary>
/// The standalone path to radio carrier sense: opens the station's own Tait on its serial port and
/// reads DCD and RSSI from it. See <see cref="RadioBusySource"/> for why the radio is asked at all
/// rather than the audio.
/// </summary>
/// <remarks>
/// <para><b>This is for a station running pdn-soundmodem on its own.</b> Running in-process under
/// packet.net the host owns the serial link, and a second opener would either fail or fight it for
/// the port. That case does not come through here at all: the host registers its own already-open
/// radio with <see cref="ChannelBusySources.Host"/> and this class is never constructed. The
/// deciding is common to both and lives in <see cref="RadioBusySource"/>.</para>
/// <para><b>It fails open.</b> A port that will not open, a radio that will not answer, a cable
/// pulled mid-session: all of them end with a source that reports no opinion, and a station that
/// transmits exactly as it did before this feature existed. A carrier sense that can silence a
/// station by losing a USB cable would be the worse failure by a distance.</para>
/// </remarks>
public sealed class TaitCarrierSense : IChannelBusySource, IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, TaitCarrierSense> Shared = [];

    private readonly TaitCcdiRadio? _radio;
    private readonly RadioBusySource? _source;
    private readonly CancellationTokenSource? _stopping;

    private TaitCarrierSense(StationRadio config)
    {
        try
        {
            _radio = TaitCcdiRadio.Open(config.TaitPort!, config.TaitBaud);
        }
        catch (Exception e)
        {
            // EVERY exception, deliberately, and this is the most important catch in the feature.
            // A modem is constructed during daemon start-up, so anything thrown here does not
            // cost carrier sense, it costs the station its whole service. The list of things that
            // can throw is longer than it looks: a port that does not exist, one another process
            // holds, one the service user lacks dialout for, a radio in the wrong mode, and
            // DllNotFoundException from the native serial library if the runtime cannot
            // resolve runtimes/linux-arm64/native out of the application's own directory.
            //
            // A station that cannot reach its radio still has to work. Say so once, then answer
            // null for ever after, which leaves the station where it was without this.
            Console.Error.WriteLine(
                $"ofdm-fm: carrier sense: cannot open {config.TaitPort}: {e.Message}. "
                + "Running without radio carrier sense.");
            return;
        }

        _stopping = new CancellationTokenSource();
        _source = new RadioBusySource(
            _radio, config.BusyAboveDbm, TimeSpan.FromMilliseconds(config.PollMilliseconds));

        Console.Error.WriteLine(
            $"ofdm-fm: carrier sense from the radio on {config.TaitPort} at {config.TaitBaud} baud"
            + (config.BusyAboveDbm is { } dbm
                ? $", DCD plus RSSI above {dbm:F0} dBm every {config.PollMilliseconds} ms"
                : ", DCD only"));

        // Both off the constructor's thread: a modem is built during daemon start-up and must not
        // wait on a serial conversation to do it.
        _ = EnableDcdAsync(_stopping.Token);
        _ = _source.StartAsync(_stopping.Token);
    }

    /// <summary>
    /// The one source for a station, or null when nothing is configured and no port should be
    /// opened.
    /// </summary>
    /// <remarks>Shared per port because a station may run several sub-channels, they all listen to
    /// one radio, and a serial port cannot be opened twice.</remarks>
    public static TaitCarrierSense? ForStation(StationRadio? config)
    {
        if (config is null || !config.WantsCarrierSense)
        {
            return null;
        }

        lock (Gate)
        {
            if (Shared.TryGetValue(config.TaitPort!, out TaitCarrierSense? existing))
            {
                return existing;
            }

            var made = new TaitCarrierSense(config);
            Shared[config.TaitPort!] = made;
            return made;
        }
    }

    /// <inheritdoc/>
    public bool? Busy => _source?.Busy;

    /// <summary>The last RSSI reading in dBm, for diagnostics.</summary>
    public double? LastRssiDbm => _source?.LastRssiDbm;

    private async Task EnableDcdAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The driver opens the port without touching the radio, so the unsolicited PROGRESS
            // messages that DCD is built from have to be asked for. This is per-session radio
            // state: it does not survive the radio being power cycled.
            await _radio!.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(
                $"ofdm-fm: carrier sense: the radio would not enable PROGRESS messages "
                + $"({e.Message}). DCD will not report; an RSSI threshold still would.");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stopping?.Cancel();
        _stopping?.Dispose();
        if (_radio is not null)
        {
            _radio.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
