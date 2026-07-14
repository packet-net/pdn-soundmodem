namespace Packet.SoundModem.Modems;

/// <summary>
/// In-band energy "channel busy" detector: compares short-term band-limited power against
/// a slowly-adapting noise-floor estimate, with assert/release hysteresis and a hold time.
/// This is the deliberately display-decoupled replacement for QtSoundModem's spectral busy
/// detector (which lives in its waterfall paint path and never runs headless). It flags
/// non-packet energy — a carrier, voice, another mode — that the packet DCD cannot see;
/// channel busy for carrier-sense purposes is the OR of both.
/// </summary>
public sealed class EnergyBusyDetector
{
    private readonly int _blockSize;
    private readonly float _assertRatio;
    private readonly float _releaseRatio;
    private readonly int _holdBlocks;

    private double _accumulator;
    private int _accumulated;
    private double _noiseFloor;
    private int _seedBlocksRemaining = 8;
    private int _hold;
    private bool _busy;

    /// <summary>Creates a detector fed with band-pass-filtered samples.</summary>
    /// <param name="sampleRate">Sample rate of the fed signal.</param>
    /// <param name="blockMilliseconds">Power integration block. Sets detection latency.</param>
    /// <param name="assertDb">dB above the noise floor at which busy asserts.</param>
    /// <param name="releaseDb">dB above the floor below which busy releases (must be
    /// below <paramref name="assertDb"/> — hysteresis).</param>
    /// <param name="holdMilliseconds">Minimum busy time after the last over-threshold
    /// block, riding through flutter and PSK envelope dips.</param>
    public EnergyBusyDetector(
        int sampleRate,
        int blockMilliseconds = 20,
        double assertDb = 6,
        double releaseDb = 3,
        int holdMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockMilliseconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(assertDb, releaseDb);
        _blockSize = sampleRate * blockMilliseconds / 1000;
        _assertRatio = (float)Math.Pow(10, assertDb / 10);
        _releaseRatio = (float)Math.Pow(10, releaseDb / 10);
        _holdBlocks = Math.Max(1, holdMilliseconds / blockMilliseconds);
    }

    /// <summary>True while in-band energy is significantly above the noise floor.</summary>
    public bool Busy => _busy;

    /// <summary>Feeds one band-pass-filtered sample.</summary>
    public void Process(float bandLimitedSample)
    {
        _accumulator += (double)bandLimitedSample * bandLimitedSample;
        if (++_accumulated < _blockSize)
        {
            return;
        }

        double blockPower = _accumulator / _blockSize;
        _accumulator = 0;
        _accumulated = 0;

        if (_seedBlocksRemaining > 0)
        {
            // Warm-up: the front-end FIR starts from zeroed history, so the first blocks
            // are artificially quiet — seeding the floor from them would flag plain noise
            // as busy forever. Seed with the loudest warm-up block (conservative: a busy
            // cold start seeds high, and the fast downward adaptation recovers the true
            // floor as soon as the channel goes quiet).
            _seedBlocksRemaining--;
            _noiseFloor = Math.Max(_noiseFloor, Math.Max(blockPower, 1e-12));
            return;
        }

        // The floor tracks downward quickly and upward very slowly, so it follows the
        // quiet channel but is not dragged up by transmissions sitting on frequency.
        _noiseFloor += (blockPower - _noiseFloor) * (blockPower < _noiseFloor ? 0.2 : 0.002);
        _noiseFloor = Math.Max(_noiseFloor, 1e-12);

        double ratio = blockPower / _noiseFloor;
        if (ratio >= _assertRatio)
        {
            _busy = true;
            _hold = _holdBlocks;
        }
        else if (ratio < _releaseRatio && _busy && --_hold <= 0)
        {
            _busy = false;
        }
    }

    /// <summary>Resets all state (e.g. after the channel's own transmission).</summary>
    public void Reset()
    {
        _accumulator = 0;
        _accumulated = 0;
        _hold = 0;
        _busy = false;
        _noiseFloor = 0;
        _seedBlocksRemaining = 8;
    }
}
