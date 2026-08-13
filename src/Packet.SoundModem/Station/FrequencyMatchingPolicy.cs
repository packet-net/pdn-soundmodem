namespace Packet.SoundModem.Station;

/// <summary>Knobs for <see cref="FrequencyMatchingPolicy"/>.</summary>
public sealed record FrequencyMatchingOptions
{
    /// <summary>Frames required before the estimate is acted on. Default 3.</summary>
    public int MinSamples { get; init; } = 3;

    /// <summary>Largest spread across those frames, in Hz, that still counts as settled. Default 20.</summary>
    public double MaxSpreadHz { get; init; } = 20;

    /// <summary>Largest shift ever applied, in Hz. Default 60.</summary>
    public double MaxTrimHz { get; init; } = 60;

    /// <summary>Fraction of the measured offset applied, 0 to 1. Default 0.5.</summary>
    public double Damping { get; init; } = 0.5;

    /// <summary>
    /// How far a station's measured offset may move, in Hz, after we start correcting for it
    /// before we conclude it is correcting for us and stop. Default 10.
    /// </summary>
    /// <remarks>
    /// Sized above ordinary frame-to-frame jitter and below any correction worth making, so an
    /// unsettled rig does not trip it and a genuine chase does.
    /// </remarks>
    public double ChaseThresholdHz { get; init; } = 10;

    /// <summary>Trim below which we are not really correcting, in Hz. Default 2.</summary>
    /// <remarks>
    /// A trim of a fraction of a Hz cannot provoke a measurable response, so movement while one
    /// is applied is somebody else's drift rather than evidence of a chase. Without this floor
    /// the detector would blame itself for every wandering rig on the band.
    /// </remarks>
    public double MinMeaningfulTrimHz { get; init; } = 2;
}

/// <summary>Why a station is no longer being corrected for.</summary>
/// <param name="Callsign">The station.</param>
/// <param name="Detail">A human-readable reason, suitable for a log line.</param>
public readonly record struct FrequencyMatchingStandDown(string Callsign, string Detail);

/// <summary>
/// Decides how far to shift a transmission so it lands where the far station's receiver is
/// listening, and stops when the far station turns out to be doing the same thing back.
/// </summary>
/// <remarks>
/// <para><b>The chase.</b> If both ends correct, neither converges on the right answer. Working
/// it through for a true oscillator difference D, with each end applying a fraction k of what it
/// measures, the pair settles at kD/(1+k) and -kD/(1+k): with k = 0.5 and D = 5 Hz, both stations
/// end up transmitting 1.7 Hz off and each still hears the other 3.3 Hz out - worse than if only
/// one of them had corrected, and the more damping, the further short they both stop. Undamped
/// (k = 1) it does not settle at all: each end applies the full correction, sees the offset
/// vanish, withdraws it, and sees it return, oscillating with a period of two exchanges.</para>
/// <para><b>Detecting it.</b> Our transmit trim cannot change what we measure of them: we measure
/// their emissions, and our transmitter is not in that path. So a station's measured offset
/// should sit still while we correct for it, however much we correct. If it moves once we start,
/// the movement is theirs - they are correcting for us, or their rig is drifting - and in either
/// case chasing it is the wrong response. So the correction latches off for that station, and
/// says so.</para>
/// <para>Standing down is deliberate and permanent for the session, in the same spirit as a
/// station that has lost a contested slice: one side correcting is the outcome worth having, and
/// a clear stop with a reason beats two stations quietly making each other worse.</para>
/// </remarks>
public sealed class FrequencyMatchingPolicy
{
    private sealed class State
    {
        public double BaselineHz;
        public bool Correcting;
        public double AppliedHz;
        public bool StoodDown;
    }

    private readonly StationFrequencyOffsets _offsets;
    private readonly FrequencyMatchingOptions _options;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates a policy over <paramref name="offsets"/>.</summary>
    public FrequencyMatchingPolicy(
        StationFrequencyOffsets offsets, FrequencyMatchingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        _offsets = offsets;
        _options = options ?? new FrequencyMatchingOptions();
    }

    /// <summary>Raised once per station, when the correction for it latches off.</summary>
    public event Action<FrequencyMatchingStandDown>? StoodDown;

    /// <summary>Whether <paramref name="callsign"/> has been given up on.</summary>
    public bool HasStoodDown(string callsign)
    {
        lock (_gate)
        {
            return _states.TryGetValue(callsign, out State? s) && s.StoodDown;
        }
    }

    /// <summary>
    /// The shift to apply, in Hz, when transmitting to <paramref name="destination"/>; zero when
    /// there is no settled estimate, or the station has been stood down.
    /// </summary>
    public double TrimFor(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return 0;
        }

        if (!_offsets.TryGet(destination, out StationOffset? found) || found is not StationOffset o)
        {
            return 0;
        }

        string? standDownReason = null;
        double trim;
        lock (_gate)
        {
            if (!_states.TryGetValue(destination, out State? state))
            {
                state = new State();
                _states[destination] = state;
            }

            if (state.StoodDown)
            {
                return 0;
            }

            // Was our own correction answered? Compare against where they sat when we began.
            if (state.Correcting
                && Math.Abs(state.AppliedHz) >= _options.MinMeaningfulTrimHz)
            {
                double moved = o.OffsetHz - state.BaselineHz;
                if (Math.Abs(moved) > _options.ChaseThresholdHz)
                {
                    double wasApplying = state.AppliedHz;
                    state.StoodDown = true;
                    state.Correcting = false;
                    state.AppliedHz = 0;
                    standDownReason =
                        $"was heard at {state.BaselineHz:+0.0;-0.0} Hz, we began answering "
                        + $"{wasApplying:+0.0;-0.0} Hz off to suit it, and it has since moved "
                        + $"to {o.OffsetHz:+0.0;-0.0} Hz. Our transmitter cannot change what we "
                        + "measure of theirs, so that movement is theirs: either they are "
                        + "correcting for us in turn, which converges on both of us being wrong, "
                        + "or their reference is drifting. Not correcting for this station again.";
                }
            }

            if (standDownReason is null)
            {
                if (o.Samples < _options.MinSamples || o.SpreadHz > _options.MaxSpreadHz)
                {
                    return 0;
                }

                trim = Math.Clamp(
                    o.OffsetHz * _options.Damping, -_options.MaxTrimHz, _options.MaxTrimHz);

                if (!state.Correcting && Math.Abs(trim) >= _options.MinMeaningfulTrimHz)
                {
                    // The moment we start correcting, remember where they were. Everything the
                    // chase detector knows is measured from here.
                    state.Correcting = true;
                    state.BaselineHz = o.OffsetHz;
                }

                state.AppliedHz = trim;
            }
            else
            {
                trim = 0;
            }
        }

        if (standDownReason is not null)
        {
            StoodDown?.Invoke(new FrequencyMatchingStandDown(destination, standDownReason));
        }

        return trim;
    }
}
