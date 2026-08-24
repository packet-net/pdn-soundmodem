using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Survey;

/// <summary>How the survey is set up. Everything here is policy; the measuring is not.</summary>
public sealed class SignalSurveyOptions
{
    /// <summary>Where captures are written.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Byte budget for that directory; oldest captures are pruned to fit.</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Most captures in any rolling hour. A busy unclaimed channel must not drown the
    /// interesting ones, and a station left running for a week must not fill a Pi's card in an
    /// afternoon.</summary>
    public int MaxPerHour { get; set; } = 30;

    /// <summary>How long the same part of the spectrum is left alone after a capture. One
    /// station rag-chewing on an unclaimed frequency is one discovery, not two hundred.</summary>
    public double CooldownSeconds { get; set; } = 120;

    /// <summary>Width of the "same part of the spectrum" for that cooldown.</summary>
    public double CooldownBucketHz { get; set; } = 250;

    /// <summary>Audio kept either side of the burst. Enough for a decoder to acquire on the
    /// lead-in it needs, and to prove what the channel was doing before and after.</summary>
    public double MarginSeconds { get; set; } = 1.0;

    /// <summary>How long a Missed capture waits for a decode to claim it before it is
    /// written. A deep fade splits one transmission into fragments at the burst detector
    /// (its grace is 0.2 s; CCIR-class fades run longer), and the frame's decode lands at
    /// the END of the transmission - measured 0.6 to 3.2 s after such a fragment closed on
    /// the 2026-08-16 by-ear session, where 9 of the day's 11 in-slot "misses" were leading
    /// fragments of transmissions the station went on to decode, fingerprinted to their
    /// stations by carrier offset. Five seconds covers the slot's real frames (a 118-byte
    /// beacon is ~4.3 s of wire at 300 Bd) without holding genuine misses back meaningfully.
    /// </summary>
    public double DecodeClaimSeconds { get; set; } = 5.0;

    /// <summary>Widest burst still plausibly a packet. Above this is an SSB over or a wideband
    /// data mode this station has no business capturing at 12 kHz.</summary>
    public double MaxWidthHz { get; set; } = 3000;

    /// <summary>Longest burst still plausibly a packet.</summary>
    public double MaxSeconds { get; set; } = 20;

    /// <summary>Weakest burst worth keeping. Below this a capture is mostly noise and a
    /// classifier will not get anywhere with it either.</summary>
    public double MinPeakSnrDb { get; set; } = 6;

    /// <summary>Rig dial, for the RF figure in the sidecar; 0 = audio frequencies only.</summary>
    public double DialFrequencyHz { get; set; }

    /// <summary>"usb" or "lsb", for the same.</summary>
    public string Sideband { get; set; } = "usb";

    /// <summary>Which verdicts are worth writing out.</summary>
    public IReadOnlyList<SurveyVerdict> Capture { get; set; } =
        [SurveyVerdict.Unclaimed, SurveyVerdict.Missed, SurveyVerdict.Unattributed];

    /// <summary>Clock, injected for tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Watches the whole passband for transmissions the station cannot read, and keeps the ones
/// worth looking at later.
/// </summary>
/// <remarks>
/// <para>
/// A station only knows about signals it was configured to decode; everything else paints on the
/// waterfall and is lost. This turns "something went past and I will never know what" into a WAV
/// and a sidecar. Three things are worth keeping, and the second is the most valuable:
/// </para>
/// <list type="bullet">
/// <item><description><b>Unclaimed</b> - packet-shaped energy outside every configured band.
/// Nobody was listening there.</description></item>
/// <item><description><b>Missed</b> - packet-shaped energy <em>inside</em> a band, with nothing
/// decoded. The station was listening and could not read it, which is a receiver problem rather
/// than a coverage one, and is invisible today unless somebody happens to be recording.</description></item>
/// <item><description><b>Unattributed</b> - a frame that decoded but carried no readable AX.25
/// addresses. The bytes are in the frame log already; the audio goes beside them so the
/// modulation can be re-examined against the payload.</description></item>
/// </list>
/// <para>
/// <b>Threading.</b> <see cref="AddAudio"/>, <see cref="AddLine"/> and <see cref="NoteDecode"/>
/// are all called from the receive path and are not thread-safe with respect to each other. Only
/// the disk write leaves that thread, and it goes to <see cref="BurstCaptureWriter"/>'s
/// background writer - the receive path copies a burst out of the ring and returns.
/// </para>
/// </remarks>
public sealed class SignalSurvey : IDisposable
{
    private readonly SignalSurveyOptions _options;
    private readonly IReadOnlyList<ModemBand> _bands;
    private readonly SpectralBurstDetector _detector;
    private readonly AudioRingBuffer _ring;
    private readonly BurstCaptureWriter _writer;
    private readonly int _sampleRate;
    private readonly int _linesPerSecond;
    private readonly int _marginSamples;
    private readonly int _claimSamples;
    private readonly int _claimLines;
    private readonly long[] _lineSamples;      // line index → samples written when it arrived
    private readonly List<Decode> _decodes = [];
    private readonly List<DateTimeOffset> _recent = [];
    private readonly Dictionary<int, DateTimeOffset> _cooldown = [];
    private readonly string[] _modemNames;
    private readonly List<PendingCapture> _pending = [];
    private long _lastLine = -1;
    private long _skippedForBudget;
    private long _claimedByLaterDecode;
    private long _holes;
    private int _silence;
    private int _resetPending;

    /// <summary>Creates a survey over <paramref name="bands"/>, writing captures per
    /// <paramref name="options"/>.</summary>
    /// <param name="options">Policy - where, how much, what counts.</param>
    /// <param name="bands">The modems the station is running, so a burst can be told from a slot.</param>
    /// <param name="sampleRate">Channel DSP rate.</param>
    /// <param name="binWidthHz">Spectrum bin width of the lines that will be fed in.</param>
    /// <param name="linesPerSecond">Their line rate.</param>
    /// <param name="lineLength">Bins per line.</param>
    /// <param name="writer">Capture sink; one is created over the options' directory if null.</param>
    public SignalSurvey(
        SignalSurveyOptions options,
        IReadOnlyList<ModemBand> bands,
        int sampleRate,
        double binWidthHz,
        int linesPerSecond,
        int lineLength,
        BurstCaptureWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _options = options;
        _bands = bands;
        _sampleRate = sampleRate;
        _linesPerSecond = linesPerSecond;
        _marginSamples = (int)(options.MarginSeconds * sampleRate);
        _claimSamples = (int)(options.DecodeClaimSeconds * sampleRate);
        _claimLines = (int)Math.Round(options.DecodeClaimSeconds * linesPerSecond);
        _writer = writer ?? new BurstCaptureWriter(
            options.Directory, options.MaxBytes, options.TimeProvider);

        _detector = new SpectralBurstDetector(
            binWidthHz, linesPerSecond, lineLength, OnBurst,
            maxSeconds: options.MaxSeconds * 2);   // long enough to see and reject an over-runner

        // The longest thing capturable, plus both margins, plus the Missed claim window it may
        // sit through, plus a second of slack so a burst is still in the ring when its closing
        // grace period expires.
        int ringSeconds = (int)Math.Ceiling(
            (options.MaxSeconds * 2) + (2 * options.MarginSeconds) + options.DecodeClaimSeconds + 1);
        _ring = new AudioRingBuffer(ringSeconds * sampleRate);

        // A line's audio position has to be remembered rather than computed: the line clock stops
        // while the station transmits and the audio clock does not, so any fixed relationship
        // between them is wrong the first time the operator keys up.
        _lineSamples = new long[Math.Max(64, ringSeconds * linesPerSecond * 2)];
        Array.Fill(_lineSamples, -1);

        var names = new string[bands.Count];
        for (int i = 0; i < bands.Count; i++)
        {
            names[i] = $"{bands[i].SubChannel}:{bands[i].Mode}";
        }

        _modemNames = names;
    }

    /// <summary>Captures written.</summary>
    public long Captured => _writer.Written;

    /// <summary>Bytes the capture directory holds, against the budget.</summary>
    public long Bytes => _writer.Bytes;

    /// <summary>Captures dropped because the disk could not keep up, or would not take
    /// them. Failures, distinct from <see cref="SkippedForBudget"/>'s deliberate refusals;
    /// 0 on a healthy station.</summary>
    public long DroppedCaptures => _writer.Dropped;

    /// <summary>Where captures are written.</summary>
    public string Directory => _options.Directory;

    /// <summary>
    /// Raised whenever the counts move - a capture kept, or one the budget refused. What a
    /// station is skipping is invisible otherwise, and a week of unattended collection that
    /// quietly became a sample rather than the set is worse than one that says so.
    /// </summary>
    public event Action? StatusChanged;

    /// <summary>Raised once a capture is on disk, with the path of its audio.</summary>
    public event Action<BurstCapture, string>? CaptureWritten
    {
        add => _writer.Captured += value;
        remove => _writer.Captured -= value;
    }

    /// <summary>Bursts worth keeping that were not, because a budget said no. Non-zero means the
    /// station is seeing more than it was told to record, which is worth an operator knowing.</summary>
    public long SkippedForBudget => Interlocked.Read(ref _skippedForBudget);

    /// <summary>Missed-verdict captures cancelled because a decode arrived inside
    /// <see cref="SignalSurveyOptions.DecodeClaimSeconds"/> and claimed the burst as part of
    /// its own transmission - fade-split fragments, not misses. The count is how often the
    /// claim window is earning its keep.</summary>
    public long ClaimedByLaterDecode => Interlocked.Read(ref _claimedByLaterDecode);

    /// <summary>Feeds channel audio. Must be the same audio the spectrum lines are made from,
    /// and must be gated the same way - nothing is surveyed while the station transmits.</summary>
    /// <remarks>
    /// Watched for holes as it goes: see <see cref="SilenceSamples"/>.
    /// </remarks>
    public void AddAudio(ReadOnlySpan<float> samples)
    {
        NoteSilence(samples);
        _ring.Write(samples);
        if (_pending.Count > 0)
        {
            DrainPending(flush: false);
        }
    }

    /// <summary>
    /// How long a run of exactly-zero samples is a hole in the audio rather than a quiet channel.
    /// Ten milliseconds.
    /// </summary>
    /// <remarks>
    /// A receiver does not deliver silence. Even a dead band arrives as noise, and noise does not
    /// land on exactly zero for a hundred samples running - so a run this long is a hole in the
    /// stream, whatever put it there.
    /// </remarks>
    public const int SilenceSamples = 120;

    /// <summary>Holes seen in the audio - runs of at least <see cref="SilenceSamples"/> exact
    /// zeros. Non-zero on a healthy station means its own transmissions are reaching here, or
    /// its capture device is dropping out.</summary>
    public long Holes => Interlocked.Read(ref _holes);

    /// <summary>
    /// Watches for a hole in the audio, and abandons everything in flight when one turns up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The station's own transmission is the common cause and it was not being caught. Receive is
    /// gated while <c>_transmitting</c> is set, so the tap is fed nothing at all and the line
    /// clock stops with it, which is the invariant this class is built on. But the <em>radio's</em>
    /// receive audio does not come back the instant the daemon clears that flag: a Flex's DAX
    /// stream stays muted a little longer, so what reaches this method is real samples that are
    /// exactly zero, followed by a step back to full audio. The line clock never stopped, so the
    /// gap check does not see it; <see cref="Reset"/> is wired to the keyed edge, so nothing has
    /// reset by then. What is left is a step discontinuity - broadband, one transform window
    /// long - which opens a burst.
    /// </para>
    /// <para>
    /// Measured on the live 40 m station: a capture at 16:52:16 holds 2,051 consecutive zero
    /// samples (170.9 ms), the audio cut off mid-waveform at amplitude 885, and the journal
    /// carries <c>tx[2] bpsk300</c> at that very second. Across 1,309 sampled captures,
    /// <b>12% hold at least 20 ms of exact zeros</b>, 71 of them inside the receiver's own
    /// passband and 85 outside - so this is not a filter-edge effect and not confined to the
    /// empty part of the spectrum.
    /// </para>
    /// <para>
    /// Testing the samples rather than the PTT is deliberate: it catches a device underrun and a
    /// dropped DAX packet on the same rule, and it needs no agreement with the transmitter about
    /// when its audio really stops.
    /// </para>
    /// </remarks>
    private void NoteSilence(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            if (sample == 0)
            {
                if (++_silence == SilenceSamples)
                {
                    Interlocked.Increment(ref _holes);
                    Reset();
                }
            }
            else
            {
                _silence = 0;
            }
        }
    }

    /// <summary>Feeds one spectrum line.</summary>
    public void AddLine(long lineIndex, ReadOnlySpan<byte> line)
    {
        ApplyPendingReset();
        _lastLine = lineIndex;
        _lineSamples[(int)(((lineIndex % _lineSamples.Length) + _lineSamples.Length) % _lineSamples.Length)]
            = _ring.Written;
        _detector.AddLine(lineIndex, line);
        TrimDecodes(lineIndex);
    }

    /// <summary>
    /// Records that a modem decoded a frame, which is what tells a burst inside a configured band
    /// from one the station failed to read. A frame whose AX.25 addresses will not parse is noted
    /// as such, and its bytes travel with the capture.
    /// </summary>
    /// <remarks>
    /// Fed from the monitor path (<see cref="Channel.SoundModemChannel.FrameReceivedWithQuality"/>), so a
    /// frame the station read and did not pass to its host counts here as a decode: its burst is
    /// marked <c>Decoded</c> and is no longer captured as <c>Missed</c>. That is the wanted answer
    /// rather than an accident of the wiring. The survey's question is "did this station read that
    /// burst", not "did a host get it", and a plain-IL2P neighbour was exactly what filled the
    /// 40 m station's survey with unreadable captures - a badged row in the panel naming the
    /// sender answers the operator's question, where another WAV of the same beacon every ten
    /// minutes only spends the capture budget that the next unexplained burst needs.
    /// </remarks>
    /// <param name="subChannel">The modem that decoded it.</param>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="quality">The modem's own report of the decode.</param>
    /// <param name="ax25">Whether the frame is AX.25 at all. False for a waveform that carries
    /// something else - ARDOP's are Winlink sessions and ID frames, not AX.25 - where asking
    /// whether the first fourteen bytes are shifted callsigns is a question about a different
    /// protocol, and a complaint that they are not would file a perfectly good decode under
    /// "unattributed". Such a frame is simply one the station read.</param>
    public void NoteDecode(
        int subChannel, ReadOnlySpan<byte> frame, Modems.FrameQuality quality, bool ax25 = true)
    {
        ApplyPendingReset();
        if (_lastLine < 0)
        {
            return;   // no line clock yet; nothing to attach it to
        }

        // Worked out here, once, rather than left for somebody to reconstruct from a payload blob
        // later: the station noticed something it could not explain, so it writes down what it
        // noticed. A frame that decoded and then would not yield callsigns has already passed
        // Reed-Solomon and the trailing CRC - the bits are right and the reading of them is not,
        // which is the whole diagnosis and is lost by the time anyone opens the file.
        string? note = ax25 ? Waterfall.Ax25AttributionNote.For(frame) : null;
        _decodes.Add(new Decode(
            _lastLine,
            subChannel,
            quality.Mode,
            note is null,
            note is null ? null : Convert.ToHexString(frame),
            quality.HeaderType?.ToString(),
            note));
    }

    /// <summary>Abandons everything in flight. Called when the station keys up: a burst that
    /// straddles our own transmission is not a measurement of anybody else's.</summary>
    /// <remarks>
    /// Callable from any thread - the keyup announcement arrives on the transmitter's, and
    /// the audio thread can be mid-block in the receive tap at that very moment. Nothing is
    /// mutated here: a flag is set, and the receive path applies it before the next line or
    /// decode it processes. Receive is gated for the length of the transmission, so the state
    /// is swept exactly once, before any post-keyup audio is examined - same effect as an
    /// immediate reset, without two threads in the unguarded lists.
    /// </remarks>
    public void Reset() => Volatile.Write(ref _resetPending, 1);

    private void ApplyPendingReset()
    {
        if (Interlocked.Exchange(ref _resetPending, 0) == 1)
        {
            // Captures waiting on their trailing margin are written now, honestly short:
            // the audio that would have completed the margin is on the far side of our own
            // transmission, and splicing post-keyup audio directly onto a pre-keyup burst
            // would manufacture context that never happened.
            DrainPending(flush: true);
            _detector.Reset();
            _decodes.Clear();
            _lastLine = -1;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // A capture still waiting on its margin at shutdown ships short rather than not at all.
        DrainPending(flush: true);
        _writer.Dispose();
    }

    private void OnBurst(SurveyBurst burst)
    {
        SurveyVerdict verdict = Triage(burst, out Decode? decode);
        if (!_options.Capture.Contains(verdict))
        {
            return;
        }

        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        if (!Allowed(burst, now))
        {
            Interlocked.Increment(ref _skippedForBudget);
            StatusChanged?.Invoke();
            return;
        }

        long startSample = SampleAt(burst.StartLine);
        long endSample = SampleAt(burst.EndLine);
        if (startSample < 0 || endSample < 0 || endSample <= startSample)
        {
            Interlocked.Increment(ref _skippedForBudget);
            StatusChanged?.Invoke();
            return;
        }

        // Queued, not written: a burst closes one grace period (~0.2 s) after its last hot
        // line, so writing at verdict time ships every capture with a truncated trailing
        // margin - measured on the 2026-08-16 v0.37.0 rollout, where every miss carried its
        // full 1 s lead-in and ~0.2 s of tail. The capture waits in _pending until the ring
        // has recorded the full trailing margin (AddAudio drains it); a keyup or shutdown
        // flushes early, honestly short. Sample extents are resolved here, while the line
        // clock still holds them.
        _pending.Add(new PendingCapture(
            burst.StartLine,
            burst.EndLine,
            new BurstCapture(
                now,
                verdict,
                burst.CentreHz,
                burst.LowHz,
                burst.HighHz,
                burst.WidthHz,
                // The burst's own length, not the WAV's less its margins: when the trailing
                // margin has not been recorded yet the file is short, and subtracting a margin
                // that was never written reported a burst as lasting −0.2 seconds.
                Math.Round((double)burst.Lines / _linesPerSecond, 3),
                Math.Round(burst.PeakSnrDb, 1),
                Math.Round(burst.MeanSnrDb, 1),
                RfOf(burst.CentreHz),
                _options.DialFrequencyHz == 0 ? null : _options.DialFrequencyHz,
                _options.DialFrequencyHz == 0 ? null : _options.Sideband,
                _sampleRate,
                _modemNames,
                decode?.SubChannel,
                decode?.Mode,
                decode?.FrameHex,
                decode?.HeaderType,
                decode?.AttributionNote),
            startSample,
            endSample));
        DrainPending(flush: false);   // a zero margin (or a generous grace) may already be satisfied
    }

    /// <summary>Writes every queued capture whose trailing margin the ring now holds - or,
    /// when <paramref name="flush"/> is set, everything, clamped to the audio that exists. A
    /// Missed capture additionally waits out <see cref="SignalSurveyOptions.DecodeClaimSeconds"/>
    /// and is re-checked against the decodes before writing: the attribution window Triage
    /// uses runs one second past the burst, but a fade-split fragment's decode lands at the
    /// END of its transmission, seconds later - so the claim is re-asked when the answer can
    /// actually be known.</summary>
    private void DrainPending(bool flush)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            PendingCapture pending = _pending[i];
            bool missed = pending.Capture.Verdict == SurveyVerdict.Missed;
            long hold = missed ? Math.Max(_marginSamples, _claimSamples) : _marginSamples;
            if (!flush && _ring.Written < pending.EndSample + hold)
            {
                continue;
            }

            _pending.RemoveAt(i--);
            if (missed && ClaimedByDecode(pending))
            {
                Interlocked.Increment(ref _claimedByLaterDecode);
                StatusChanged?.Invoke();
                continue;
            }
            long from = Math.Max(0, pending.StartSample - _marginSamples);
            long to = Math.Min(pending.EndSample + _marginSamples, _ring.Written);
            var buffer = new float[(int)(to - from)];
            if (!_ring.TryCopy(from, buffer))
            {
                // Aged out of the ring; better nothing than the wrong audio. The ring is
                // sized to hold a maximum burst plus both margins with slack, so this is a
                // failure worth counting, not a policy refusal.
                Interlocked.Increment(ref _skippedForBudget);
                StatusChanged?.Invoke();
                continue;
            }

            _writer.Write(pending.Capture, buffer);
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Whether a decode has arrived that claims this Missed burst as a fragment of
    /// its own transmission - Triage's attribution rule, re-asked with the window a
    /// transmission-end decode actually needs.</summary>
    private bool ClaimedByDecode(PendingCapture pending)
    {
        foreach (Decode candidate in _decodes)
        {
            if (candidate.Line >= pending.StartLine
                && candidate.Line <= pending.EndLine + Math.Max(30, _claimLines))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A triaged, budget-approved capture waiting for its trailing margin to be
    /// recorded - and, for a Missed verdict, for the decode claim window to pass - before it
    /// is written.</summary>
    private sealed record PendingCapture(
        long StartLine, long EndLine, BurstCapture Capture, long StartSample, long EndSample);

    /// <summary>
    /// Decides what a closed burst is. The order matters: shape first, because an SSB over that
    /// happens to sit outside every band is not a discovery.
    /// </summary>
    private SurveyVerdict Triage(SurveyBurst burst, out Decode? decode)
    {
        decode = null;

        // Duration is the discriminator that width is not. A voice over and a wide data burst
        // occupy much the same 2.4-3 kHz, so width alone cannot separate them - but an over runs
        // for tens of seconds and the longest frame these modes can carry does not.
        double seconds = (double)burst.Lines / _linesPerSecond;
        if (burst.EndedOnTimeout
            || seconds > _options.MaxSeconds
            || burst.WidthHz > _options.MaxWidthHz
            || burst.PeakSnrDb < _options.MinPeakSnrDb)
        {
            return SurveyVerdict.NotAPacket;
        }

        Decode? attributed = null;
        foreach (Decode candidate in _decodes)
        {
            // A frame is delivered at the end of the burst that carried it, and the deframer
            // takes a moment more, so the window runs a little past the burst's own end.
            if (candidate.Line < burst.StartLine || candidate.Line > burst.EndLine + 30)
            {
                continue;
            }

            if (!candidate.Attributed)
            {
                decode = candidate;
                return SurveyVerdict.Unattributed;
            }

            attributed ??= candidate;
        }

        if (attributed is not null)
        {
            decode = attributed;
            return SurveyVerdict.Decoded;
        }

        foreach (ModemBand band in _bands)
        {
            if (burst.CentreHz >= band.LowHz && burst.CentreHz <= band.HighHz)
            {
                decode = new Decode(0, band.SubChannel, band.Mode, true, null, null, null);
                return SurveyVerdict.Missed;
            }
        }

        return SurveyVerdict.Unclaimed;
    }

    /// <summary>Rate limit and per-frequency cooldown. Disk is the writer's budget; this one is
    /// about not burying the interesting captures under a hundred copies of the dull one.</summary>
    private bool Allowed(SurveyBurst burst, DateTimeOffset now)
    {
        for (int i = _recent.Count - 1; i >= 0; i--)
        {
            if (now - _recent[i] > TimeSpan.FromHours(1))
            {
                _recent.RemoveAt(i);
            }
        }

        if (_recent.Count >= _options.MaxPerHour)
        {
            return false;
        }

        int bucket = (int)(burst.CentreHz / _options.CooldownBucketHz);
        if (_cooldown.TryGetValue(bucket, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(_options.CooldownSeconds))
        {
            return false;
        }

        _cooldown[bucket] = now;
        _recent.Add(now);
        return true;
    }

    private long SampleAt(long lineIndex)
    {
        if (lineIndex < 0 || _lastLine - lineIndex >= _lineSamples.Length)
        {
            return -1;
        }

        return _lineSamples[(int)(((lineIndex % _lineSamples.Length) + _lineSamples.Length) % _lineSamples.Length)];
    }

    private double? RfOf(double audioHz) =>
        _options.DialFrequencyHz == 0
            ? null
            : string.Equals(_options.Sideband, "lsb", StringComparison.OrdinalIgnoreCase)
                ? _options.DialFrequencyHz - audioHz
                : _options.DialFrequencyHz + audioHz;

    private void TrimDecodes(long lineIndex)
    {
        for (int i = _decodes.Count - 1; i >= 0; i--)
        {
            if (lineIndex - _decodes[i].Line > _lineSamples.Length)
            {
                _decodes.RemoveAt(i);
            }
        }
    }

    private sealed record Decode(
        long Line,
        int SubChannel,
        string Mode,
        bool Attributed,
        string? FrameHex,
        string? HeaderType,
        string? AttributionNote);
}
