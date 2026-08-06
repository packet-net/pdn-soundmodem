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
    private readonly long[] _lineSamples;      // line index → samples written when it arrived
    private readonly List<Decode> _decodes = [];
    private readonly List<DateTimeOffset> _recent = [];
    private readonly Dictionary<int, DateTimeOffset> _cooldown = [];
    private readonly string[] _modemNames;
    private long _lastLine = -1;
    private long _skippedForBudget;

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
        _writer = writer ?? new BurstCaptureWriter(
            options.Directory, options.MaxBytes, options.TimeProvider);

        _detector = new SpectralBurstDetector(
            binWidthHz, linesPerSecond, lineLength, OnBurst,
            maxSeconds: options.MaxSeconds * 2);   // long enough to see and reject an over-runner

        // The longest thing capturable, plus both margins, plus a second of slack so a burst is
        // still in the ring when its closing grace period expires.
        int ringSeconds = (int)Math.Ceiling((options.MaxSeconds * 2) + (2 * options.MarginSeconds) + 1);
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

    /// <summary>Feeds channel audio. Must be the same audio the spectrum lines are made from,
    /// and must be gated the same way - nothing is surveyed while the station transmits.</summary>
    public void AddAudio(ReadOnlySpan<float> samples) => _ring.Write(samples);

    /// <summary>Feeds one spectrum line.</summary>
    public void AddLine(long lineIndex, ReadOnlySpan<byte> line)
    {
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
    public void Reset()
    {
        _detector.Reset();
        _decodes.Clear();
        _lastLine = -1;
    }

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

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

        if (!TryCopyAudio(burst, out float[]? audio))
        {
            Interlocked.Increment(ref _skippedForBudget);
            StatusChanged?.Invoke();
            return;
        }

        _writer.Write(
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
            audio);
        StatusChanged?.Invoke();
    }

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

    private bool TryCopyAudio(SurveyBurst burst, out float[] audio)
    {
        audio = [];
        long start = SampleAt(burst.StartLine);
        long end = SampleAt(burst.EndLine);
        if (start < 0 || end < 0 || end <= start)
        {
            return false;
        }

        long from = Math.Max(0, start - _marginSamples);
        long to = end + _marginSamples;
        if (to > _ring.Written)
        {
            to = _ring.Written;   // the trailing margin has not been captured yet
        }

        int count = (int)(to - from);
        if (count <= 0)
        {
            return false;
        }

        var buffer = new float[count];
        if (!_ring.TryCopy(from, buffer))
        {
            return false;   // aged out of the ring; better nothing than the wrong audio
        }

        audio = buffer;
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
