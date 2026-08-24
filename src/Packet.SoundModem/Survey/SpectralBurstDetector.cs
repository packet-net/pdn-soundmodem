using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Survey;

/// <summary>
/// Finds bursts of energy anywhere in the passband, from the same spectrum lines the waterfall
/// draws - the whole-band generalisation of <see cref="Waterfall.BandActivityTracker"/>, which
/// measures one declared modem band.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a station notice a transmission it was never configured to decode. The
/// alternative - a comb of decoders across the passband - fails for a reason that is not CPU:
/// the mode is unknown too, so brute force is centres × modes, and it is still silent when it
/// guesses wrong. Energy, by contrast, is already being computed for the display.
/// </para>
/// <para>
/// <b>Floor.</b> Per bin, <see cref="Modems.EnergyBusyDetector"/>'s asymmetric one-pole
/// tracker - down fast, up slowly, but always up - run per bin rather than per band, so a
/// signal parked in one part of the passband cannot raise the floor everywhere else. It moves
/// when a block closes, not per line: a floor that moved within a burst would chase the burst.
/// A bin an open burst covers is not measuring noise at all and its floor is held still for
/// as long as the burst runs, which is what stops the tracker climbing into a transmission
/// and going blind to it (see <see cref="CloseBlock"/>).
/// </para>
/// <para>
/// It was a rolling minimum over the last ~15 s until 2026-08-05, and that latched. A dip
/// deeper than <see cref="ThresholdDb"/> entered the window and took the floor with it;
/// ordinary noise then stood over the lowered floor, so every line in those bins read as hot;
/// a block with no unhot line to average carried the previous value forward, which was the
/// dip. The low value recirculated and the floor could not climb back, because climbing back
/// needed the noise to fall below a floor already beneath it. A station left collecting on
/// 40 m for five hours wrote 95 captures of which 29 held anything that stood out from the
/// noise, at SNRs that barely correlated with whether there was a signal there at all
/// (r = +0.27, the inflation being the depth of whatever fade had latched the bin); the
/// honest ones clustered in the minute after each restart, where the floor was still new.
/// A tracker cannot latch: every block moves it, and the only question is how far.
/// </para>
/// <para>
/// <b>Bursts.</b> A run of adjacent bins standing <see cref="ThresholdDb"/> over their floors is
/// a detection; runs on consecutive lines that overlap in frequency are the same burst. A burst
/// is reported only once it <em>ends</em>, because "started and stopped" is most of what
/// separates a transmission from a carrier - and because its width and duration are not known
/// until then. Brief drop-outs are bridged (<c>graceLines</c>) so a fade does not split one
/// transmission into three.
/// </para>
/// </remarks>
public sealed class SpectralBurstDetector
{
    /// <summary>How far over its floor a bin must stand to count as signal. 6 dB, matching
    /// <see cref="Waterfall.BandActivityTracker"/> so the two agree about what a burst is.</summary>
    public const double ThresholdDb = 6;

    // The three rates the floor moves at, per half-second block. Down is quick, because a
    // channel that has genuinely gone quieter is a fact about the noise and the sooner the
    // floor says so the sooner a weak signal over it can be seen. Up is slower, because most
    // of what raises a bin's power is somebody transmitting on it. Which of the two upward
    // rates applies is decided by whether the block held any line measuring noise: a block
    // with quiet lines in it is noise that has come up and the floor should follow it over
    // ten seconds or so, while a bin hot on every line of every block - and never wide enough
    // to open a burst - is a het or a carrier, and the floor must barely move under it. It
    // must still move, because a bin that is hot for ever is not a transmission, it is a
    // floor that is wrong.
    //
    // Neither upward rate is a rate in dB: the step is a fraction of the distance to what the
    // block measured, and that distance is the signal's own power. At 0.05 of a 20 dB gap the
    // floor climbs 1.6 dB in half a second, so these rates are only ever safe on a bin that
    // is measuring noise. Deciding which bins those are is the whole of CloseBlock.
    private const double DownRate = 0.25;
    private const double UpRate = 0.05;
    private const double StuckUpRate = 0.004;

    // Blocks averaged before detections mean anything, taking the loudest as the seed. Same
    // reasoning as EnergyBusyDetector's: a cold start seeds high, which costs a couple of
    // seconds of deafness, where seeding low costs every bin reading as busy until the floor
    // has climbed all the way back.
    private const int SeedBlocks = 4;

    private static readonly double[] ByteToLinearPower = WaterfallSource.ByteToLinearPower;

    private readonly Action<SurveyBurst> _burstClosed;
    private readonly double _binWidthHz;
    private readonly int _lowBin;
    private readonly int _binCount;
    private readonly int _blockLines;
    private readonly int _minRunBins;
    private readonly int _minLines;
    private readonly int _graceLines;
    private readonly int _maxLines;

    // Per-bin floor machinery: the block being accumulated and the tracked floor itself - all
    // preallocated, nothing per line. Two sums, because the floor wants the quiet lines when
    // there are any and needs to know what the loud ones measured when there are not.
    private readonly double[] _blockSum;    // per bin: lines measuring noise only
    private readonly int[] _blockCount;     // per bin: lines that contributed to _blockSum
    private readonly double[] _blockAllSum; // per bin: every line, hot or not
    private readonly bool[] _blockCarried;  // per bin: an open burst covered it during the block
    private readonly double[] _blockPower;  // per bin: what the last closed block measured
    private readonly double[] _floor;
    private readonly bool[] _hot;
    private readonly bool[] _inBurst;       // per bin: covered by a burst open right now
    private int _seedBlocksRemaining = SeedBlocks;
    private int _blockFilled;

    /// <summary>
    /// How far over <see cref="WaterfallSource.FloorDb"/> a bin's floor must sit before that bin
    /// is measuring anything at all. Three decibels: a floor at the bottom of the encoding scale
    /// is not a quiet channel, it is a bin with nothing arriving in it, and 3 dB is comfortably
    /// below the quietest real noise a receiver delivers - the live 40 m station's quietest
    /// in-passband bins sit 16 dB over it and its ordinary ones 25 to 55.
    /// </summary>
    private const double DeadBinMarginDb = 3;

    /// <summary>The linear power <see cref="DeadBinMarginDb"/> describes.</summary>
    private static readonly double DeadBinPower =
        Math.Pow(10, (WaterfallSource.FloorDb + DeadBinMarginDb) / 10);

    private readonly List<Open> _open = [];
    private readonly List<(int Low, int High)> _runs = [];
    private long _lastLine = -1;
    private long _deadBins;

    /// <summary>Creates a detector over part of the spectrum.</summary>
    /// <param name="binWidthHz">The line source's bin width.</param>
    /// <param name="linesPerSecond">The line source's line rate.</param>
    /// <param name="lineLength">Bins per line.</param>
    /// <param name="burstClosed">Called once per burst, when it ends.</param>
    /// <param name="lowHz">Low edge of the range watched - below the audio passband is DC,
    /// rumble and the sound card's own noise, none of it a transmission.</param>
    /// <param name="highHz">High edge of the range watched.</param>
    /// <param name="minWidthHz">Narrowest run that counts. Below this is a carrier, a tuning
    /// whistle or a single hot bin, not a modulated signal.</param>
    /// <param name="minSeconds">Shortest burst that counts, in seconds - a click is not a frame.</param>
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
        _minRunBins = Math.Max(1, (int)Math.Round(minWidthHz / binWidthHz));

        // Ceiling, not Round. "At least minSeconds" is what the parameter says and rounding does
        // not deliver it: the default 0.15 s at 30 lines/s is 4.5 lines, and Math.Round takes
        // that to 4 (midpoint-to-even), so the shortest burst the detector accepted was 0.133 s.
        // The two thirtieths of a second in the gap are not academic - they are precisely where
        // a one-line glitch lives, and 39% of the live 40 m station's unclaimed captures were
        // 0.133 s events sitting in them.
        _minLines = Math.Max(1, (int)Math.Ceiling(minSeconds * linesPerSecond));
        _maxLines = Math.Max(_minLines + 1, (int)Math.Round(maxSeconds * linesPerSecond));
        _graceLines = Math.Max(1, (int)Math.Round(graceSeconds * linesPerSecond));

        _blockSum = new double[_binCount];
        _blockCount = new int[_binCount];
        _blockAllSum = new double[_binCount];
        _blockCarried = new bool[_binCount];
        _blockPower = new double[_binCount];
        _floor = new double[_binCount];
        _hot = new bool[_binCount];
        _inBurst = new bool[_binCount];
    }

    /// <summary>True once a floor has been seeded and detections mean anything. Before that the
    /// detector is warming up and reports nothing - an honest silence rather than a burst
    /// measured against a floor it does not have.</summary>
    public bool Ready => _seedBlocksRemaining == 0;

    /// <summary>
    /// Feeds one spectrum line. The source's own buffer is fine - nothing is retained.
    /// </summary>
    /// <param name="lineIndex">The source's monotonic line index, which is what burst extents
    /// are reported in and what the caller maps back to audio.</param>
    /// <param name="line">dB-scaled bytes, bin 0 = DC (see <see cref="WaterfallSource"/>).</param>
    public void AddLine(long lineIndex, ReadOnlySpan<byte> line)
    {
        // A gap in the line clock means audio stopped reaching us - the station transmitted, or
        // the device dried up. Everything open is abandoned rather than stretched across it.
        if (_lastLine >= 0 && lineIndex != _lastLine + 1)
        {
            _open.Clear();
            Array.Clear(_inBurst);
        }

        _lastLine = lineIndex;

        // Which bins are carrying signal right now. Needed before the floor is updated as well as
        // after, because a bin carrying signal is not measuring noise.
        const double ratio = 3.98107;   // 6 dB
        bool ready = Ready;
        for (int n = 0; n < _binCount; n++)
        {
            double power = ByteToLinearPower[line[_lowBin + n]];

            // A bin whose floor has reached the bottom of the encoding scale is not measuring a
            // quiet channel; it is measuring nothing, and an SNR against it is arithmetic on an
            // absence. Above a receiver's filter cut the audio really is empty - the live 40 m
            // station's bins above its slice's 2550 Hz high cut sit at exactly -100 dBFS, byte
            // zero - so any energy at all reads as an enormous burst: a break in the waveform,
            // broadband by construction and 60 dB over nothing, was reported as a 460 Hz signal
            // at 39 dB SNR. 3,433 of that station's 8,874 unclaimed captures were that, all of
            // them clustered above the filter cut, because it is the only part of the spectrum
            // quiet enough for it to show. In the passband the same break is invisible.
            bool alive = _floor[n] > DeadBinPower;
            _hot[n] = ready && alive && power >= _floor[n] * ratio;
            if (ready && !alive)
            {
                _deadBins++;
            }
            _blockAllSum[n] += power;

            // A bin under an open burst is held out of the noise average whether or not this
            // particular line stood over threshold. Hotness alone is the wrong test: it is the
            // same 6 dB the detector uses, so everything a modulated signal does between 0 and
            // 6 dB - the gaps between an FSK pair's tones, a symbol transition, the shoulders
            // of the shaped spectrum - reads as noise and feeds the floor the signal's own
            // energy. The burst is the honest statement that this part of the spectrum is
            // carrying a transmission, and a bin carrying a transmission is not measuring any
            // noise.
            if (_inBurst[n])
            {
                _blockCarried[n] = true;
                continue;
            }

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
        MarkOpenBins();
    }

    /// <summary>
    /// Bins ignored because nothing is arriving in them at all - see
    /// <see cref="DeadBinMarginDb"/>. Non-zero means the receiver is delivering less of the
    /// passband than the survey is watching, which is worth an operator knowing.
    /// </summary>
    public long DeadBins => _deadBins;

    /// <summary>Abandons everything in flight - nothing that straddles a break in the audio is a
    /// measurement of anything. Called when the station keys up.</summary>
    public void Reset()
    {
        _open.Clear();
        Array.Clear(_inBurst);
        _lastLine = -1;
    }

    /// <summary>
    /// Averages this block per bin, over the lines that were measuring noise, and moves each
    /// bin's floor towards it. Bins an open burst was sitting on are held still.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bins carrying signal are held out of the average, because a floor is a measurement of
    /// noise and a bin carrying a transmission is not measuring any. Without that, a signal
    /// outlasting the floor's memory fills it with its own energy, raises its own floor, and
    /// stops looking like a burst - a 25-second SSB over reported as two 13-second "packets",
    /// each short enough to pass a duration gate.
    /// </para>
    /// <para>
    /// <b>Held out means the burst, not the hot lines.</b> Excluding only the lines standing
    /// over threshold is not enough, and the 40 m station showed why: a 300 baud signal
    /// captured 2026-08-24 at 1149 Hz drove its own floor up 13.6 dB in half a minute and the
    /// detector lost sight of it after three seconds, because a modulated signal spends a
    /// great deal of every bin's time between 0 and 6 dB over the noise - and every one of
    /// those lines was being averaged in as if the channel were quiet. What is measuring noise
    /// is not "this line is under threshold" but "no burst is open on this part of the
    /// spectrum", so that is the test, and a bin under an open burst does not move at all.
    /// </para>
    /// <para>
    /// A burst cannot hold a floor still forever: it is closed at <c>maxSeconds</c> whatever
    /// it is doing, and a burst closed that way has its bins' floors snapped up to what the
    /// block last measured (<see cref="AgeOut"/>). That is the escape the slow upward rate
    /// used to provide - a bin hot for longer than any transmission can run is a floor that is
    /// wrong, not a signal - now bounded by the timeout rather than by a creep that a real
    /// transmission could not survive.
    /// </para>
    /// <para>
    /// A bin hot on every line with no burst over it is a het or a carrier too narrow to be
    /// one, and nothing will ever close over it, so it keeps the old slow climb.
    /// </para>
    /// </remarks>
    private void CloseBlock()
    {
        for (int n = 0; n < _binCount; n++)
        {
            bool measured = _blockCount[n] > 0;
            double power = measured
                ? _blockSum[n] / _blockCount[n]
                : _blockAllSum[n] / _blockLines;
            bool carried = _blockCarried[n];

            // What the bin actually held, noise or not - the reading a burst that turns out to
            // have been a wrong floor rather than a transmission is snapped to.
            _blockPower[n] = _blockAllSum[n] / _blockLines;
            _blockSum[n] = 0;
            _blockCount[n] = 0;
            _blockAllSum[n] = 0;
            _blockCarried[n] = false;

            if (_seedBlocksRemaining > 0)
            {
                _floor[n] = Math.Max(_floor[n], Math.Max(power, 1e-12));
                continue;
            }

            if (!measured && carried)
            {
                continue;   // a transmission is sitting on this bin; it says nothing about noise
            }

            double rate = power < _floor[n] ? DownRate : measured ? UpRate : StuckUpRate;
            _floor[n] = Math.Max(_floor[n] + ((power - _floor[n]) * rate), 1e-12);
        }

        if (_seedBlocksRemaining > 0)
        {
            _seedBlocksRemaining--;
        }

        _blockFilled = 0;
    }

    /// <summary>Which bins the bursts still open are sitting on - the bins whose floors are
    /// held still, because they are carrying a transmission rather than measuring noise.</summary>
    private void MarkOpenBins()
    {
        Array.Clear(_inBurst);
        foreach (Open open in _open)
        {
            for (int n = open.LowBin; n < open.HighBin; n++)
            {
                _inBurst[n] = true;
            }
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
            // Overlapping in frequency on consecutive lines is the same transmission. Edges
            // wander by a bin or two as a signal fades, so overlap - not equality - is what
            // keeps a burst whole.
            Open? match = null;
            foreach (Open candidate in _open)
            {
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
            if (timedOut)
            {
                // Nothing this long is a transmission, so the floor that was held still under
                // it for the whole of it was being held for nothing - and if the reason it
                // stayed hot was that its floor was too low, holding it is what kept it there.
                // What the block measured is the best reading of that part of the spectrum
                // there is, so the floor takes it and the bin starts again from the truth.
                for (int n = open.LowBin; n < open.HighBin; n++)
                {
                    _floor[n] = Math.Max(_blockPower[n], 1e-12);
                }
            }

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
