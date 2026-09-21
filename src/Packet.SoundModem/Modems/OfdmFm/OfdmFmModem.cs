using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// OFDM-FM as a pdn-soundmodem <see cref="IModem"/>: audio in, AX.25 frames out, and back.
/// </summary>
/// <remarks>
/// <para><b>The payload is one AX.25 frame, opaque, and there is no IL2P.</b> The burst already
/// supplies sync, length, a CRC and forward error correction, so IL2P would spend rate a second
/// time for guarantees this waveform already holds - and its Reed-Solomon layer cannot use the
/// soft information the equalised constellation produces, which is where most of the coding gain
/// here comes from.</para>
/// <para><b>The work is the streaming adapter.</b> <see cref="OfdmFmBurstCodec"/> is whole-buffer:
/// hand it audio and it finds and decodes a burst. <see cref="IModem.Process"/> is a stream, at
/// whatever block size the sound card feels like. Accumulating the stream and re-running a
/// whole-buffer search over it would work on a bench and nowhere else: the search is O(N) per
/// start position, so re-running it costs quadratically, and the buffer would grow without limit
/// on a channel that never goes quiet. So this holds a bounded window, runs the same
/// self-correlation metric incrementally as samples arrive, and once it has a sync position calls
/// straight into the codec's <see cref="OfdmFmBurstCodec.DecodeAt(ReadOnlySpan{float}, int)"/> - the same decode the
/// whole-buffer path uses, so the two cannot drift into disagreeing.</para>
/// </remarks>
public sealed class OfdmFmModem : IModem
{
    /// <summary>
    /// The longest payload this modem carries, either way: everything the burst header can name.
    /// </summary>
    /// <remarks>
    /// <para>It was 2048, chosen when the header's length field allowed 65535 and a length field
    /// is a number the channel made up: a burst announcing 60 kB would have been a 60 kB buffer
    /// allocated on the strength of noise. The header's field is 12 bits now, so what it can
    /// announce is bounded by the wire format itself, and a receiver may as well assemble all of
    /// it.</para>
    /// <para>It should carry all of it, because frame length is the biggest throughput lever
    /// this waveform has: four fixed symbols per burst before any payload, so on the 8 kHz
    /// preset a 1024-byte frame delivers 16.3 kbit/s, a 1900-byte one 19.3, and the arithmetic
    /// for 4000 says 24 (docs/dev/ofdm-fm/preset-design.md). Whether the arithmetic survives a
    /// burst that long on air, with the sample clocks drifting under it, is what raising this
    /// is for finding out. The KISS layer of the daemon this plugs into passes frames to 8192
    /// bytes since pdn-soundmodem 0.72.0; before that it dropped anything over 2048 silently.
    /// </para>
    /// </remarks>
    public const int MaxPayloadBytes = OfdmFmBurstCodec.MaxPayloadBytes;

    private readonly OfdmFmBurstCodec _codec;
    private readonly OfdmFmParameters _parameters;
    private readonly string _mode;
    private readonly Action<byte[]> _frameReceived;
    private readonly EnergyBusyDetector _energy;
    private readonly FmQuietingBusyDetector _quieting;
    private readonly IChannelBusySource? _externalBusy;

    /// <summary>The metric window: the two half-symbols being correlated, after the prefix.</summary>
    private readonly int _half;
    private readonly int _windowStart;
    private readonly int _windowLength;

    /// <summary>Audio still under consideration. Compacted from the front every block, so it holds
    /// the sync search's window while hunting and exactly one burst while collecting.</summary>
    private float[] _window;

    // The same audio, band limited, for the sync search and for nothing else. Held in lockstep with
    // _window so an index means the same thing in both, allowing for the filter's group delay. See
    // SearchBandLimit for why the decode path must NOT be filtered.
    private float[] _search;
    private readonly SearchBandLimit _bandLimit;
    private int _windowCount;

    /// <summary>Samples that have left the window, so a position in it can be quoted absolutely.</summary>
    private long _discarded;

    /// <summary>The next start position to test for sync, as an index into <see cref="_window"/>.</summary>
    private int _scanAt;

    /// <summary>Running sums for the self-correlation at <see cref="_scanAt"/>: the dot product of
    /// the two halves, and each half's energy. Valid only when <see cref="_sumsValid"/>.</summary>
    private double _dot;
    private double _energyA;
    private double _energyB;
    private bool _sumsValid;

    /// <summary>
    /// Header reads allowed per unbroken run of above-threshold correlation. One is enough for a
    /// clean burst; a few cover a header that lands marginally and is retried a sample or two down
    /// its own plateau. The cap is what stops a permanently correlated signal - an unmodulated
    /// carrier is exactly that - from costing a transform per sample.
    /// </summary>
    internal const int MaxSyncAttemptsPerRun = 4;

    /// <summary>
    /// The lead-in the host asks for before every frame after the first in a keyup: pdn-soundmodem
    /// passes 30 ms there and the station's full TXDELAY before the first (its
    /// <c>SoundModemChannel</c>, where the keyup is drained). A request at or under this is how
    /// the modem knows it is inside a keyup, which is the only place a profile with
    /// <see cref="OfdmFmParameters.ContiguousBursts"/> renders no lead-in at all.
    /// </summary>
    internal const int HostLeadInWithinKeyupMs = 30;

    /// <summary>Peak tracking across the metric's plateau, which is about a prefix long.</summary>
    private bool _tracking;
    private double _peakScore;
    private int _peakAt;
    private int _trackedFor;

    /// <summary>Header reads spent on the current run of correlation, and whether they are gone.</summary>
    private int _attemptsThisRun;
    private bool _exhausted;

    private bool _collecting;
    private int _burstAt;
    private int _need;
    private OfdmFmHeader? _header;

    // Follow-on frames. After a burst decodes, the receiver expects the next frame of the keyup
    // to start where that one ended, with its header first and no sync symbol to find it by, and
    // reads it with the timing and channel the keyup state carries. A header that fails ends the
    // expectation and the sync hunt resumes from there. See OfdmFmParameters.FollowOnFrames.
    private bool _followOn;
    private OfdmFmKeyupState? _keyup;

    // What the transmitter last sent a burst on, so a frame inside a keyup goes out as a
    // follow-on only when its geometry is one the far end has already acquired in this keyup.
    private int? _lastGeometry;

    // Touched from two threads without a lock, knowingly. Process feeds Heard on the receive
    // thread while Modulate reads Transmit and Recommendation on the transmit path, and the
    // IModem contract promises nothing about their relationship. Every shared read is a reference
    // or an int, so nothing tears on the 64-bit runtimes this runs on; the worst race is one
    // burst transmitted at a rate the far end asked out of a moment earlier, which the next
    // burst's header corrects. A lock would buy consistency nothing observable pays for.
    private readonly OfdmFmRateController? _rate;
    private readonly TimeProvider _time;
    private readonly TimeSpan _recommendationLife;
    private long _reportedAt = long.MinValue;
    private long _heardAtTicks;
    // A flag rather than a zero sentinel on the timestamp. A real clock never returns zero so the
    // sentinel looked fine; a test clock starting at zero does, and the ageing then never fired at
    // all. Found by the fake clock, which is the entire argument for having one.
    private bool _hasHeard;

    /// <summary>Builds a modem for one profile.</summary>
    /// <param name="mode">The mode string to report, as the catalogue names it, e.g.
    /// <c>ofdm-fm-8k</c>. See <see cref="OfdmFmPresets.ByMode"/>.</param>
    /// <param name="parameters">The profile geometry, already at the channel's sample rate.</param>
    /// <param name="frameReceived">The decoded-frame sink.</param>
    /// <param name="timeProvider">Clock, for ageing out a correspondent's rate recommendation.
    /// Only used when the profile adapts.</param>
    /// <param name="channelBusySource">Where carrier sense comes from, when the station has
    /// something better than the audio to ask. Null means it has not configured one, and the
    /// station keeps the shipped audio behaviour with the defects named on
    /// <see cref="ChannelBusy"/>. See <see cref="ChannelBusySources"/> for the seam a host
    /// supplies one through.</param>
    /// <param name="geometryTable">The station's geometry table, which every burst's header
    /// indexes, or null for a profile running alone. See <see cref="OfdmFmGeometryTable"/>.</param>
    public OfdmFmModem(
        string mode,
        OfdmFmParameters parameters,
        Action<byte[]> frameReceived,
        TimeProvider? timeProvider = null,
        IChannelBusySource? channelBusySource = null,
        OfdmFmGeometryTable? geometryTable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(frameReceived);

        _mode = mode;
        _parameters = parameters;
        _frameReceived = frameReceived;
        _codec = new OfdmFmBurstCodec(parameters, geometryTable);
        _time = timeProvider ?? TimeProvider.System;
        _recommendationLife = TimeSpan.FromSeconds(parameters.RecommendationLifeSeconds);
        // Starting where the profile is configured, not at the bottom of the ladder. The first
        // burst of any link goes out before anything has been heard back, so a station that always
        // crawled from the bottom would open every contact slower than the operator asked for. A
        // profile whose rate is not on the ladder starts at the most robust rung instead, which is
        // the only other defensible answer.
        _rate = parameters.AdaptiveRate
            ? new OfdmFmRateController(
                Math.Max(
                    OfdmFmRateLadder.IndexOf(parameters.Constellation, parameters.Codes),
                    OfdmFmRateLadder.Slowest),
                parameters.AdaptiveTopConstellation)
            : null;

        _half = parameters.FftSize / 2;
        _windowStart = parameters.CyclicPrefix;
        _windowLength = parameters.FftSize;

        // Enough to hold the metric's window plus one arriving block before the first compaction.
        _window = new float[Math.Max(parameters.SymbolSamples * 4, 4096)];
        _search = new float[_window.Length];

        // Cut for the acquisition layout, which is where the sync symbol is, and not for this
        // profile's own layout, which on a wide profile in a table is wider than the sync symbol
        // and would let the search swallow noise it need not.
        _bandLimit = new SearchBandLimit(parameters, _codec.Acquisition);

        // The burst is short and loud against the noise floor; the detector's own 20 ms blocks and
        // hysteresis do the rest. Fed unfiltered audio, which is what this modem gets: the whole
        // occupied band is inside the passband a station is already listening to.
        //
        // Consulted, and measurably wrong on this path, which is the state of play rather than an
        // endorsement. The reason is measured
        // rather than assumed. It asserts when audio rises above an adapting noise floor. On FM the
        // floor sags to the QUIETED level during a received burst, so when the open-squelch noise
        // returns about 9 dB louder at the end of that burst it reads the return of the noise as a
        // signal: simulated against the host's own source with this bench's levels, busy for 9.6 s
        // after a 0.4 s burst and 11.0 s after a 1 s one. Ored into ChannelBusy that is a station
        // which cannot answer anybody, which is what two connected-mode sessions did here on
        // 2026-09-18 before this was understood.
        //
        // It is still built because the instrument is useful and because a future non-FM path
        // would want it. Nothing reads it.
        _energy = new EnergyBusyDetector(parameters.SampleRate);

        // Built, tested, and NOT consulted: see docs/dev/ofdm-fm/carrier-sense.md. It reads a far end's
        // carrier correctly, including the silent lead-in nothing else can see, and it stopped one
        // of the two bench stations transmitting at all, because its thresholds are absolute dBFS
        // and the two stations' levels are not the same.
        _quieting = new FmQuietingBusyDetector(parameters.SampleRate);
        _externalBusy = channelBusySource;
    }

    /// <inheritdoc/>
    public string Mode => _mode;

    /// <inheritdoc/>
    public event Action<byte[], FrameQuality>? FrameDecoded;

    /// <summary>
    /// Every burst whose header read, whether or not its payload did: what it announced, what it
    /// recommended, and the per-carrier figures. For instruments and logs, not for traffic, which
    /// is <see cref="FrameDecoded"/>. A burst whose payload failed may be reported more than once,
    /// because the receiver retries a marginal sync a sample on; a burst that copied is reported
    /// exactly once.
    /// </summary>
    public event Action<OfdmFmBurst>? BurstHeard;

    /// <summary>
    /// True from the moment the sync correlation crosses its threshold until the burst behind it
    /// has been decoded or abandoned. Deliberately not an energy test: this is "a burst of ours is
    /// on the air", which is what a station holds its own transmission off for.
    /// </summary>
    public bool CarrierDetect => _tracking || (_collecting && (!_followOn || _header is not null));

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>With an <see cref="IChannelBusySource"/> registered</b> (see
    /// <see cref="ChannelBusySources"/>) the answer is that source, ored with this modem's sync
    /// detect. The audio energy detect is dropped entirely, because on this path it is not merely
    /// redundant but harmful: see below. A source that does not know answers null and contributes
    /// nothing, so one that loses its link costs carrier sense rather than costing the station its
    /// transmitter.</para>
    /// <para><b>Without one, the shipped audio behaviour, which is known to be wrong in two
    /// ways.</b> The energy detect asserts at the END of every burst it hears on FM, for ten
    /// seconds or more, because the return of the open-squelch noise looks like a signal to a
    /// detector waiting for a rise. And neither source sees a far end's silent lead-in, which is
    /// the window a far end's TXDELAY occupies and where two stations were measured colliding
    /// head-on here on 2026-09-18. Both are measured; see docs/dev/ofdm-fm/carrier-sense.md.</para>
    /// </remarks>
    public bool ChannelBusy => _externalBusy is null
        ? CarrierDetect || _energy.Busy
        : CarrierDetect || (_externalBusy.Busy ?? false);

    /// <summary>The profile this modem runs.</summary>
    public OfdmFmParameters Parameters => _parameters;

    /// <summary>Audio currently held. The instrument for the claim that the buffer is bounded:
    /// a receiver that accumulated its stream would show this climbing forever.</summary>
    internal int BufferedSamples => _windowCount;

    /// <summary>The backing array's length, which is what the claim is really about - a buffer
    /// that is compacted but never reused would show this climbing instead.</summary>
    internal int BufferCapacity => _window.Length;

    /// <summary>
    /// Where the last decoded burst's sync symbol started, counted in samples fed in since
    /// construction - the streaming search's answer to the question
    /// <see cref="OfdmFmBurst.StartSample"/> answers for the whole-buffer path. The instrument for
    /// the two searches agreeing, which is what makes "the same audio decodes the same either way"
    /// a statement about the code rather than a coincidence on one test vector.
    /// </summary>
    internal long LastSyncAtSample { get; private set; } = -1;

    /// <inheritdoc/>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _energy.Process(sample);
            _quieting.Process(sample);
        }

        Append(samples);
        Drain();
        Compact();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// TXDELAY becomes whole silent symbols before the burst, rounded up: the receiver's
    /// acquisition needs somewhere to settle, and a partial symbol of silence is not a unit this
    /// waveform has.
    /// </remarks>
    public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds)
    {
        if (ax25Frame.Length > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"frame of {ax25Frame.Length} bytes is longer than the {MaxPayloadBytes} this "
                + "waveform carries",
                nameof(ax25Frame));
        }

        double symbolsPerMs = _parameters.SymbolRate / 1000.0;
        double asked = Math.Max(0, txDelayMilliseconds) * symbolsPerMs;

        // Whole symbols, rounded up, with at least one: the receiver's acquisition needs somewhere
        // to settle before the FIRST burst of a keyup. Between bursts of one keyup it does not,
        // and a profile that says so gets none when the host asks for the small figure it uses
        // after the first frame. See ContiguousBursts.
        bool withinKeyup = txDelayMilliseconds <= HostLeadInWithinKeyupMs;
        int leadIn = _parameters.ContiguousBursts && withinKeyup
            ? 0
            : Math.Max(1, (int)Math.Ceiling(asked));

        OfdmFmConstellation constellation = _parameters.Constellation;
        OfdmFmRate? recommendation = null;
        OfdmFmCoding? coding = null;
        int? geometry = null;
        if (_rate is not null)
        {
            ExpireStaleRecommendation();
            OfdmFmRate sending = _rate.Transmit;
            constellation = sending.Constellation;
            recommendation = _rate.Recommendation;
            coding = sending.Coding;
            geometry = _askedGeometry;
        }

        // A follow-on burst only inside a keyup, only on a geometry the far end has acquired in
        // this keyup, which is the geometry of the burst before, and only while the far end has
        // not asked for full bursts. The first frame of a keyup and the first after a change of
        // geometry go out in full.
        int carrying = geometry ?? _codec.GeometryId;
        bool askedFull = _rate?.SendFullBursts ?? false;
        bool askFull = _rate?.WantFullBursts ?? false;
        bool followOn = _parameters.FollowOnFrames && withinKeyup && _lastGeometry == carrying
            && !askedFull;
        _lastGeometry = withinKeyup || _parameters.FollowOnFrames ? carrying : null;
        return followOn
            ? _codec.ModulateFollowOn(
                ax25Frame, constellation, recommendation, coding, geometry, askFullBursts: askFull)
            : _codec.Modulate(
                ax25Frame, constellation, leadIn, recommendation, coding, geometry,
                askFullBursts: askFull);
    }

    /// <summary>Whether this modem is asking its correspondent for full bursts rather than
    /// follow-on ones, from what it has heard. False if it does not adapt.</summary>
    public bool AskingForFullBursts => _rate?.WantFullBursts ?? false;

    /// <summary>Whether this modem's correspondent has asked it for full bursts, which its next
    /// frames inside a keyup then are. False if it does not adapt.</summary>
    public bool SendingFullBursts => _rate?.SendFullBursts ?? false;

    /// <summary>What this modem is currently transmitting at, or null if it does not adapt.
    /// </summary>
    public OfdmFmRate? TransmittingAt => _rate?.Transmit;

    /// <summary>The table entry this modem's next burst will carry its payload on: what the
    /// correspondent last asked for, if it adapts and has been asked, otherwise the profile's own.
    /// </summary>
    public int TransmittingOn => _askedGeometry ?? _codec.GeometryId;

    /// <summary>What this modem is currently asking its correspondent for, or null if it does not
    /// adapt.</summary>
    public OfdmFmRate? AskingFor => _rate?.Recommendation;

    /// <summary>The station's geometry table.</summary>
    public OfdmFmGeometryTable GeometryTable => _codec.Table;

    // The geometry the correspondent asked to be sent on, kept beside the rate controller rather
    // than inside it because the ladder knows nothing of geometry. Null until asked, and again
    // when the recommendation goes stale. Only ever an id this station holds: ReadHeader drops any
    // other before it gets here.
    //
    // Following the ask is the whole of what adaptation does with geometry today. Which geometry
    // to ASK for is not yet decided from measurement - a station recommends its own - because the
    // measurement that would decide it (CarrierSnrDb, on air) is newer than this code.
    private int? _askedGeometry;

    // A recommendation describes a path, and a path that has not been heard from for a while may
    // not be the same path any more. Going back to the most robust rate costs one slow burst; going
    // on transmitting at a rate the far end asked for before it drove into a valley costs the
    // frame, and then costs it again.
    private void ExpireStaleRecommendation()
    {
        if (_rate is null || !_hasHeard)
        {
            return;
        }

        if (_time.GetElapsedTime(_heardAtTicks) > _recommendationLife)
        {
            _rate.Reset();
            _askedGeometry = null;
            _hasHeard = false;
        }
    }

    /// <inheritdoc/>
    public void ResetCarrierState()
    {
        _discarded += _windowCount;
        _windowCount = 0;
        _scanAt = 0;
        _sumsValid = false;
        _tracking = false;
        _trackedFor = 0;
        _attemptsThisRun = 0;
        _exhausted = false;
        _collecting = false;
        _header = null;
        _followOn = false;
        _keyup = null;
        _energy.Reset();
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        Grow(_windowCount + samples.Length);
        samples.CopyTo(_window.AsSpan(_windowCount));
        samples.CopyTo(_search.AsSpan(_windowCount));
        _bandLimit.Process(_search.AsSpan(_windowCount, samples.Length));
        _windowCount += samples.Length;
    }

    private void Grow(int needed)
    {
        if (needed <= _window.Length)
        {
            return;
        }

        // Bounded by the longest burst this receiver will assemble, plus the block that arrived
        // with it. Nothing here grows with how long the channel has been open.
        int capacity = _window.Length;
        while (capacity < needed)
        {
            capacity *= 2;
        }

        Array.Resize(ref _window, capacity);
        Array.Resize(ref _search, capacity);
    }

    /// <summary>
    /// Consumes as much of the window as it can: hunting for sync, then collecting and decoding a
    /// burst, then hunting again from just past it.
    /// </summary>
    private void Drain()
    {
        while (true)
        {
            if (_collecting)
            {
                if (!Collect())
                {
                    return;
                }

                continue;
            }

            if (!Hunt())
            {
                return;
            }
        }
    }

    /// <summary>
    /// Advances the sync search. Returns true when it has committed to a burst position, false
    /// when it has run out of samples.
    /// </summary>
    private bool Hunt()
    {
        while (_scanAt + _windowStart + _windowLength <= _windowCount)
        {
            double score = Metric(_scanAt);
            _scanAt++;

            if (score < OfdmFmBurstCodec.SyncThreshold)
            {
                // The correlation has broken while nothing was being tracked: whatever was up
                // there is over, so the budget below resets and the next thing to cross the
                // threshold gets a fresh set of attempts.
                //
                // NOT while tracking. Falling off a plateau while tracking is the commit path
                // below, and resetting the budget on the way to it made the budget count to one:
                // every retry after a failed payload resumes a sample on, still on the same
                // plateau, tracks to its end, falls off, reset, commits. Measured on a 1900-byte
                // QAM-64 burst on air, 2026-09-19: a burst whose payload failed was decoded 140
                // times over before the search walked off its plateau, which on a Pi is a second
                // of the receive thread spent on one burst while the next ones went by unheard.
                if (!_tracking)
                {
                    _attemptsThisRun = 0;
                    _exhausted = false;
                    continue;
                }
            }
            else
            {
                if (_exhausted)
                {
                    // Spent this run's attempts. Keep sliding the metric - which is a constant
                    // handful of multiplies a sample - and commit to nothing until the correlation
                    // breaks. Without this a signal that simply stays correlated, which is what an
                    // unmodulated carrier sitting on the channel IS, would commit at every sample,
                    // read a header at every sample, and put a transform per sample on the receive
                    // thread. That does not fall behind gracefully; it falls behind for as long as
                    // somebody holds a PTT down.
                    continue;
                }

                if (!_tracking || score > _peakScore)
                {
                    _peakScore = score;
                    _peakAt = _scanAt - 1;
                }

                _tracking = true;
                _trackedFor++;

                // The metric plateaus for about a prefix, because the cyclic prefix makes the
                // whole transmitted sync symbol periodic over half a transform. Keep going to the
                // top of that plateau rather than committing to its leading edge - but not
                // forever, because a signal that stays correlated would never resolve.
                if (_trackedFor <= _parameters.SymbolSamples)
                {
                    continue;
                }
            }

            // Committing costs a header read, so a run of correlated samples gets a few attempts
            // and no more. A real burst needs one, or a handful when a header lands marginally and
            // the search steps down its own plateau retrying.
            _exhausted = ++_attemptsThisRun >= MaxSyncAttemptsPerRun;

            // Fallen off the plateau (or run out of patience): the peak is where the burst starts.
            _collecting = true;

            // _peakAt indexes the filtered audio, which lags the raw by the filter's group delay,
            // so the burst starts that many samples earlier in the audio the decoder reads.
            _burstAt = Math.Max(0, _peakAt - _bandLimit.Delay);
            _need = _codec.HeaderEndOffset;
            _header = null;
            _tracking = false;
            _trackedFor = 0;
            _sumsValid = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collects a committed burst. Returns true when it has finished with it - decoded, or given
    /// up and gone back to hunting - and false while it is still waiting for samples.
    /// </summary>
    private bool Collect()
    {
        if (_windowCount - _burstAt < _need)
        {
            return false;
        }

        if (_followOn)
        {
            return CollectFollowOn();
        }

        if (_header is null)
        {
            // The header is in: it says what the rest of the burst looks like, and therefore how
            // much more to wait for. A burst that will not fit is abandoned here, before anything
            // is sized from it.
            OfdmFmHeader? read = _codec.ReadHeader(_window.AsSpan(0, _windowCount), _burstAt);
            if (read is not OfdmFmHeader header || header.PayloadLength > MaxPayloadBytes)
            {
                // A false sync, or a header the noise got to. Resume the search from one sample
                // past where it committed: nothing is discarded, so a real burst starting inside
                // what was collected is still found.
                return Abandon(SearchPositionOf(_burstAt) + 1);
            }

            _header = header;
            _need = _codec.BurstSamples(header);
            return true;
        }

        OfdmFmBurst? burst = _codec.DecodeAt(
            _window.AsSpan(0, _windowCount), _burstAt, out OfdmFmKeyupState? keyup);
        if (burst is not null)
        {
            BurstHeard?.Invoke(burst);
        }

        if (burst?.Payload is byte[] payload)
        {
            LastSyncAtSample = _discarded + _burstAt;
            Report(burst);
            Deliver(payload);

            // A burst that copied is not a run of correlated samples to be defended against.
            // The attempt budget exists for a signal that stays correlated and never decodes,
            // an unmodulated carrier; it used to reset only when the correlation broke, which a
            // host that plays its frames back to back never lets it do, because the hunt
            // resumes exactly at the next sync symbol. Measured on air 2026-09-19 with such a
            // host: four bursts of every five decoded and the fifth refused, every time.
            _attemptsThisRun = 0;
            _exhausted = false;

            // It copied, so a burst really was here. The next frame of this keyup, if there is
            // one, starts where this one ended and may carry only its header: expect it there
            // rather than hunting for a sync symbol it will not have.
            return ExpectFollowOn(_burstAt + _need, keyup);
        }

        // A header that read and a payload that did not is NOT proof the timing was right, which
        // is the assumption the obvious version makes. The header is BPSK and coded, so it survives a
        // timing offset the QAM payload does not, so the commonest way to land here is a sync
        // committed a few samples off a peak that noise moved - and the position that would decode
        // is still in the window, a handful of samples further on. Skipping the whole announced
        // burst throws that away for good.
        //
        // Measured on the synthetic profile, 400 bursts across an AWGN sweep, against the
        // whole-buffer path on identical audio: resuming past the burst copies 335 where the
        // reference copies 339, and resuming one sample on copies 359 - it recovers the four it was
        // losing and finds twenty the reference's own global argmax misses. The retry is bounded by
        // the same per-run attempt budget as any other commit, so it cannot become the plateau
        // re-walk that budget exists to stop.
        if (burst is not null)
        {
            Report(burst);
        }

        return Abandon(SearchPositionOf(_burstAt) + 1);
    }

    /// <summary>
    /// Arms the follow-on expectation at <paramref name="at"/> with the keyup state a decoded
    /// burst handed back, or resumes hunting there when there is no state to read one with.
    /// </summary>
    private bool ExpectFollowOn(int at, OfdmFmKeyupState? keyup)
    {
        if (keyup is null)
        {
            return Abandon(SearchPositionOf(at));
        }

        _keyup = keyup;
        _followOn = true;
        _collecting = true;
        _burstAt = at;
        _need = _codec.FollowOnSearchSamples;
        _header = null;
        _tracking = false;
        _trackedFor = 0;
        _sumsValid = false;
        return true;
    }

    /// <summary>
    /// Collects a follow-on burst at the expected position. Returns true when it has finished
    /// with it, false while it is still waiting for samples.
    /// </summary>
    /// <remarks>
    /// The header is read where the previous burst ended, or within the gap allowance after it,
    /// against the acquisition channel the keyup carries. No header there means the keyup is over,
    /// or a frame was lost with it, and either way the sync hunt resumes from where the burst was
    /// expected: a new keyup's sync symbol may be exactly there. A header that reads but a payload
    /// that does not still sizes the burst, so the expectation moves on to the frame after it.
    /// </remarks>
    private bool CollectFollowOn()
    {
        ReadOnlySpan<float> audio = _window.AsSpan(0, _windowCount);
        if (_header is null)
        {
            OfdmFmHeader? read = _codec.ReadFollowOnHeader(audio, _burstAt, _keyup!, out int start);
            if (read is not OfdmFmHeader header || header.PayloadLength > MaxPayloadBytes)
            {
                _followOn = false;
                _keyup = null;
                return Abandon(SearchPositionOf(_burstAt));
            }

            _burstAt = start;
            _header = header;
            _need = _codec.FollowOnBurstSamples(header);
            return true;
        }

        OfdmFmBurst? burst = _codec.DecodeFollowOnAt(audio, _burstAt, _keyup!);
        if (burst is not null)
        {
            BurstHeard?.Invoke(burst);
            Report(burst);
        }

        if (burst?.Payload is byte[] payload)
        {
            LastSyncAtSample = _discarded + _burstAt;
            Deliver(payload);
            _attemptsThisRun = 0;
            _exhausted = false;
        }

        return ExpectFollowOn(_burstAt + _need, _keyup);
    }

    /// <summary>
    /// Tells the rate controller what a burst came to, once per burst rather than once per attempt.
    /// </summary>
    /// <remarks>
    /// <para>The debounce is not tidiness. A payload that fails is retried one sample on, several
    /// times, because the commonest cause is a sync committed a few samples off a peak that noise
    /// moved - so one burst that eventually copies produces a run of failures first. Feeding those
    /// to the controller would report a healthy link as a failing one and walk the rate down every
    /// time acquisition was slightly off.</para>
    /// <para>Reports inside one symbol of the last one are therefore dropped, which collapses a
    /// retry cluster to whatever it finally came to: the retries step a sample at a time and there
    /// are a handful of them. This used to be one announced burst length, which was too wide by a
    /// burst: with frames played back to back, consecutive bursts are exactly one burst apart, and
    /// a second failed burst that was longer than the first was dropped as though it were a retry
    /// of it, so a run of failures reached the controller as one. A success always reports,
    /// because a success ends the burst.</para>
    /// </remarks>
    private void Report(OfdmFmBurst burst)
    {
        if (_rate is null)
        {
            return;
        }

        long at = _discarded + _burstAt;
        if (burst.Payload is null && at - _reportedAt < _parameters.SymbolSamples)
        {
            return;
        }

        _reportedAt = at;
        _heardAtTicks = _time.GetTimestamp();
        _hasHeard = true;
        _rate.Heard(burst);

        // The geometry ask rides with the rate ask and is honoured the same way: a burst that
        // carried a recommendation carried a geometry with it, and one that carried none says
        // nothing about geometry either.
        if (burst.Recommendation is not null)
        {
            _askedGeometry = burst.RecommendedGeometry;
        }
    }

    /// <summary>
    /// Where a raw-audio position sits in the band-limited audio the search scans.
    /// </summary>
    /// <remarks>
    /// The two buffers hold the same samples but the filtered one lags by the filter's group delay,
    /// so the two index spaces differ by exactly that. Mixing them is not a rounding error: resuming
    /// the search at a raw position puts it BEHIND the peak it just rejected, which finds the same
    /// peak again and never terminates. That is not hypothetical, it hung the test suite.
    /// </remarks>
    private int SearchPositionOf(int rawPosition) => rawPosition + _bandLimit.Delay;

    private bool Abandon(int resumeAt)
    {
        _collecting = false;
        _header = null;
        _scanAt = Math.Min(resumeAt, _windowCount);
        _sumsValid = false;
        _tracking = false;
        _trackedFor = 0;
        return true;
    }

    private void Deliver(byte[] payload)
    {
        // The burst's own CRC-16/X-25 over the payload was checked in the codec and passed - a
        // failing one never reaches here. Reported as CrcValid rather than null because a display
        // asking "did this frame's CRC check out" has a true answer, even though the CRC is this
        // waveform's own rather than IL2P's; there is no IL2P here at all.
        FrameDecoded?.Invoke(
            payload,
            new FrameQuality(_mode, payload.Length, CorrectedBytes: null, CrcValid: true));
        _frameReceived(payload);
    }

    /// <summary>
    /// The self-correlation coefficient at <paramref name="at"/>: the sync symbol's useful part is
    /// two identical halves, so correlating the signal against itself half a transform later peaks
    /// exactly where the symbol starts. Both halves travel the same path, so a channel that tilts
    /// or echoes scales them together and the coefficient stays high - which is why this beats
    /// matching against a clean reference.
    /// </summary>
    /// <remarks>
    /// Incremental. The whole-buffer <c>FindSync</c> recomputes three sums of half a transform at
    /// every start position, which is fine for a fixed buffer and quadratic for a stream. These
    /// slide: one sample leaves each half and one joins, so a position costs a constant handful of
    /// multiplies however long the channel has been open.
    /// </remarks>
    private double Metric(int at)
    {
        int a = at + _windowStart;
        int b = a + _half;

        if (!_sumsValid)
        {
            _dot = 0;
            _energyA = 0;
            _energyB = 0;
            for (int n = 0; n < _half; n++)
            {
                double x = _search[a + n];
                double y = _search[b + n];
                _dot += x * y;
                _energyA += x * x;
                _energyB += y * y;
            }

            _sumsValid = true;
        }
        else
        {
            // Slide by one: the sample at a-1 leaves the first half and the one at b-1 crosses
            // from the second half into the first, while b+_half-1 joins the second.
            double left = _search[a - 1];
            double middle = _search[b - 1];
            double right = _search[b + _half - 1];

            _dot += (middle * right) - (left * middle);
            _energyA += (middle * middle) - (left * left);
            _energyB += (right * right) - (middle * middle);
        }

        // Sliding sums drift, and squares can only be positive, so clamp rather than let a tiny
        // negative from rounding become a NaN in the square root.
        double denominator = Math.Sqrt(Math.Max(_energyA, 0) * Math.Max(_energyB, 0));
        return denominator < 1e-12 ? 0 : _dot / denominator;
    }

    /// <summary>
    /// Drops audio nothing can still need: everything before the earliest position the search or a
    /// burst in progress might read. This is what makes the buffer bounded - without it a channel
    /// that never goes quiet would grow one indefinitely.
    /// </summary>
    private void Compact()
    {
        int keepFrom = _collecting ? _burstAt : _scanAt;
        if (_tracking)
        {
            keepFrom = Math.Min(keepFrom, _peakAt);
        }

        // A follow-on header not found where it was expected is looked for around there, on
        // audio the search band-limits from a fresh filter, so the filter's run-in before the
        // expected position has to still be here.
        if (_followOn && _header is null)
        {
            keepFrom = Math.Min(keepFrom, _burstAt - _codec.FollowOnLookBack);
        }

        // One sample more than anything still needs, because the sliding metric update reads the
        // sample immediately BEFORE the window it describes. On a profile with no cyclic prefix
        // that window starts exactly at the search position, so keeping only from there leaves the
        // next update reading off the front of the buffer. Every real profile has a guard and
        // would never have shown it.
        // ...and the filter's group delay beyond that, because a peak the search has not yet
        // committed points that many samples further back in the raw audio than its own index.
        keepFrom = Math.Max(0, keepFrom - 1 - _bandLimit.Delay);

        if (keepFrom <= 0)
        {
            return;
        }

        int remaining = _windowCount - keepFrom;
        _window.AsSpan(keepFrom, remaining).CopyTo(_window);
        _search.AsSpan(keepFrom, remaining).CopyTo(_search);
        _windowCount = remaining;
        _discarded += keepFrom;

        // Only the positions that are live: a stale one is never read, and letting it drift
        // negative for the life of the process is how a long-running station finds an overflow.
        _scanAt = Math.Max(0, _scanAt - keepFrom);
        if (_collecting)
        {
            _burstAt -= keepFrom;
        }

        if (_tracking)
        {
            _peakAt -= keepFrom;
        }

        // The running sums describe a window at a position that has just moved with everything
        // else, so they stay valid; only their indices changed.
    }
}
