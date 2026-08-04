using M0LTE.Flex;

namespace Packet.SoundModem.Ota;

/// <summary>
/// The per-burst SWR interlock and meter recorder, shared by both transmit routes so the
/// trust rules cannot quietly diverge between them: subscribe for the duration of one keyed
/// transmission, and it collects every meter sample, tracks peak forward power, and - only
/// when the burst's RF envelope is constant - derives SWR and raises
/// <see cref="AbortReason"/> the moment it exceeds the limit.
/// </summary>
/// <remarks>
/// <para>The trust rules, both learned live on M0LTE's 6500:</para>
/// <list type="bullet">
/// <item><b>SWR is only meaningful on a constant RF envelope.</b> Forward and reflected power
/// are separate meter samples taken at slightly different instants, so if the envelope is
/// moving between them their ratio describes two different moments and is nonsense. A
/// two-tone burst reported 1.93 where the constant-envelope pre-flight tone moments earlier
/// read 1.30 into the same load; a data waveform, with real PAPR, would be worse. So the
/// pre-flight carrier is the SWR check and modulated bursts do not attempt one.</item>
/// <item><b>Only believe SWR at full output.</b> During the key-up/key-down ramps the same
/// two-instants problem applies and the ratio reads high - a load measuring a steady 1.31
/// reported 1.56 purely because a ramp was included. Requiring the sample to sit within 3 dB
/// of the burst's own peak forward power confines the measurement to the steady state.</item>
/// </list>
/// </remarks>
internal sealed class SwrInterlock : IDisposable
{
    private readonly FlexMeters _meters;
    private readonly double _maxSwr;
    private readonly double? _maxForwardWatts;
    private readonly bool _constantEnvelope;
    private readonly List<FlexMeterReading> _collected = [];
    private readonly Lock _gate = new();

    private double? _peakSwr;
    private double? _peakFwd;
    private string? _abortReason;

    /// <summary>Arms the interlock: subscribes to <paramref name="meters"/> until disposed.</summary>
    /// <param name="meters">The session's meter telemetry.</param>
    /// <param name="maxSwr">Abort threshold.</param>
    /// <param name="constantEnvelope">Whether the burst holds a constant RF envelope - the
    /// precondition for the radio's forward/reflected samples to be comparable at all. False
    /// collects meters and peak forward power but never evaluates SWR.</param>
    /// <param name="maxForwardWatts">Hard forward-power ceiling in watts, or null for none. Unlike
    /// SWR this needs no constant envelope - power is power - so it is checked on every FWDPWR
    /// sample regardless, and abort fires the instant measured forward power exceeds it. This is
    /// the enforcement behind a strict transmit-power limit (e.g. the 5 W ceiling on the
    /// attenuated RSP1 rig): the radio's <c>rfpower</c> is a 0-100 level, not watts, so the
    /// measured power is what the limit is held against, not the commanded setting.</param>
    public SwrInterlock(
        FlexMeters meters, double maxSwr, bool constantEnvelope, double? maxForwardWatts = null)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _meters = meters;
        _maxSwr = maxSwr;
        _maxForwardWatts = maxForwardWatts;
        _constantEnvelope = constantEnvelope;
        _meters.Updated += OnMeter;
    }

    /// <summary>Why the transmission should stop, or null while it is healthy. Polled between
    /// write chunks so the interlock can cut a burst short.</summary>
    public string? AbortReason
    {
        get
        {
            lock (_gate)
            {
                return _abortReason;
            }
        }
    }

    /// <summary>Highest derived SWR seen in the steady state, when it could be trusted.</summary>
    public double? PeakSwr
    {
        get
        {
            lock (_gate)
            {
                return _peakSwr;
            }
        }
    }

    /// <summary>Highest forward power seen while armed, in dBm.</summary>
    public double? PeakForwardDbm
    {
        get
        {
            lock (_gate)
            {
                return _peakFwd;
            }
        }
    }

    /// <summary>Every meter sample seen while armed, for the transmit report.</summary>
    public IReadOnlyList<FlexMeterReading> Collected
    {
        get
        {
            lock (_gate)
            {
                return [.. _collected];
            }
        }
    }

    private void OnMeter(FlexMeterReading reading)
    {
        lock (_gate)
        {
            _collected.Add(reading);
            if (reading.Descriptor.Name.Equals("FWDPWR", StringComparison.OrdinalIgnoreCase))
            {
                if (_peakFwd is null || reading.Value > _peakFwd)
                {
                    _peakFwd = reading.Value;
                }

                // The forward-power ceiling is enforced in measured watts and independently of the
                // SWR path (no constant-envelope precondition - a modulated data burst still must
                // not exceed the limit). Abort the instant a sample is over.
                if (_maxForwardWatts is double maxW
                    && FlexMeters.DbmToWatts(reading.Value) > maxW
                    && _abortReason is null)
                {
                    _abortReason =
                        $"forward power {FlexMeters.DbmToWatts(reading.Value):F1} W exceeded the " +
                        $"{maxW:F1} W limit";
                }
            }
        }

        // Derived SWR (forward/reflected, both dBm) is the trustworthy one - see
        // FlexMeters.SwrFromPowers. Null simply means "not transmitting hard enough to
        // measure", which is not a fault.
        if (!_constantEnvelope)
        {
            return;
        }

        double? swr = _meters.SwrFromPowers();
        if (swr is null)
        {
            return;
        }

        // The steady-state gate - see the class remarks.
        if (!_meters.TryGet("FWDPWR", out FlexMeterReading fwdNow))
        {
            return;
        }

        lock (_gate)
        {
            if (_peakFwd is null || fwdNow.Value < _peakFwd - 3.0)
            {
                return;
            }

            if (_peakSwr is null || swr > _peakSwr)
            {
                _peakSwr = swr;
            }

            if (swr > _maxSwr && _abortReason is null)
            {
                _abortReason = $"SWR {swr:F2} exceeded the {_maxSwr:F2} limit";
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meters.Updated -= OnMeter;
}
