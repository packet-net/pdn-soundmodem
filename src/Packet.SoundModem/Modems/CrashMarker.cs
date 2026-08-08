namespace Packet.SoundModem.Modems;

/// <summary>
/// Marks the samples a static crash (QRN impulse) is hitting, so the demodulator can
/// crush the confidence of the symbols underneath and the Reed-Solomon erasure ladder
/// (PR #240) can treat them as located damage - rx-roadmap workstream 6 leg 2. The
/// blanker experiment (PR #254) measured why clipping cannot help at 300 Bd: a 5-7
/// symbol crash is a short jam, and bounding its energy does not restore the symbols
/// beneath. But the same experiment proved crash spans are reliably DETECTABLE, and an
/// erasure is worth twice an error to the RS decoder - so the span becomes information
/// instead of a repair attempt.
/// </summary>
/// <remarks>
/// Detection is an in-band envelope ratio on the demodulator's own band-passed samples:
/// a fast envelope (~1 ms) against a slow reference (~1 s). The measured record says a
/// crash's envelope sits 15.6 dB (the detection floor) and typically ~18 dB over the
/// broadband noise sigma, and that ratio survives band-passing (leg 1's broadband-shaping
/// argument), while the wanted signal at the Reed-Solomon threshold sits only a few dB
/// over the in-band floor - so a crash stands roughly 10 dB proud of the reference where
/// legitimate RRC envelope peaks reach ~3 dB. The reference keeps adapting during a
/// crash at 1/16 rate rather than freezing, so a mis-entered mark always self-heals and
/// a signal onset misread as a crash cannot latch.
/// </remarks>
public sealed class CrashMarker
{
    /// <summary>Fast-envelope time constant, seconds - inside a single symbol at 300 Bd,
    /// so the mark follows the crash edge, not the symbol rhythm.</summary>
    private const double FastSeconds = 0.001;

    /// <summary>Reference-envelope time constant, seconds - long against any crash train
    /// (p99 ~1.5 s), short against band-condition drift.</summary>
    private const double SlowSeconds = 1.0;

    /// <summary>Amplitude ratio at which a crash mark asserts. 3.2x is ~10 dB over the
    /// reference: above legitimate RRC envelope peaks (~3 dB) with margin, below the
    /// measured crash floor's ~12 dB in-band prominence at the RS-threshold operating
    /// points - measured on the sim ladder against 2.5 and 4 (docs/qpsk-style probe in
    /// the leg 2 PR).</summary>
    private const double EnterRatio = 3.2;

    /// <summary>Release ratio (hysteresis partner of <see cref="EnterRatio"/>).</summary>
    private const double ExitRatio = 2.0;

    /// <summary>Samples the mark holds after the envelope drops below
    /// <see cref="ExitRatio"/> - one symbol at 300 Bd / 12 kHz, covering the matched
    /// filter smearing the crash tail into the next decision.</summary>
    private readonly int _holdSamples;

    /// <summary>No marks until the reference has been fed this long from cold - a
    /// near-zero reference makes ANY onset an apparent 30 dB excursion, and a mark fired
    /// on the first signal a fresh demodulator ever hears is a statement about the
    /// reference, not the band.</summary>
    private readonly int _warmupSamples;

    /// <summary>Hard bound on one continuous mark. The measured record puts even crash
    /// trains at p99 ~1.5 s; a "crash" outlasting this is a level change (a stronger
    /// station, a band shift), so the reference snaps to the new level and the mark
    /// clears - the self-heal that keeps a false entry from ever latching.</summary>
    private readonly int _maxActiveSamples;

    private readonly double _fastRate;
    private readonly double _slowRate;
    private double _fast;
    private double _slow;
    private bool _active;
    private int _hold;
    private int _fed;
    private int _activeRun;

    /// <summary>Creates the marker for a stream at <paramref name="sampleRate"/> with
    /// symbols of <paramref name="samplesPerSymbol"/> samples.</summary>
    public CrashMarker(int sampleRate, int samplesPerSymbol)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(samplesPerSymbol, 0);
        _fastRate = 1.0 / (sampleRate * FastSeconds);
        _slowRate = 1.0 / (sampleRate * SlowSeconds);
        _holdSamples = samplesPerSymbol;
        _warmupSamples = sampleRate;      // one SlowSeconds of feed
        _maxActiveSamples = sampleRate;   // 1 s, past any measured single crash
    }

    /// <summary>True while a crash is being marked.</summary>
    public bool Active => _active || _hold > 0;

    /// <summary>Feeds one band-passed sample's magnitude; returns <see cref="Active"/>.</summary>
    public bool Update(float magnitude)
    {
        _fast += _fastRate * (magnitude - _fast);
        // The reference follows the band at 1 s normally and at 16 s while a crash is
        // marked: slow enough that the crash cannot lift its own threshold, fast enough
        // that a false mark on a legitimate level change always heals.
        double slowRate = _active ? _slowRate / 16 : _slowRate;
        _slow += slowRate * (magnitude - _slow);

        if (_fed < _warmupSamples)
        {
            _fed++;
            return false;
        }

        double reference = Math.Max(_slow, 1e-9);
        if (_active)
        {
            _activeRun++;
            if (_fast < ExitRatio * reference)
            {
                _active = false;
                _hold = _holdSamples;
            }
            else if (_activeRun > _maxActiveSamples)
            {
                // Not a crash after a full second: a level change. Snap and heal.
                _slow = _fast;
                _active = false;
                _hold = 0;
            }
        }
        else if (_fast > EnterRatio * reference)
        {
            _active = true;
            _activeRun = 0;
            _hold = 0;
        }

        if (!_active && _hold > 0)
        {
            _hold--;
            return true;
        }

        return _active;
    }

    /// <summary>Clears all state (see <see cref="BpskDemodulator.ResetCarrierState"/>).</summary>
    public void Reset()
    {
        _fast = 0;
        _slow = 0;
        _active = false;
        _hold = 0;
        _fed = 0;
        _activeRun = 0;
    }
}
