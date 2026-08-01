using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Measures per-modem burst SNR and extent from the same <see cref="WaterfallSource"/>
/// lines the display draws — one tracker per modem band. Each line contributes the band's
/// mean linear power; the noise floor is the minimum half-second block average over the
/// last ~15 s (the <see cref="Modems.EnergyBusyDetector"/> min-tracking idea, on spectral
/// rather than time-domain power); a burst is a run of lines ≥ 6 dB over that floor. When
/// a frame decodes, <see cref="TryMeasureBurst"/> reports the burst it arrived on: mean
/// SNR over the run and the run length in lines. Being derived from the display's own data,
/// the numbers always agree with what the waterfall shows.
/// </summary>
public sealed class BandActivityTracker
{
    private const double BurstThresholdRatio = 4; // 6 dB over the floor
    private static readonly double[] ByteToLinearPower = BuildLut();

    private readonly int _lowBin;
    private readonly int _binCount;
    private readonly int _blockLines;
    private readonly int _graceLines;
    private readonly double[] _floorRing;
    private int _floorRingFilled;
    private int _floorRingIndex;
    private double _blockSum;
    private int _blockFilled;

    private int _runLines;
    private double _runSum;
    private int _lastRunLines;
    private double _lastRunSnrDb;
    private int _linesSinceRunEnd = int.MaxValue;

    /// <summary>Creates a tracker for one modem's band.</summary>
    /// <param name="binWidthHz">The waterfall source's bin width.</param>
    /// <param name="linesPerSecond">The waterfall source's line rate.</param>
    /// <param name="lineLength">Bins per line (bounds the band).</param>
    /// <param name="lowHz">Band low edge.</param>
    /// <param name="highHz">Band high edge.</param>
    public BandActivityTracker(double binWidthHz, int linesPerSecond, int lineLength, double lowHz, double highHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(binWidthHz, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(linesPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(highHz, lowHz);
        _lowBin = Math.Clamp((int)(lowHz / binWidthHz), 0, lineLength - 1);
        int highBin = Math.Clamp((int)Math.Ceiling(highHz / binWidthHz), _lowBin + 1, lineLength);
        _binCount = highBin - _lowBin;
        _blockLines = Math.Max(1, linesPerSecond / 2);
        _graceLines = linesPerSecond * 2;
        _floorRing = new double[30]; // 30 half-second blocks ≈ 15 s of floor memory
    }

    /// <summary>Feeds one waterfall line (the reused source buffer is fine — nothing is kept).</summary>
    public void AddLine(ReadOnlySpan<byte> line)
    {
        double sum = 0;
        for (int n = 0; n < _binCount; n++)
        {
            sum += ByteToLinearPower[line[_lowBin + n]];
        }

        double power = sum / _binCount;

        // Half-second block averages feed the min-tracking floor ring.
        _blockSum += power;
        if (++_blockFilled == _blockLines)
        {
            _floorRing[_floorRingIndex] = _blockSum / _blockLines;
            _floorRingIndex = (_floorRingIndex + 1) % _floorRing.Length;
            _floorRingFilled = Math.Min(_floorRingFilled + 1, _floorRing.Length);
            _blockSum = 0;
            _blockFilled = 0;
        }

        double floor = NoiseFloor(power);
        if (power >= floor * BurstThresholdRatio)
        {
            _runLines++;
            _runSum += power;
            return;
        }

        if (_runLines > 0)
        {
            _lastRunLines = _runLines;
            _lastRunSnrDb = 10 * Math.Log10(_runSum / _runLines / floor);
            _runLines = 0;
            _runSum = 0;
            _linesSinceRunEnd = 0;
        }
        else if (_linesSinceRunEnd < int.MaxValue)
        {
            _linesSinceRunEnd++;
        }
    }

    /// <summary>
    /// The burst behind a just-decoded frame: mean band SNR over the run and its length in
    /// lines. True while a burst is in progress or within two seconds of one ending (decode
    /// completes at burst end; the grace covers deframer flush latency). False when the band
    /// has been quiet — the frame cannot be attributed to visible energy.
    /// </summary>
    public bool TryMeasureBurst(out double snrDb, out int burstLines)
    {
        if (_runLines > 0)
        {
            double floor = NoiseFloor(_runSum / _runLines);
            snrDb = 10 * Math.Log10(_runSum / _runLines / floor);
            burstLines = _runLines;
            return true;
        }

        if (_linesSinceRunEnd <= _graceLines && _lastRunLines > 0)
        {
            snrDb = _lastRunSnrDb;
            burstLines = _lastRunLines;
            return true;
        }

        snrDb = 0;
        burstLines = 0;
        return false;
    }

    private double NoiseFloor(double fallback)
    {
        if (_floorRingFilled == 0)
        {
            // Warm-up: nothing banked yet — treat the current level as the floor (matches
            // EnergyBusyDetector's warm-up seeding; a burst in the first half second will
            // under-read, honestly, rather than fabricate a floor).
            return Math.Max(fallback, 1e-12);
        }

        double min = double.MaxValue;
        for (int n = 0; n < _floorRingFilled; n++)
        {
            min = Math.Min(min, _floorRing[n]);
        }

        return Math.Max(min, 1e-12);
    }

    private static double[] BuildLut()
    {
        var lut = new double[256];
        for (int b = 0; b < 256; b++)
        {
            double db = WaterfallSource.FloorDb + b * (-WaterfallSource.FloorDb / 255);
            lut[b] = Math.Pow(10, db / 10);
        }

        return lut;
    }
}
