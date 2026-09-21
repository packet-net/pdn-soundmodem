using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.CarrierSense;

/// <summary>
/// Carrier sense for an open-squelch FM path from the audio alone, for a station with no control
/// cable to its radio. It judges the SHAPE of the received spectrum against the shape the
/// station's own idle channel was showing a few seconds ago.
/// </summary>
/// <remarks>
/// <para><b>The one rule this had to obey: no absolute level anywhere in the decision.</b> Two
/// audio detectors were written for this job before it and both failed on that. The energy
/// detector asserts on a rise and an FM signal makes the receiver quieter, so it is
/// anti-correlated. <see cref="FmQuietingBusyDetector"/> gets the direction right and then needs
/// absolute dBFS thresholds to do it, and two nominally identical bench stations measured 22 dB
/// apart: the threshold set from one of them stopped the other transmitting at all. Nothing here
/// is an absolute number except frequencies and times. Every level is a ratio against something
/// the same station measured itself.</para>
/// <para><b>What it measures.</b> An arriving FM carrier captures the discriminator and replaces
/// band noise with modulation, and it does that hardest well above the signal's own band, where
/// the modulation puts nothing back. Measured on radio1, one burst against eight seconds of idle:
/// the signal band 0.3 to 1 kHz quiets by 0.9 dB, 6 to 8 kHz by 15.1 dB and 8 to 10 kHz by
/// 26.5 dB. So the RATIO of power below a split to power above it is a strong statistic and the
/// level is a weak one. An unmodulated carrier, which is what a far end's silent TXDELAY puts on
/// the air and which nothing else here can see at all, quiets by 32 to 54 dB at every
/// frequency.</para>
/// <para><b>Why the split is a sixth of the sample rate.</b> The denominator has to sit just above
/// the widest signal the channel carries, because a modulated signal only quiets the band its own
/// modulation does not fill. Swept from 7 to 18 kHz on a 48 kHz recording of C4FSK 19k2, the
/// margin peaks at 8 kHz and falls monotonically either side: 18.4 dB of worst-case gap there
/// against 10.6 dB at the 16 to 23.5 kHz band the first measurement used.</para>
/// <para><b>Measured, against real recordings.</b> 15 of 15 NinoTNC bursts on the 45 s reference
/// capture and 11 of 11 of our own transmissions on a 660 s chunk, asserting a median 26 ms after
/// the carrier arrives, with <b>zero</b> false-busy blocks in 620 s of idle open-squelch channel.
/// The relative threshold gives 0 missed and 0 false at every value from 4 to 20 dB, which is what
/// buying the margin was for.</para>
/// <para><b>It knows when it does not know.</b> The level gate below is not a quality check, it is
/// the detector asking whether this is a path it understands: one where a signal does NOT change
/// the level much. Digital silence, a muted card, a squelched receiver, receive audio gated by the
/// station's own transmission, and an additive path (SSB, a wired loop) all fail it, and all
/// return null. That is correct rather than a limitation: on an additive path
/// <see cref="Modems.EnergyBusyDetector"/> is the right instrument and keeps the job.</para>
/// <para><b>It does not work at 12 kHz.</b> The split would be 2 kHz, inside a wideband mode's own
/// occupancy. Measured on the same recording decimated, and cross-checked with an ideal brickwall
/// to take the decimator out of the question: 15 of 15 bursts missed, and the best margin over
/// every split from 1 to 4 kHz is 0.6 dB against 19.3 dB at 48 kHz. The caller gates on the
/// channel's rate; see <see cref="MinimumSampleRate"/>.</para>
/// </remarks>
public sealed class FmShapeBusyDetector : IChannelBusySource
{
    /// <summary>
    /// Below this the statistic is measured not to separate, because the split lands inside a
    /// wideband mode's own occupancy.
    /// </summary>
    public const int MinimumSampleRate = 48000;

    /// <summary>
    /// How far the shape ratio must rise above the station's own idle ratio to call the channel
    /// busy. A ratio of a ratio: nothing here is a level.
    /// </summary>
    /// <remarks>
    /// Idle reaches +2.1 dB over 620 s of real idle channel, and +2.6 dB on the worst of seven
    /// simulated station audio paths. Bursts reach +21.4 dB, and +20.1 dB on the worst path. So
    /// this sits 7.4 dB clear of idle and 10.2 dB clear of the shallowest burst.
    /// </remarks>
    public const double AssertAboveReferenceDb = 10.0;

    /// <summary>Where busy releases, for hysteresis.</summary>
    public const double ReleaseAboveReferenceDb = 5.0;

    /// <summary>
    /// Audio this far below the station's own idle level is not a channel to judge: a dead or
    /// muted card, a closed squelch, or the station's own receive audio gated while it transmits.
    /// </summary>
    /// <remarks>
    /// A burst sits 2.7 to 5.0 dB below the idle reference, so there is no risk of a real signal
    /// falling through here. Our own gated audio measured 70 dB below its own idle on radio1 and
    /// 47 dB below on radio2: this covers both with 17 dB to spare on the worse one, and the 22 dB
    /// difference between the two stations, which is what broke the absolute guard, is no longer
    /// in the decision at all.
    /// </remarks>
    public const double DeadInputBelowReferenceDb = 30.0;

    /// <summary>
    /// Audio this far ABOVE the station's own idle level is an additive path, not a quieting one.
    /// </summary>
    /// <remarks>
    /// An SSB receiver or a wired loop gains 40 dB when a signal arrives. Open-squelch FM does the
    /// opposite, and the most a real idle channel rose over 620 s is 1.2 dB.
    /// </remarks>
    public const double LoudInputAboveReferenceDb = 12.0;

    /// <summary>How fast the reference follows a genuine change in the station's own audio.</summary>
    /// <remarks>Frozen while busy, so a burst cannot drag it. At this rate seven blocks of burst
    /// would move it 0.35 dB.</remarks>
    public const double ReferenceStepDbPerSecond = 2.5;

    /// <summary>Audio heard before the detector will assert on anything.</summary>
    /// <remarks>The reference is seeded from the LOUDEST and most in-band-heavy block of the
    /// warm-up, deliberately: a reference seeded high makes the detector deaf, and deaf is the
    /// safe failure.</remarks>
    public const double WarmUpSeconds = 2.0;

    /// <summary>
    /// How long busy may persist before the detector decides its reference is wrong and relearns.
    /// </summary>
    /// <remarks>
    /// The backstop against the only failure that can silence a station. The longest transmission
    /// measured on this channel is 13.6 s; a 10 s backstop was tried and cut a real one short.
    /// </remarks>
    public const double MaxBusySeconds = 30.0;

    /// <summary>Everything below this is outside both the statistic and the level gate, so a stuck
    /// DC level or sub-audio rumble is invisible to the decision rather than read as a
    /// carrier.</summary>
    private const double LowBandFloorHz = 300;

    /// <summary>The analysis window, in seconds, before rounding to a power of two.</summary>
    private const double BlockSeconds = 0.02133;

    private readonly int _fftSize;
    private readonly double[] _window;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly float[] _pending;
    private readonly double _windowPower;
    private readonly double _binHz;
    private readonly int _lowFrom;
    private readonly int _lowTo;
    private readonly int _highTo;
    private readonly int _holdBlocks;
    private readonly int _warmUpBlocks;
    private readonly int _maxBusyBlocks;
    private readonly double _referenceStepDb;

    private int _filled;
    private int _blocksSeen;
    private int _busyBlocks;
    private int _ungatedBlocks;
    private int _hold;
    private double _warmShapeDb = double.NegativeInfinity;
    private double _warmLevelDb = double.NegativeInfinity;
    private double _shapeReferenceDb = double.NaN;
    private double _levelReferenceDb = double.NaN;
    private volatile bool _busy;
    private volatile bool _known;

    /// <param name="sampleRate">The channel's rate. See <see cref="MinimumSampleRate"/>: this is
    /// not refused here, so that a test can measure what it does below it, but a station must not
    /// consult it at 12 kHz.</param>
    /// <param name="holdMilliseconds">Minimum busy time after the shape comes back. Costs 60 ms of
    /// hangover per burst on the reference recording and none at all on the 660 s chunk.</param>
    public FmShapeBusyDetector(int sampleRate, int holdMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        _fftSize = 1 << Math.Max(4, (int)Math.Round(Math.Log2(sampleRate * BlockSeconds)));
        _window = new double[_fftSize];
        _re = new double[_fftSize];
        _im = new double[_fftSize];
        _pending = new float[_fftSize];

        double power = 0;
        for (int i = 0; i < _fftSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _fftSize));
            power += _window[i] * _window[i];
        }

        _windowPower = power / _fftSize;
        _binHz = (double)sampleRate / _fftSize;

        // A sixth of the rate, which is a third of Nyquist: the split is stated as a fraction of
        // the rate rather than in hertz because what it has to clear is the widest signal the
        // channel can carry, and that scales with the rate too.
        double splitHz = sampleRate / 6.0;
        _lowFrom = Math.Max(1, (int)Math.Ceiling(LowBandFloorHz / _binHz));
        _lowTo = (int)Math.Floor(splitHz / _binHz);
        _highTo = (_fftSize / 2) - 1;

        double blockSeconds = (double)_fftSize / sampleRate;
        _holdBlocks = Math.Max(1, (int)Math.Round(holdMilliseconds / 1000.0 / blockSeconds));
        _warmUpBlocks = Math.Max(1, (int)Math.Round(WarmUpSeconds / blockSeconds));
        _maxBusyBlocks = Math.Max(1, (int)Math.Round(MaxBusySeconds / blockSeconds));
        _referenceStepDb = ReferenceStepDbPerSecond * blockSeconds;
    }

    /// <inheritdoc/>
    public bool? Busy => _known ? _busy : null;

    /// <summary>The shape ratio of the last block, in dB. For diagnostics and tests.</summary>
    public double ShapeDb { get; private set; }

    /// <summary>The station's own idle shape ratio, which <see cref="ShapeDb"/> is judged against.
    /// NaN until the warm-up is done.</summary>
    public double ShapeReferenceDb => _shapeReferenceDb;

    /// <summary>The last block's total power in the measured bands, in dB. For diagnostics.</summary>
    public double LevelDb { get; private set; }

    /// <summary>The station's own idle level, which the competence gate is relative to.</summary>
    public double LevelReferenceDb => _levelReferenceDb;

    /// <summary>Whether the detector considers this a path it can judge at all.</summary>
    public bool Engaged => _known;

    /// <summary>Feeds received audio.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        int at = 0;
        while (at < samples.Length)
        {
            int take = Math.Min(_fftSize - _filled, samples.Length - at);
            samples.Slice(at, take).CopyTo(_pending.AsSpan(_filled, take));
            _filled += take;
            at += take;
            if (_filled == _fftSize)
            {
                _filled = 0;
                Block();
            }
        }
    }

    /// <summary>Forgets everything and relearns, as after the station's own transmission.</summary>
    public void Reset()
    {
        _filled = 0;
        _blocksSeen = 0;
        _busyBlocks = 0;
        _ungatedBlocks = 0;
        _hold = 0;
        _warmShapeDb = double.NegativeInfinity;
        _warmLevelDb = double.NegativeInfinity;
        _shapeReferenceDb = double.NaN;
        _levelReferenceDb = double.NaN;
        _busy = false;
        _known = false;
    }

    private void Block()
    {
        for (int i = 0; i < _fftSize; i++)
        {
            _re[i] = _pending[i] * _window[i];
            _im[i] = 0;
        }

        RealFft.Forward(_re, _im);

        double low = BandPower(_lowFrom, _lowTo);
        double high = BandPower(_lowTo + 1, _highTo);

        // Digital silence: there is nothing here to have an opinion about, and a ratio of two
        // zeroes is not one.
        if (low <= 0 && high <= 0)
        {
            Decline();
            return;
        }

        LevelDb = 10 * Math.Log10(Math.Max(low + high, 1e-30));
        ShapeDb = 10 * Math.Log10(Math.Max(low, 1e-30)) - 10 * Math.Log10(Math.Max(high, 1e-30));

        if (_blocksSeen < _warmUpBlocks)
        {
            _warmShapeDb = Math.Max(_warmShapeDb, ShapeDb);
            _warmLevelDb = Math.Max(_warmLevelDb, LevelDb);
            if (++_blocksSeen == _warmUpBlocks)
            {
                _shapeReferenceDb = _warmShapeDb;
                _levelReferenceDb = _warmLevelDb;
            }

            Decline();
            return;
        }

        if (_busy && _known && ++_busyBlocks > _maxBusyBlocks)
        {
            // Busy has outlasted anything this channel carries, so the reference is wrong rather
            // than the channel occupied. Throw it away and relearn from the audio arriving now.
            Relearn();
            return;
        }

        // The competence gate, entirely relative to this station's own idle. A dead card, a muted
        // card, a closed squelch, our own receive audio gated while we transmit, and an additive
        // path all sit outside it, and on none of them does a shape test mean anything.
        if (LevelDb < _levelReferenceDb - DeadInputBelowReferenceDb
            || LevelDb > _levelReferenceDb + LoudInputAboveReferenceDb)
        {
            Decline();
            _busyBlocks = 0;
            _hold = 0;
            if (++_ungatedBlocks > _maxBusyBlocks)
            {
                // The input has stopped resembling what the reference was built from for longer
                // than any transmission, so the reference is stale rather than the input wrong.
                Relearn();
            }

            return;
        }

        _ungatedBlocks = 0;

        if (ShapeDb > _shapeReferenceDb + AssertAboveReferenceDb)
        {
            if (!_busy)
            {
                _busyBlocks = 0;
            }

            _busy = true;
            _known = true;
            _hold = _holdBlocks;
            return;
        }

        if (ShapeDb < _shapeReferenceDb + ReleaseAboveReferenceDb)
        {
            if (_busy && --_hold > 0)
            {
                _known = true;
                return;
            }

            _busy = false;
            _busyBlocks = 0;
        }
        else if (!_busy)
        {
            _busy = false;
        }

        _known = true;

        // The reference only moves while the channel is not held busy, and by a fixed step, so a
        // burst cannot drag it while a genuine change in the station's audio is still followed.
        if (!_busy)
        {
            _shapeReferenceDb += Math.Sign(ShapeDb - _shapeReferenceDb) * _referenceStepDb;
            _levelReferenceDb += Math.Sign(LevelDb - _levelReferenceDb) * _referenceStepDb;
        }
    }

    private void Decline()
    {
        _busy = false;
        _known = false;
    }

    private void Relearn()
    {
        _busy = false;
        _known = false;
        _busyBlocks = 0;
        _ungatedBlocks = 0;
        _hold = 0;
        _blocksSeen = 0;
        _warmShapeDb = double.NegativeInfinity;
        _warmLevelDb = double.NegativeInfinity;
        _shapeReferenceDb = double.NaN;
        _levelReferenceDb = double.NaN;
    }

    private double BandPower(int fromBin, int toBin)
    {
        double acc = 0;
        for (int k = fromBin; k <= toBin; k++)
        {
            acc += 2 * ((_re[k] * _re[k]) + (_im[k] * _im[k]));
        }

        return acc / ((double)_fftSize * _fftSize * _windowPower);
    }
}
