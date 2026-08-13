namespace Packet.SoundModem.Station;

/// <summary>Knobs for <see cref="FrequencyMatchingPolicy"/>.</summary>
public sealed record FrequencyMatchingOptions
{
    /// <summary>Frames required before the estimate is acted on. Default 3.</summary>
    public int MinSamples { get; init; } = 3;

    /// <summary>Largest spread across those frames, in Hz, that still counts as settled. Default 20.</summary>
    public double MaxSpreadHz { get; init; } = 20;

    /// <summary>Largest shift ever applied, in Hz. Default 50.</summary>
    public double MaxTrimHz { get; init; } = 50;

    /// <summary>
    /// Fraction of the measured offset applied to a station that has already moved under our
    /// correction once. Default 0.5. A station that has never moved gets the whole thing.
    /// </summary>
    /// <remarks>
    /// <para><b>Conditional, because damping is only ever a fix for a feedback loop, and in the
    /// normal case there is no loop.</b> Our transmit trim cannot change what we measure of them,
    /// so correcting for a station that is not itself correcting is open-loop: damping there does
    /// not stabilise anything, it just leaves half the error uncorrected for nothing. Measurement
    /// noise is already handled, and better, by averaging the window and gating on its spread;
    /// a wild estimate is already bounded by <see cref="MaxTrimHz"/>.</para>
    /// <para>Where it does earn its keep is a two-sided chase too small to trip
    /// <see cref="ChaseThresholdHz"/>: at a true difference of 5 Hz two undamped stations
    /// alternate between perfectly aligned and 5 Hz apart every exchange, and a steady small
    /// offset is easier on a demodulator than one that jumps. So it is applied where there is
    /// evidence of a peer that reacts - a station that has moved under our correction at least
    /// once - and nowhere else.</para>
    /// </remarks>
    public double Damping { get; init; } = 0.5;

    /// <summary>
    /// How far a station's measured offset may move, in Hz, after we start correcting for it
    /// before we back off. Default 10.
    /// </summary>
    public double ChaseThresholdHz { get; init; } = 10;

    /// <summary>Trim below which we are not really correcting, in Hz. Default 2.</summary>
    public double MinMeaningfulTrimHz { get; init; } = 2;

    /// <summary>
    /// How long to leave a station alone after its frequency moved under our correction.
    /// Default 30 minutes.
    /// </summary>
    /// <remarks>
    /// Long enough that a station correcting for us in turn cannot trade adjustments with us at
    /// any rate worth worrying about, and short enough that a rig which simply moved - a knocked
    /// dial, a warm-up drift - is picked up again the same day rather than being written off.
    /// </remarks>
    public TimeSpan ChaseCooldown { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many times a station may move under our correction before we stop trying. Default 3.
    /// </summary>
    /// <remarks>
    /// One move is a rig that moved: it settles at its new offset and we correct for that instead.
    /// Moving again every time we correct is not a coincidence, it is the far end running this
    /// same algorithm, and two of those trade adjustments forever without either landing on the
    /// right answer. Set 0 to retry indefinitely.
    /// </remarks>
    public int MaxChases { get; init; } = 3;
}

/// <summary>Why a station is not being corrected for at the moment.</summary>
/// <param name="Callsign">The station.</param>
/// <param name="Detail">A human-readable reason, suitable for a log line.</param>
/// <param name="Chases">How many times this station has now moved under our correction.</param>
/// <param name="RetryAfter">When it will be tried again; null when it will not be.</param>
public readonly record struct FrequencyMatchingStandDown(
    string Callsign, string Detail, int Chases, TimeSpan? RetryAfter);

/// <summary>
/// Decides how far to shift a transmission so it lands where the far station's receiver is
/// listening, and backs off when the far station turns out to be moving in response.
/// </summary>
/// <remarks>
/// <para><b>The chase.</b> If both ends correct, neither converges on the right answer. For a
/// true oscillator difference D, with each end applying a fraction k of what it measures, the
/// pair settles at kD/(1+k) and -kD/(1+k): with k = 0.5 and D = 5 Hz, both stations end up
/// transmitting 1.7 Hz off and each still hears the other 3.3 Hz out - worse than if only one
/// had corrected. Undamped (k = 1) it does not settle at all, oscillating with a period of two
/// exchanges.</para>
/// <para><b>Detecting it.</b> Our transmit trim cannot change what we measure of them: we measure
/// their emissions, and our transmitter is not in that path. So a station's measured offset
/// should sit still while we correct for it, however much we correct. If it moves, the movement
/// is theirs.</para>
/// <para><b>Why that is not immediately fatal.</b> A station that moves once has probably just
/// moved - a knocked dial, a rig warming up - and will sit at its new offset perfectly happily.
/// Writing it off forever would mean never correcting for it again because of something it did
/// on a Tuesday. So a move costs a cooldown, after which the new offset is measured and
/// corrected for like any other. What separates that from a real chase is repetition: a rig that
/// moved stays put afterwards, while a peer running this same algorithm moves again every time
/// we correct. Only after <see cref="FrequencyMatchingOptions.MaxChases"/> of those does the
/// correction stop for good.</para>
/// <para><b>And the cap.</b> None of the above is what bounds the damage. Every trim is clamped
/// to <see cref="FrequencyMatchingOptions.MaxTrimHz"/>, so even two stations chasing each other
/// with the detector disabled entirely cannot walk more than that far off the channel.</para>
/// </remarks>
public sealed class FrequencyMatchingPolicy
{
    private sealed class State
    {
        public double BaselineHz;
        public bool Correcting;
        public double AppliedHz;
        public int Chases;
        public bool Retired;
        public long ResumeAtTicks;
    }

    private readonly StationFrequencyOffsets _offsets;
    private readonly FrequencyMatchingOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates a policy over <paramref name="offsets"/>.</summary>
    public FrequencyMatchingPolicy(
        StationFrequencyOffsets offsets,
        FrequencyMatchingOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        _offsets = offsets;
        _options = options ?? new FrequencyMatchingOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised each time the correction for a station backs off, temporarily or for good.</summary>
    public event Action<FrequencyMatchingStandDown>? StoodDown;

    /// <summary>Whether <paramref name="callsign"/> has been given up on for good.</summary>
    public bool HasRetired(string callsign)
    {
        lock (_gate)
        {
            return _states.TryGetValue(callsign, out State? s) && s.Retired;
        }
    }

    /// <summary>Whether <paramref name="callsign"/> is inside a cooldown after moving.</summary>
    public bool IsCoolingDown(string callsign)
    {
        lock (_gate)
        {
            return _states.TryGetValue(callsign, out State? s)
                && !s.Retired
                && s.ResumeAtTicks > _time.GetUtcNow().UtcTicks;
        }
    }

    /// <summary>
    /// The shift to apply, in Hz, when transmitting to <paramref name="destination"/>; zero when
    /// there is no settled estimate, or the station is cooling down, or it has been retired.
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

        FrequencyMatchingStandDown? announce = null;
        double trim = 0;
        long now = _time.GetUtcNow().UtcTicks;

        lock (_gate)
        {
            if (!_states.TryGetValue(destination, out State? state))
            {
                state = new State();
                _states[destination] = state;
            }

            if (state.Retired)
            {
                return 0;
            }

            if (state.ResumeAtTicks > now)
            {
                return 0;
            }

            // Did our own correction get answered? Measured from where they sat when we began.
            if (state.Correcting && Math.Abs(state.AppliedHz) >= _options.MinMeaningfulTrimHz)
            {
                double moved = o.OffsetHz - state.BaselineHz;
                if (Math.Abs(moved) > _options.ChaseThresholdHz)
                {
                    double wasApplying = state.AppliedHz;
                    state.Chases++;
                    state.Correcting = false;
                    state.AppliedHz = 0;

                    bool retire = _options.MaxChases > 0 && state.Chases >= _options.MaxChases;
                    state.Retired = retire;
                    state.ResumeAtTicks = retire ? 0 : now + _options.ChaseCooldown.Ticks;

                    string what =
                        $"was heard at {state.BaselineHz:+0.0;-0.0} Hz, we began answering "
                        + $"{wasApplying:+0.0;-0.0} Hz off to suit it, and it has since moved to "
                        + $"{o.OffsetHz:+0.0;-0.0} Hz. Our transmitter cannot change what we "
                        + "measure of theirs, so that movement is theirs.";

                    announce = new FrequencyMatchingStandDown(
                        destination,
                        retire
                            ? what + $" That is {state.Chases} times now, which is not a rig that "
                                + "moved but a station correcting for us in turn; neither of us "
                                + "lands on the right answer that way. Not correcting for it again."
                            : what + " Leaving it alone for "
                                + $"{_options.ChaseCooldown.TotalMinutes:0} minutes, then measuring "
                                + "it afresh - a rig that has simply been moved will sit at its new "
                                + "offset and can be corrected for there.",
                        state.Chases,
                        retire ? null : _options.ChaseCooldown);
                }
            }

            if (announce is null)
            {
                if (o.Samples < _options.MinSamples || o.SpreadHz > _options.MaxSpreadHz)
                {
                    return 0;
                }

                // Full correction until a station gives us a reason to hold back. Damping is a
                // remedy for a loop, and there is no loop unless the far end is correcting too;
                // having moved once under our correction is the only evidence of that we get.
                double gain = state.Chases > 0 ? _options.Damping : 1.0;
                trim = Math.Clamp(
                    o.OffsetHz * gain, -_options.MaxTrimHz, _options.MaxTrimHz);

                if (!state.Correcting && Math.Abs(trim) >= _options.MinMeaningfulTrimHz)
                {
                    // The moment we start correcting, remember where they were. Everything the
                    // chase detector knows is measured from here.
                    state.Correcting = true;
                    state.BaselineHz = o.OffsetHz;
                }

                state.AppliedHz = trim;
            }
        }

        if (announce is FrequencyMatchingStandDown stand)
        {
            StoodDown?.Invoke(stand);
        }

        return trim;
    }
}
