using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Survey;

/// <summary>
/// Finds bursts of energy anywhere in the passband, from the same spectrum lines the waterfall
/// draws — the whole-band generalisation of <see cref="Waterfall.BandActivityTracker"/>, which
/// measures one declared modem band.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a station notice a transmission it was never configured to decode. The
/// alternative — a comb of decoders across the passband — fails for a reason that is not CPU:
/// the mode is unknown too, so brute force is centres × modes, and it is still silent when it
/// guesses wrong. Energy, by contrast, is already being computed for the display.
/// </para>
/// <para>
/// <b>Floor.</b> Per bin, the minimum half-second block average over the last ~15 s — the
/// <see cref="Modems.EnergyBusyDetector"/> min-tracking idea, per bin rather than per band, so
/// a signal parked in one part of the passband cannot raise the floor everywhere else. It is
/// recomputed when a block closes, not per line: a floor that moved within a burst would chase
/// the burst.
/// </para>
/// <para>
/// <b>Bursts.</b> A run of adjacent bins standing <see cref="ThresholdDb"/> over their floors is
/// a detection; runs on consecutive lines that overlap in frequency are the same burst. A burst
/// is reported only once it <em>ends</em>, because "started and stopped" is most of what
/// separates a transmission from a carrier — and because its width and duration are not known
/// until then. Brief drop-outs are bridged (<c>graceLines</c>) so a fade does not split one
/// transmission into three.
/// </para>
/// </remarks>
public sealed class SpectralBurstDetector
{
    /// <summary>How far over its floor a bin must stand to count as signal. 6 dB, matching
    /// <see cref="Waterfall.BandActivityTracker"/> so the two agree about what a burst is.</summary>
    public const double ThresholdDb = 6;

    private static readonly double[] ByteToLinearPower = BuildLut();

    private readonly Action<SurveyBurst> _burstClosed;
    private readonly double _binWidthHz;
    private readonly int _lowBin;
    private readonly int _binCount;
    private readonly int _blockLines;
    private readonly int _minRunBins;
    private readonly int _minLines;
    private readonly int _graceLines;
    private readonly int _maxLines;

    // Per-bin floor machinery: block sums being accumulated, a ring of completed block averages,
    // and the min over that ring — all preallocated, nothing per line.
    private readonly double[] _blockSum;
    private readonly int[] _blockCount;     // per bin: lines that contributed, hot ones excluded
    private readonly double[] _floorRing;   // _floorBlocks × _binCount, block-major
    private readonly double[] _floor;
    private readonly bool[] _hot;
    private readonly int _floorBlocks;
    private int _floorRingIndex;
    private int _floorRingFilled;
    private int _blockFilled;

    private readonly List<Open> _open = [];
    private readonly List<(int Low, int High)> _runs = [];
    private long _lastLine = -1;

    /// <summary>Creates a detector over part of the spectrum.</summary>
    /// <param name="binWidthHz">The line source's bin width.</param>
    /// <param name="linesPerSecond">The line source's line rate.</param>
    /// <param name="lineLength">Bins per line.</param>
    /// <param name="burstClosed">Called once per burst, when it ends.</param>
    /// <param name="lowHz">Low edge of the range watched — below the audio passband is DC,
    /// rumble and the sound card's own noise, none of it a transmission.</param>
    /// <param name="highHz">High edge of the range watched.</param>
    /// <param name="minWidthHz">Narrowest run that counts. Below this is a carrier, a tuning
    /// whistle or a single hot bin, not a modulated signal.</param>
    /// <param name="minSeconds">Shortest burst that counts, in seconds — a click is not a frame.</param>
    /// <param name="maxSeconds">Longest a burst may run before it is closed as a timeout. A
    /// carrier would otherwise hold one open forever and never be reported at all.</param>
    /// <param name="graceSeconds">How long a burst may drop below threshold and still be the
    /// same burst.</param>
    public SpectralBurstDetector(
        double binWidthHz,
        int linesPerSecond,
        int lineLength,
        Action<SurveyBurst> burstClosed,
        double lowHz = 200,
        double highHz = 3200,
        double minWidthHz = 150,
        double minSeconds = 0.15,
        double maxSeconds = 30,
        double graceSeconds = 0.2)
    {
        ArgumentNullException.ThrowIfNull(burstClosed);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(binWidthHz, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(linesPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lineLength, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(highHz, lowHz);

        _burstClosed = burstClosed;
        _binWidthHz = binWidthHz;
        _lowBin = Math.Clamp((int)(lowHz / binWidthHz), 0, lineLength - 1);
        int highBin = Math.Clamp((int)Math.Ceiling(highHz / binWidthHz), _lowBin + 1, lineLength);
        _binCount = highBin - _lowBin;
        _blockLines = Math.Max(1, linesPerSecond / 2);
        _floorBlocks = 30;                                  // 30 half-second blocks ≈ 15 s
        _minRunBins = Math.Max(1, (int)Math.Round(minWidthHz / binWidthHz));
        _minLines = Math.Max(1, (int)Math.Round(minSeconds * linesPerSecond));
        _maxLines = Math.Max(_minLines + 1, (int)Math.Round(maxSeconds * linesPerSecond));
        _graceLines = Math.Max(1, (int)Math.Round(graceSeconds * linesPerSecond));

        _blockSum = new double[_binCount];
        _blockCount = new int[_binCount];
        _floorRing = new double[_floorBlocks * _binCount];
        _floor = new double[_binCount];
        _hot = new bool[_binCount];
    }

    /// <summary>True once a floor has been banked and detections mean anything. Before that the
    /// detector is warming up and reports nothing — an honest silence rather than a burst
    /// measured against a floor it does not have.</summary>
    public bool Ready => _floorRingFilled > 0;

    /// <summary>
    /// Feeds one spectrum line. The source's own buffer is fine — nothing is retained.
    /// </summary>
    /// <param name="lineIndex">The source's monotonic line index, which is what burst extents
    /// are reported in and what the caller maps back to audio.</param>
    /// <param name="line">dB-scaled bytes, bin 0 = DC (see <see cref="WaterfallSource"/>).</param>
    public void AddLine(long lineIndex, ReadOnlySpan<byte> line)
    {
        // A gap in the line clock means audio stopped reaching us — the station transmitted, or
        // the device dried up. Everything open is abandoned rather than stretched across it.
        if (_lastLine >= 0 && lineIndex != _lastLine + 1)
        {
            _open.Clear();
        }

        _lastLine = lineIndex;

        // Which bins are carrying signal right now. Needed before the floor is updated as well as
        // after, because a bin carrying signal is not measuring noise.
        const double ratio = 3.98107;   // 6 dB
        bool ready = Ready;
        for (int n = 0; n < _binCount; n++)
        {
            double power = ByteToLinearPower[line[_lowBin + n]];
            _hot[n] = ready && power >= _floor[n] * ratio;
            if (!_hot[n])
            {
                _blockSum[n] += power;
                _blockCount[n]++;
            }
        }

        if (++_blockFilled == _blockLines)
        {
            CloseBlock();
        }

        if (!ready)
        {
            return;
        }

        FindRuns();
        MatchRuns(lineIndex, line);
        AgeOut(lineIndex);
    }

    /// <summary>Abandons everything in flight — nothing that straddles a break in the audio is a
    /// measurement of anything. Called when the station keys up.</summary>
    public void Reset()
    {
        _open.Clear();
        _lastLine = -1;
    }

    /// <summary>
    /// Banks this block's per-bin averages and recomputes the floor.
    /// </summary>
    /// <remarks>
    /// Bins that were carrying signal are excluded, and a bin hot for the whole block keeps the
    /// floor it had. Without that, a signal outlasting the floor's ~15 s memory fills the window
    /// with its own energy, raises its own floor, and stops looking like a burst — so a 25-second
    /// SSB over reported as two 13-second "packets", each short enough to pass a duration gate.
    /// A floor is a measurement of noise, and a bin carrying a transmission is not measuring any.
    /// </remarks>
    private void CloseBlock()
    {
        int at = _floorRingIndex * _binCount;
        int previous = ((_floorRingIndex + _floorBlocks - 1) % _floorBlocks) * _binCount;
        for (int n = 0; n < _binCount; n++)
        {
            _floorRing[at + n] = _blockCount[n] > 0
                ? _blockSum[n] / _blockCount[n]
                : _floorRingFilled > 0 ? _floorRing[previous + n] : _floor[n];
            _blockSum[n] = 0;
            _blockCount[n] = 0;
        }

        _floorRingIndex = (_floorRingIndex + 1) % _floorBlocks;
        _floorRingFilled = Math.Min(_floorRingFilled + 1, _floorBlocks);
        _blockFilled = 0;

        for (int n = 0; n < _binCount; n++)
        {
            double min = double.MaxValue;
            for (int block = 0; block < _floorRingFilled; block++)
            {
                min = Math.Min(min, _floorRing[block * _binCount + n]);
            }

            _floor[n] = Math.Max(min, 1e-12);
        }
    }

    /// <summary>Contiguous runs of bins standing over their floors, wide enough to be a signal.</summary>
    private void FindRuns()
    {
        _runs.Clear();
        int runStart = -1;
        for (int n = 0; n < _binCount; n++)
        {
            bool hot = _hot[n];
            if (hot && runStart < 0)
            {
                runStart = n;
            }
            else if (!hot && runStart >= 0)
            {
                if (n - runStart >= _minRunBins)
                {
                    _runs.Add((runStart, n));
                }

                runStart = -1;
            }
        }

        if (runStart >= 0 && _binCount - runStart >= _minRunBins)
        {
            _runs.Add((runStart, _binCount));
        }
    }

    /// <summary>Extends the bursts this line's runs belong to, and opens one for each run that
    /// belongs to none.</summary>
    private void MatchRuns(long lineIndex, ReadOnlySpan<byte> line)
    {
        foreach ((int low, int high) in _runs)
        {
            Open? match = null;
            foreach (Open candidate in _open)
            {
                // Overlapping in frequency on consecutive lines is the same transmission. Edges
                // wander by a bin or two as a signal fades, so overlap — not equality — is what
                // keeps a burst whole.
                if (low < candidate.HighBin && high > candidate.LowBin)
                {
                    match = candidate;
                    break;
                }
            }

            if (match is null)
            {
                match = new Open { StartLine = lineIndex, LowBin = low, HighBin = high };
                _open.Add(match);
            }

            match.LowBin = Math.Min(match.LowBin, low);
            match.HighBin = Math.Max(match.HighBin, high);
            match.LowSum += low;
            match.HighSum += high;
            match.Lines++;
            match.LastLine = lineIndex;

            double power = 0;
            double floor = 0;
            for (int n = low; n < high; n++)
            {
                power += ByteToLinearPower[line[_lowBin + n]];
                floor += _floor[n];
            }

            double snr = power / Math.Max(floor, 1e-12);
            match.SnrSum += snr;
            match.PeakSnr = Math.Max(match.PeakSnr, snr);
        }
    }

    /// <summary>Closes bursts that have stopped, and any that has run so long it cannot be a
    /// transmission.</summary>
    private void AgeOut(long lineIndex)
    {
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            Open open = _open[i];
            bool timedOut = lineIndex - open.StartLine >= _maxLines;
            if (!timedOut && lineIndex - open.LastLine <= _graceLines)
            {
                continue;
            }

            _open.RemoveAt(i);
            if (open.Lines < _minLines)
            {
                continue;   // a click, not a transmission
            }

            _burstClosed(new SurveyBurst(
                open.StartLine,
                open.LastLine + 1,
                (_lowBin + (open.LowSum / open.Lines)) * _binWidthHz,
                (_lowBin + (open.HighSum / open.Lines)) * _binWidthHz,
                10 * Math.Log10(Math.Max(open.PeakSnr, 1e-12)),
                10 * Math.Log10(Math.Max(open.SnrSum / open.Lines, 1e-12)),
                timedOut));
        }
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

    /// <summary>A burst still in progress. A class, not a struct: it is mutated in place in a
    /// list while the burst runs.</summary>
    private sealed class Open
    {
        public long StartLine;
        public long LastLine;
        public int LowBin;
        public int HighBin;
        public double LowSum;
        public double HighSum;
        public double SnrSum;
        public double PeakSnr;
        public int Lines;
    }
}
