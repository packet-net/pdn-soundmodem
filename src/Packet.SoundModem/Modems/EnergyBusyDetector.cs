namespace Packet.SoundModem.Modems;

/// <summary>
/// In-band energy "channel busy" detector: compares short-term band-limited power against
/// a slowly-adapting noise-floor estimate, with assert/release hysteresis and a hold time.
/// This is the deliberately display-decoupled replacement for QtSoundModem's spectral busy
/// detector (which lives in its waterfall paint path and never runs headless). It flags
/// non-packet energy - a carrier, voice, another mode - that the packet DCD cannot see;
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
    /// <param name="blockMilliseconds">Power integration block. Sets detection latency, and
    /// with the feeding filter's width it sets the false-busy rate: see the note on the
    /// default below.</param>
    /// <param name="assertDb">dB above the noise floor at which busy asserts.</param>
    /// <param name="releaseDb">dB above the floor below which busy releases (must be
    /// below <paramref name="assertDb"/> - hysteresis).</param>
    /// <param name="holdMilliseconds">Minimum busy time after the last over-threshold
    /// block, riding through flutter and PSK envelope dips.</param>
    public EnergyBusyDetector(
        int sampleRate,
        // 40 ms, not the 20 this shipped with, and the block length is the lever rather than
        // the floor's adaptation rates. Measured 2026-09-12 on pure seeded noise through the
        // afsk300 multi-modem's own 500 Hz branch band-pass (1250-1750 Hz, 256 taps, 12 kHz):
        // at 20 ms the detector read busy for 0.9 to 1.6 % of 300 s on every seed tried, and
        // 5.9 to 7.3 % through a 400 Hz filter. Through the whole shipped bank, 11 branches
        // ORed into one ChannelBusy, that came out as 3.2 to 4.9 % of a 120 s noise run, which
        // is spurious CSMA deferral on a channel nobody is using. At 40 ms it reads 0.000 % on
        // every seed and every band, and of the lengths swept (10, 15, 20, 25, 30, 40, 50, 60,
        // 80) it is the shortest that does.
        //
        // Why, and why not the rates: the floor below adapts down about 100x faster than it
        // adapts up, so it settles BELOW the mean of the noise it is tracking (measured over
        // 600 s: 2.55 dB below through the 500 Hz filter at 20 ms). The false-busy rate is then
        // set by how wide the block-power estimate scatters, and a block of band-limited noise
        // is a chi-square average with roughly 2*B*T degrees of freedom, which for 500 Hz and
        // 20 ms is 20: sigma 1.41 dB, worst block in 600 s 7.18 dB over the floor, clearing the
        // 6 dB assert 55 times in 30k blocks. Doubling T doubles the degrees of freedom, which pulls
        // BOTH terms in: bias 1.76 dB, sigma 0.99 dB, worst block 5.09 dB, nothing over 6 dB in
        // 600 s. It is one lever on the noise statistics, and it leaves every deliberate
        // behaviour of the floor exactly as it was.
        //
        // The two rate changes that also zero the false busy were measured and rejected, both
        // because they spend a behaviour the asymmetry is there to buy:
        //   - up 0.002 -> 0.02 (floor climbs faster): busy gives up on a carrier 10 dB over the
        //     noise after 0.7 s instead of 5.0 s, i.e. the detector stops seeing a station that
        //     is still sitting on the frequency. That is the failure the slow up rate exists to
        //     prevent. At 40 ms the same rate per longer block runs the climb over 20 s rather
        //     than 10, so busy now holds through 17 % of a 60 s carrier where it held 7 %.
        //   - down 0.2 -> 0.02 (floor falls slower): after a cold start seeded inside a burst
        //     (the warm-up below seeds from the LOUDEST block on purpose), the detector was deaf
        //     to a fresh 10 dB signal for 5.5 s instead of 1.5 s. On c4fsk, where Busy is a hard
        //     gate on the bit path, deaf means no decoding at all.
        //
        // What it costs: assert latency. Measured over 200 random burst-start offsets, a carrier
        // 10 dB over the noise asserts a median 12 ms / worst 25 ms after it starts at 20 ms
        // blocks, and a median 28 ms / worst 49 ms at 40 ms; a real afsk300 burst at 3 dB in-band
        // SNR asserts 80 ms in and holds busy for 97 % of itself. Both sit well inside the CSMA
        // slot time (100 ms) this feeds and inside a stock 300 ms TXDELAY, so a station still
        // sees the other end's preamble in time to defer. One caller cannot pay even that:
        // C4fskModem gates its bit path on Busy and passes 20 ms for the reasons written there.
        int blockMilliseconds = 40,
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
        // Round the hold UP to a whole block: holdMilliseconds is a minimum, and truncating
        // division would quietly hand a 40 ms block an 80 ms hold when 100 was asked for.
        _holdBlocks = Math.Max(1, (holdMilliseconds + blockMilliseconds - 1) / blockMilliseconds);
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
            // are artificially quiet - seeding the floor from them would flag plain noise
            // as busy forever. Seed with the loudest warm-up block (conservative: a busy
            // cold start seeds high, and the fast downward adaptation recovers the true
            // floor as soon as the channel goes quiet).
            _seedBlocksRemaining--;
            _noiseFloor = Math.Max(_noiseFloor, Math.Max(blockPower, 1e-12));
            return;
        }

        // The floor tracks downward quickly and upward very slowly, so it follows the
        // quiet channel but is not dragged up by transmissions sitting on frequency. Both
        // rates are per block, so they are a time constant only together with the block
        // length: at the 40 ms default the upward climb runs over about 20 s. The asymmetry
        // is also why the floor settles below the true mean of the noise, which is what the
        // block length in the constructor is chosen against.
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
