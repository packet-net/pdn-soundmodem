using System.Runtime.CompilerServices;

namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>What a call to <see cref="PayloadReceiver.Receive"/> came to. The rest of what it found stays readable on the receiver until its next call.</summary>
public readonly struct PayloadReceiveResult
{
    internal PayloadReceiveResult(bool accepted, bool allBlocksSatisfied, bool checkPassed, int payloadBytes, int guardedCarriers)
    {
        Accepted = accepted;
        AllBlocksSatisfied = allBlocksSatisfied;
        CheckPassed = checkPassed;
        PayloadBytes = payloadBytes;
        GuardedCarriers = guardedCarriers;
    }

    /// <summary>False when the call was refused (not a data burst this code reads, or the capture does not hold the burst): nothing was computed.</summary>
    public bool Accepted { get; }

    /// <summary>Whether every code block was satisfied (the prototype's <c>converged</c>).</summary>
    public bool AllBlocksSatisfied { get; }

    /// <summary>Whether the payload's 32-bit check passed. The decoder's blocks can all be satisfied and the check still fail, and the check can pass with a block unsatisfied; the check is what decides.</summary>
    public bool CheckPassed { get; }

    /// <summary>The payload bytes written (the burst's whole room, padding included) when the check passed; 0 otherwise.</summary>
    public int PayloadBytes { get; }

    /// <summary>The carriers the tone guard raised the noise estimate on, counted over all 333.</summary>
    public int GuardedCarriers { get; }
}

/// <summary>
/// Receives the payload of a burst whose position and header are known, and measures the path it came over: the receive
/// side of the payload of the adaptive OFDM-FM modem, pinned to the prototype's <c>modem.receive(aligned, design, None)</c>
/// as the station calls it (<c>passes=1</c>, <c>guard=2.0</c>, <c>dd=0</c>, <c>prior=None</c>, <c>track=True</c>,
/// <c>gain=False</c>; the design is <c>formats.from_header</c>'s), with <c>modem.opening_quality</c> and <c>modem.sudden</c>.
/// </summary>
/// <remarks>
/// <para>The burst's symbols are taken one at a time as the header reader takes the opening's (bins 9 to 341 of the 2048-point
/// transform after the 64-sample prefix). The opening, rebuilt as known symbols (the header's from its bits as read, all at the
/// level gain of the format), gives the channel (smoothed across 11 carriers) and the noise (a running median across 25).
/// Each symbol's own delay is then found from the symbol itself and taken out: a ladder of 81 delays of a quarter of a microsecond,
/// about where the last symbol and the running drift put it, keeping the one that leaves the carriers whose noise rarely
/// moves a point across a boundary nearest their constellation points, then three refinements from the phase slope. The
/// equalised symbols are demapped, the tone guard raises the noise estimate on carriers whose scatter stands out, the
/// code blocks are decoded and the payload's check is tried. When it passes, the burst is rebuilt as it left the sender's
/// peak limit and the path is measured against it on every carrier.</para>
/// <para>One instance per receiver; not thread-safe. Every buffer for the longest burst is allocated by the constructor, so a
/// receive allocates nothing once the code plan for the burst's shape and the opening's are cached (the first burst of a new
/// shape builds them). What a call found is kept for later stages and tests, readable until the next call; the accessors
/// say what they hold and when. The prototype's path computes no gain (<c>gain=False</c>), so there is none here.</para>
/// </remarks>
public sealed class PayloadReceiver
{
    /// <summary>Delays tried for each symbol: -10 to +10 microseconds in quarter-microsecond steps.</summary>
    public const int LadderSteps = 81;

    private const int Carriers = BurstRenderer.Carriers;
    private const int SymbolSamples = BurstRenderer.SymbolSamples;
    private const int MaxSymbols = 9 + StandardFormats.MaxSymbols;
    private const int QualityLadderSteps = 321;
    private const double VarianceFloor = 1e-30;
    private const double Guard = 2.0;

    private static readonly double[] Freq = BuildFreq();
    private static readonly double[] TwoPiF = BuildTwoPiF();
    private static readonly double[] Ladder = BuildLadder(-10e-6, LadderSteps);
    private static readonly double[] QualityLadder = BuildLadder(-40e-6, QualityLadderSteps);
    private static readonly double[] TurnRe = BuildTurn(false);
    private static readonly double[] TurnIm = BuildTurn(true);
    private static readonly double[] Doubts = [0.5e-6, 0.15e-6, 0.15e-6];

    private readonly OpeningTransform _transform = new(MaxSymbols);
    private readonly OpeningTransform _openingTransform = new(9);
    private readonly QuadraticSmoother[] _smooth5 = new QuadraticSmoother[3];
    private readonly QuadraticSmoother[] _smooth3 = new QuadraticSmoother[3];
    private readonly RunningMedian _median12 = new(12);
    private readonly RunningMedian _median8 = new(8);
    private readonly RunningMedian _median6 = new(6);

    private readonly double[] _knownRe = new double[9 * Carriers];
    private readonly double[] _knownIm = new double[9 * Carriers];
    private readonly double[] _corrRe = new double[MaxSymbols * Carriers];
    private readonly double[] _corrIm = new double[MaxSymbols * Carriers];
    private readonly double[] _hRe = new double[Carriers];
    private readonly double[] _hIm = new double[Carriers];
    private readonly double[] _h2 = new double[Carriers];
    private readonly double[] _hinvRe = new double[Carriers];
    private readonly double[] _hinvIm = new double[Carriers];
    private readonly double[] _var = new double[Carriers];
    private readonly double[] _var2 = new double[Carriers];
    private readonly double[] _noise = new double[Carriers];
    private readonly double[] _rawRe = new double[Carriers];
    private readonly double[] _rawIm = new double[Carriers];
    private readonly double[] _vb = new double[Carriers];
    private readonly double[] _sigma2 = new double[Carriers];
    private readonly double[] _w0 = new double[Carriers];
    private readonly int[] _steady = new int[Carriers];
    private readonly bool[] _isSteady = new bool[Carriers];
    private readonly double[] _yRe = new double[Carriers];
    private readonly double[] _yIm = new double[Carriers];
    private readonly double[] _ladderMiss = new double[LadderSteps];
    private readonly double[] _taus = new double[MaxSymbols];
    private readonly double[] _miss = new double[MaxSymbols];
    private readonly byte[] _loading = new byte[Carriers];
    private readonly byte[] _scratchLoading = new byte[Carriers];
    private readonly byte[] _headerCoded = new byte[666];
    private readonly double[] _scatter = new double[Carriers];
    private readonly double[] _scatterMedian = new double[Carriers];
    private readonly double[] _measured = new double[Carriers];
    private readonly double[] _hkRe = new double[Carriers];
    private readonly double[] _hkIm = new double[Carriers];
    private readonly double[] _vk = new double[Carriers];
    private readonly double[] _h2Re = new double[Carriers];
    private readonly double[] _h2Im = new double[Carriers];
    private readonly double[] _v2 = new double[Carriers];
    private readonly double[] _decidedRe;
    private readonly double[] _decidedIm;
    private readonly double[] _zRe;
    private readonly double[] _zIm;
    private readonly float[] _llr;
    private readonly float[] _flat;
    private readonly byte[] _signBits;
    private readonly byte[] _guess;
    private readonly byte[] _tx;
    private readonly byte[] _info;
    private readonly double[] _refRe = new double[MaxSymbols * Carriers];
    private readonly double[] _refIm = new double[MaxSymbols * Carriers];
    private readonly double[] _fftRe = new double[BurstRenderer.FftSize];
    private readonly double[] _fftIm = new double[BurstRenderer.FftSize];
    private readonly double[] _body = new double[MaxSymbols * SymbolSamples];
    private readonly double[] _qhRe = new double[Carriers];
    private readonly double[] _qhIm = new double[Carriers];
    private readonly double[] _qErr = new double[Carriers];
    private readonly double[] _qErrMedian = new double[Carriers];
    private float[] _floatScratch = new float[1024];
    private byte[] _byteScratch = new byte[1024];

    private bool _valid;
    private bool _measuredValid;
    private int _known;
    private int _payloadSymbols;
    private int _guarded;

    /// <summary>Makes a receiver, with every buffer for the longest burst.</summary>
    public PayloadReceiver()
    {
        for (int o = 0; o < 3; o++)
        {
            int nb = HeaderReader.OpeningCarriers((FormatOpening)o);
            _smooth5[o] = new QuadraticSmoother(nb, 5);
            _smooth3[o] = new QuadraticSmoother(nb, 3);
        }

        int info = 0;
        int coded = 0;
        for (int n = 0; n < StandardFormats.Count; n++)
        {
            FormatSpec s = StandardFormats.Describe(n);
            info = Math.Max(info, StandardFormats.InfoBits(n, StandardFormats.MaxSymbols));
            coded = Math.Max(coded, s.Carriers * s.ConstellationBits * StandardFormats.MaxSymbols);
        }

        _info = new byte[info];
        _guess = new byte[coded];
        _tx = new byte[coded];
        _signBits = new byte[coded];
        _llr = new float[coded];
        _flat = new float[coded];
        _decidedRe = new double[StandardFormats.MaxSymbols * Carriers];
        _decidedIm = new double[StandardFormats.MaxSymbols * Carriers];
        _zRe = new double[StandardFormats.MaxSymbols * Carriers];
        _zIm = new double[StandardFormats.MaxSymbols * Carriers];
    }

    /// <summary>
    /// The samples a burst with this header needs, counted from its first training sample (the start index of
    /// <see cref="Receive"/>): the opening's symbols and the payload's, 2112 samples each. -1 when the header is not one this
    /// receiver reads (a data burst of version 0 with payload symbols, 4 to 252).
    /// </summary>
    public static int SamplesNeeded(in BurstHeader header)
    {
        if (!Readable(header))
        {
            return -1;
        }

        FormatOpening opening = StandardFormats.Describe(header.Format).Opening;
        return (HeaderReader.TrainingSymbolCount(opening) + HeaderReader.HeaderSymbolCount(opening) + header.PayloadSymbols) * SymbolSamples;
    }

    /// <summary>The bytes of payload room a burst with this header has (what <see cref="Receive"/> needs a payload span of); -1 when the header is not one this receiver reads.</summary>
    public static int PayloadRoom(in BurstHeader header) =>
        Readable(header) ? PayloadCodec.Capacity(header.Format, header.PayloadSymbols) : -1;

    /// <summary>Whether the last call computed anything (it was accepted).</summary>
    public bool HasResult => _valid;

    /// <summary>Symbols the opening has (training and header) for the last burst: the first payload symbol's index in <see cref="Delays"/> and <see cref="Miss"/>.</summary>
    public int KnownSymbols => _valid ? _known : 0;

    /// <summary>The delay found for each symbol of the last burst, the opening's first, in seconds: the accumulated delay after that symbol's ladder search and refinements, the next symbol starts from it plus the running drift. A positive value means the symbol's carriers were turned forward by that much to meet the channel.</summary>
    public ReadOnlySpan<double> Delays => _valid ? _taus.AsSpan(0, _known + _payloadSymbols) : default;

    /// <summary>Each symbol's mean squared miss from its constellation points (the opening's from the known symbols), in units of the noise expected, over the carriers whose noise rarely moves a point across a boundary: the prototype's <c>follow.last_miss</c>.</summary>
    public ReadOnlySpan<double> Miss => _valid ? _miss.AsSpan(0, _known + _payloadSymbols) : default;

    /// <summary>The carriers the tone guard raised the noise estimate on in the last burst (over all 333, as the prototype counts).</summary>
    public int GuardedCarriers => _valid ? _guarded : 0;

    /// <summary>The decided symbols of the last burst: the carriers rebuilt from the decoder's best guess at every coded bit (a code block that was satisfied, re-encoded; the soft values' own signs where it was not), payload symbols times 333, real parts. A later stage rebuilds the burst from them.</summary>
    public ReadOnlySpan<double> DecidedReal => _valid ? _decidedRe.AsSpan(0, _payloadSymbols * Carriers) : default;

    /// <summary>The decided symbols' imaginary parts, laid out as <see cref="DecidedReal"/>.</summary>
    public ReadOnlySpan<double> DecidedImaginary => _valid ? _decidedIm.AsSpan(0, _payloadSymbols * Carriers) : default;

    /// <summary>
    /// The measured quality of the path on each of the 333 carriers (a power ratio), when the last burst's check passed;
    /// empty otherwise. As the prototype's <c>measured_snr</c>: on the format's own carriers from the whole burst as it left the
    /// sender's peak limit, on every other carrier (the unused ones of the opening's band, 1.0 beyond it) from the opening alone.
    /// </summary>
    public ReadOnlySpan<double> Quality => _valid && _measuredValid ? _measured : default;

    /// <summary>Whether the last burst's quality collapsed part-way (see <see cref="IsSudden"/>).</summary>
    public bool Sudden => _valid && IsSudden(_miss.AsSpan(0, _known + _payloadSymbols), _known);

    /// <summary>
    /// Whether a burst's quality collapsed part-way, as the prototype's <c>modem.sudden</c>: something else came on the
    /// channel. <paramref name="miss"/> is each symbol's miss (<see cref="Miss"/>), <paramref name="known"/> the symbols its
    /// opening has. True when some point splits the payload's symbols into a part that was decoding cleanly and a later part
    /// whose median miss is at least three times worse. A path that is simply too poor is bad from the start.
    /// </summary>
    public static bool IsSudden(ReadOnlySpan<double> miss, int known)
    {
        if (known < 0 || known > miss.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(known));
        }

        ReadOnlySpan<double> m = miss[known..];
        if (m.Length < 6)
        {
            return false;
        }

        Span<double> scratch = miss.Length <= 512 ? stackalloc double[miss.Length] : new double[miss.Length];
        double reference = known != 0 ? Median(miss[..known], scratch) : Median(m[..2], scratch);
        for (int cut = 2; cut < m.Length - 1; cut++)
        {
            double before = Median(m[..cut], scratch);
            double after = Median(m[cut..], scratch);
            if (before < (1.5 * reference) + 0.5 && after > 3.0 * Math.Max(before, 0.5))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Receives the payload of the burst whose first training sample is <paramref name="start"/> in the capture, its header
    /// (<paramref name="header"/>) and the header's 51 bits as read (<paramref name="headerBits"/>: the opening's second known
    /// symbol is rebuilt from them). The unscrambled payload bytes are written to <paramref name="payload"/> when the check
    /// passes (see <see cref="PayloadRoom"/>; <see cref="PayloadCodec.TryUnpack"/> reads them). The result's
    /// <see cref="PayloadReceiveResult.Accepted"/> is false, and nothing is computed, when the header is not a data burst of
    /// version 0 with payload symbols or the capture does not hold <see cref="SamplesNeeded"/> samples from the start.
    /// </summary>
    /// <exception cref="ArgumentException">The header's bits are fewer than 51, or the payload span is smaller than <see cref="PayloadRoom"/>.</exception>
    public PayloadReceiveResult Receive(ReadOnlySpan<float> capture, int start, in BurstHeader header, ReadOnlySpan<byte> headerBits, Span<byte> payload)
    {
        _valid = false;
        _measuredValid = false;
        _guarded = 0;
        if (!Readable(header))
        {
            return default;
        }

        if (headerBits.Length < BurstHeaderCodec.HeaderBits)
        {
            throw new ArgumentException("Need the header's 51 bits.", nameof(headerBits));
        }

        int room = PayloadCodec.Capacity(header.Format, header.PayloadSymbols);
        if (payload.Length < room)
        {
            throw new ArgumentException($"Need room for {room} payload bytes.", nameof(payload));
        }

        FormatSpec spec = StandardFormats.Describe(header.Format);
        FormatOpening opening = spec.Opening;
        int training = HeaderReader.TrainingSymbolCount(opening);
        int nb = HeaderReader.OpeningCarriers(opening);
        int known = training + HeaderReader.HeaderSymbolCount(opening);
        int symbols = header.PayloadSymbols;
        int n = known + symbols;
        if (start < 0 || capture.Length - start < n * SymbolSamples)
        {
            return default;
        }

        _known = known;
        _payloadSymbols = symbols;

        // The opening as known symbols, at the format's level gain, and the format's loading.
        double gain = Math.Sqrt((double)spec.Carriers / nb);
        BurstGrid.BuildOpening(opening, headerBits, gain, _knownRe, _knownIm, _scratchLoading, _headerCoded);
        _loading.AsSpan().Clear();
        _loading.AsSpan(0, spec.Carriers).Fill((byte)spec.ConstellationBits);

        _transform.Transform(capture, start, n);
        ReadOnlySpan<double> rawRe = _transform.Real;
        ReadOnlySpan<double> rawIm = _transform.Imaginary;

        // The channel and the noise from the known symbols (every one of them, header included).
        Channel(rawRe, rawIm, _knownRe, _knownIm, known, nb, _smooth5[(int)opening], _hRe, _hIm);
        Noise(rawRe, rawIm, _knownRe, _knownIm, known, nb, _hRe, _hIm, _median12, _var);
        for (int c = 0; c < Carriers; c++)
        {
            _var[c] = Math.Max(_var[c], VarianceFloor);
            _h2[c] = (_hRe[c] * _hRe[c]) + (_hIm[c] * _hIm[c]);
            double d = Math.Max(_h2[c], VarianceFloor);
            _hinvRe[c] = _hRe[c] / d;
            _hinvIm[c] = -_hIm[c] / d;
        }

        Follow(rawRe, rawIm, spec, known, n);

        // Equalise the payload symbols and read them: soft values, then the tone guard, then soft values again.
        int perSymbol = spec.Carriers * spec.ConstellationBits;
        int coded = symbols * perSymbol;
        for (int s = 0; s < symbols; s++)
        {
            int at = (known + s) * Carriers;
            int o = s * Carriers;
            for (int c = 0; c < Carriers; c++)
            {
                double yr = _corrRe[at + c];
                double yi = _corrIm[at + c];
                _zRe[o + c] = (yr * _hinvRe[c]) - (yi * _hinvIm[c]);
                _zIm[o + c] = (yr * _hinvIm[c]) + (yi * _hinvRe[c]);
            }
        }

        for (int c = 0; c < Carriers; c++)
        {
            _noise[c] = _var[c] / Math.Max(_h2[c], VarianceFloor);
        }

        ReadOnlySpan<double> zRe = _zRe.AsSpan(0, symbols * Carriers);
        ReadOnlySpan<double> zIm = _zIm.AsSpan(0, symbols * Carriers);
        Span<float> llr = _llr.AsSpan(0, coded);
        Demapper.DemapPayload(zRe, zIm, _noise, _loading, symbols, llr);

        // The tone guard: carriers whose scatter about the nearest decided points is far above its neighbours' get the variance they show.
        for (int i = 0; i < coded; i++)
        {
            _signBits[i] = llr[i] < 0 ? (byte)1 : (byte)0;
        }

        for (int s = 0; s < symbols; s++)
        {
            Constellation.MapSymbol(_signBits.AsSpan(s * perSymbol, perSymbol), _loading, _decidedRe.AsSpan(s * Carriers, Carriers), _decidedIm.AsSpan(s * Carriers, Carriers));
        }

        for (int c = 0; c < Carriers; c++)
        {
            double sum = 0;
            for (int s = 0; s < symbols; s++)
            {
                int at = (s * Carriers) + c;
                double dr = _corrRe[((known + s) * Carriers) + c] - ((_hRe[c] * _decidedRe[at]) - (_hIm[c] * _decidedIm[at]));
                double di = _corrIm[((known + s) * Carriers) + c] - ((_hRe[c] * _decidedIm[at]) + (_hIm[c] * _decidedRe[at]));
                sum += (dr * dr) + (di * di);
            }

            _scatter[c] = sum / symbols;
        }

        _median8.Apply(_scatter, _scatterMedian);
        int hot = 0;
        for (int c = 0; c < Carriers; c++)
        {
            bool isHot = _scatter[c] > Guard * _scatterMedian[c];
            _var2[c] = isHot ? Math.Max(_var[c], _scatter[c]) : _var[c];
            if (isHot)
            {
                hot++;
            }

            _noise[c] = _var2[c] / Math.Max(_h2[c], VarianceFloor);
        }

        _guarded = hot;
        Demapper.DemapPayload(zRe, zIm, _noise, _loading, symbols, llr);

        // Decode, block by block.
        int infoBits = StandardFormats.InfoBits(header.Format, symbols);
        Span<float> flat = _flat.AsSpan(0, coded);
        Interleaver.Deinterleave<float>(llr, flat);
        PayloadCode code = PayloadCode.Get(infoBits, coded);
        EnsureScratch(code);
        Span<byte> info = _info.AsSpan(0, infoBits);
        Span<byte> guess = _guess.AsSpan(0, coded);
        bool all = code.Decode(flat, info, guess, _floatScratch, _byteScratch);

        // The best guess at every coded bit, as the carriers a later stage rebuilds the burst from.
        Span<byte> tx = _tx.AsSpan(0, coded);
        Interleaver.Interleave<byte>(guess, tx);
        for (int s = 0; s < symbols; s++)
        {
            Constellation.MapSymbol(tx.Slice(s * perSymbol, perSymbol), _loading, _decidedRe.AsSpan(s * Carriers, Carriers), _decidedIm.AsSpan(s * Carriers, Carriers));
        }

        _valid = true;
        bool passed = PayloadCodec.TryDecode(info, header.Sender, header.Destination, header.BurstNumber, payload, out int length);
        if (passed)
        {
            Measure(spec, code, info, known, n, nb, symbols, perSymbol, coded);
            _measuredValid = true;
        }

        return new PayloadReceiveResult(true, all, passed, passed ? length : 0, hot);
    }

    /// <summary>
    /// The quality on each carrier from a burst's opening alone, for a burst whose payload failed: the prototype's
    /// <c>modem.opening_quality</c>. The opening's training gives the channel and the header, whose bits are known, gives what
    /// is left over on each carrier, smoothed across neighbouring carriers. Writes 333 values into <paramref name="quality"/>,
    /// NaN on carriers the opening does not use. The drive is the format's, and the opening is measured against what the sender's
    /// peak limit left of it. False, with nothing written, when the header names no format or the capture does not hold the opening.
    /// </summary>
    public bool MeasureOpening(ReadOnlySpan<float> capture, int start, in BurstHeader header, ReadOnlySpan<byte> headerBits, Span<double> quality)
    {
        if ((uint)header.Format >= StandardFormats.Count)
        {
            return false;
        }

        FormatSpec spec = StandardFormats.Describe(header.Format);
        return MeasureOpening(capture, start, spec.Opening, spec.DriveDb, headerBits, quality);
    }

    /// <summary>As <see cref="MeasureOpening(ReadOnlySpan{float}, int, in BurstHeader, ReadOnlySpan{byte}, Span{double})"/> for an opening and a drive (decibels over full drive; NaN for none, the opening measured as sent).</summary>
    public bool MeasureOpening(ReadOnlySpan<float> capture, int start, FormatOpening opening, double driveDb, ReadOnlySpan<byte> headerBits, Span<double> quality)
    {
        if (quality.Length < Carriers)
        {
            throw new ArgumentException("Need room for 333 values.", nameof(quality));
        }

        if (headerBits.Length < BurstHeaderCodec.HeaderBits)
        {
            throw new ArgumentException("Need the header's 51 bits.", nameof(headerBits));
        }

        int t = HeaderReader.TrainingSymbolCount(opening);
        int h = HeaderReader.HeaderSymbolCount(opening);
        int nb = HeaderReader.OpeningCarriers(opening);
        int symbols = t + h;
        if (start < 0 || capture.Length - start < symbols * SymbolSamples)
        {
            return false;
        }

        _openingTransform.Transform(capture, start, symbols);
        ReadOnlySpan<double> yRe = _openingTransform.Real;
        ReadOnlySpan<double> yIm = _openingTransform.Imaginary;
        BurstGrid.BuildOpening(opening, headerBits, 1.0, _knownRe, _knownIm, _scratchLoading, _headerCoded);
        if (!double.IsNaN(driveDb))
        {
            Limit(_knownRe, _knownIm, symbols, driveDb);
        }

        // The channel from the training alone.
        for (int c = 0; c < nb; c++)
        {
            double sr = 0;
            double si = 0;
            for (int r = 0; r < t; r++)
            {
                int at = (r * Carriers) + c;
                sr += (yRe[at] * _knownRe[at]) + (yIm[at] * _knownIm[at]);
                si += (yIm[at] * _knownRe[at]) - (yRe[at] * _knownIm[at]);
            }

            _rawRe[c] = sr / t;
            _rawIm[c] = si / t;
        }

        _smooth5[(int)opening].Smooth(_rawRe, _rawIm, _qhRe, _qhIm);
        for (int c = 0; c < nb; c++)
        {
            double h2 = (_qhRe[c] * _qhRe[c]) + (_qhIm[c] * _qhIm[c]);
            double d = Math.Max(h2, VarianceFloor);
            _h2[c] = h2;
            _hinvRe[c] = _qhRe[c] / d;
            _hinvIm[c] = -_qhIm[c] / d;
            _qErr[c] = 0;
        }

        // What each header symbol leaves over on each carrier, at the delay that suits it best.
        for (int k = 0; k < h; k++)
        {
            int row = (t + k) * Carriers;
            double bestCost = double.PositiveInfinity;
            double bestDelay = 0;
            for (int i = 0; i < QualityLadderSteps; i++)
            {
                double cost = 0;
                for (int c = 0; c < nb; c++)
                {
                    HeaderMiss(yRe[row + c], yIm[row + c], c, QualityLadder[i], row, out double e);
                    cost += e * _h2[c];
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestDelay = QualityLadder[i];
                }
            }

            for (int c = 0; c < nb; c++)
            {
                HeaderMiss(yRe[row + c], yIm[row + c], c, bestDelay, row, out double e);
                _qErr[c] += e;
            }
        }

        // What is left over holds the header symbol's noise and the channel estimate's; the estimate's share is taken out.
        for (int c = 0; c < nb; c++)
        {
            _qErr[c] /= h;
        }

        _median12.Apply(_qErr.AsSpan(0, nb), _qErrMedian.AsSpan(0, nb));
        double power = 0;
        for (int k = 0; k < h; k++)
        {
            for (int c = 0; c < nb; c++)
            {
                int at = ((t + k) * Carriers) + c;
                power += (_knownRe[at] * _knownRe[at]) + (_knownIm[at] * _knownIm[at]);
            }
        }

        power /= h * nb;
        quality.Slice(0, Carriers).Fill(double.NaN);
        for (int c = 0; c < nb; c++)
        {
            double err = _qErrMedian[c] * (t / (t + 1.0)) / power;
            quality[c] = Math.Max((1.0 / Math.Max(err, 1e-9)) - (1.0 / (5.0 * t)), 1e-3);
        }

        return true;
    }

    private static bool Readable(in BurstHeader header) =>
        header.Kind == BurstKind.Data && header.Version == 0 && header.PayloadSymbols > 0 && header.PayloadSymbols <= StandardFormats.MaxSymbols
        && (uint)header.Format < StandardFormats.Count && PayloadCodec.Capacity(header.Format, header.PayloadSymbols) >= 0;

    // numpy's median: the middle value, or the mean of the two middle ones. Sorts a copy in the scratch.
    private static double Median(ReadOnlySpan<double> x, Span<double> scratch)
    {
        Span<double> s = scratch.Slice(0, x.Length);
        x.CopyTo(s);
        s.Sort();
        int m = s.Length;
        return (m & 1) == 1 ? s[m / 2] : (s[(m / 2) - 1] + s[m / 2]) / 2;
    }

    // One header symbol's carrier c, turned by the delay, divided by the channel, against the header's symbol: the squared miss.
    private void HeaderMiss(double yr, double yi, int c, double delay, int row, out double e)
    {
        double th = TwoPiF[c] * delay;
        double cs = Math.Cos(th);
        double sn = Math.Sin(th);
        double ar = (yr * cs) - (yi * sn);
        double ai = (yr * sn) + (yi * cs);
        double zr = (ar * _hinvRe[c]) - (ai * _hinvIm[c]);
        double zi = (ar * _hinvIm[c]) + (ai * _hinvRe[c]);
        double dr = zr - _knownRe[row + c];
        double di = zi - _knownIm[row + c];
        e = (dr * dr) + (di * di);
    }

    private void EnsureScratch(PayloadCode code)
    {
        if (_floatScratch.Length < code.DecodeScratchLength)
        {
            _floatScratch = new float[code.DecodeScratchLength];
        }

        if (_byteScratch.Length < code.ScratchLength)
        {
            _byteScratch = new byte[code.ScratchLength];
        }
    }

    // The prototype's channel(): per carrier on the band, the sum over the rows of y times the conjugate of the reference over the sum of the
    // reference's power, smoothed (no smoother: as it is, which a half-window of 1 is); one beyond the band.
    private void Channel(ReadOnlySpan<double> yRe, ReadOnlySpan<double> yIm, ReadOnlySpan<double> refRe, ReadOnlySpan<double> refIm, int rows, int nb, QuadraticSmoother? smoother, Span<double> hRe, Span<double> hIm)
    {
        for (int c = 0; c < nb; c++)
        {
            double sr = 0;
            double si = 0;
            double p = 0;
            for (int k = 0; k < rows; k++)
            {
                int at = (k * Carriers) + c;
                sr += (yRe[at] * refRe[at]) + (yIm[at] * refIm[at]);
                si += (yIm[at] * refRe[at]) - (yRe[at] * refIm[at]);
                p += (refRe[at] * refRe[at]) + (refIm[at] * refIm[at]);
            }

            double d = Math.Max(p, VarianceFloor);
            _rawRe[c] = sr / d;
            _rawIm[c] = si / d;
        }

        if (smoother is null)
        {
            _rawRe.AsSpan(0, nb).CopyTo(hRe);
            _rawIm.AsSpan(0, nb).CopyTo(hIm);
        }
        else
        {
            smoother.Smooth(_rawRe, _rawIm, hRe, hIm);
        }

        for (int c = nb; c < Carriers; c++)
        {
            hRe[c] = 1;
            hIm[c] = 0;
        }
    }

    // The prototype's noise(): the mean over the rows of the squared difference between y and the channel times the reference, per carrier
    // on the band, then a running median when there is one; one beyond the band.
    private void Noise(ReadOnlySpan<double> yRe, ReadOnlySpan<double> yIm, ReadOnlySpan<double> refRe, ReadOnlySpan<double> refIm, int rows, int nb, ReadOnlySpan<double> hRe, ReadOnlySpan<double> hIm, RunningMedian? median, Span<double> v)
    {
        for (int c = 0; c < nb; c++)
        {
            double sum = 0;
            for (int k = 0; k < rows; k++)
            {
                int at = (k * Carriers) + c;
                double dr = yRe[at] - ((hRe[c] * refRe[at]) - (hIm[c] * refIm[at]));
                double di = yIm[at] - ((hRe[c] * refIm[at]) + (hIm[c] * refRe[at]));
                sum += (dr * dr) + (di * di);
            }

            _vb[c] = sum / rows;
        }

        if (median is null)
        {
            _vb.AsSpan(0, nb).CopyTo(v);
        }
        else
        {
            median.Apply(_vb.AsSpan(0, nb), v.Slice(0, nb));
        }

        for (int c = nb; c < Carriers; c++)
        {
            v[c] = 1;
        }
    }

    // The prototype's follow(): each symbol's own delay found from the symbol and taken out, written to the corrected symbols, the delays
    // (seconds) and the misses.
    private void Follow(ReadOnlySpan<double> rawRe, ReadOnlySpan<double> rawIm, FormatSpec spec, int known, int n)
    {
        int used = spec.Carriers;
        Constellation constellation = Constellation.For(spec.ConstellationBits);
        double scale = constellation.HalfDistance;
        int mi = (spec.ConstellationBits + 1) / 2;
        int mq = spec.ConstellationBits / 2;
        double niM1 = (1 << mi) - 1;
        double nqM1 = mq == 0 ? 0 : (1 << mq) - 1;

        // Only carriers whose noise rarely moves a point across a boundary are used at all.
        int steadyCount = 0;
        for (int c = 0; c < used; c++)
        {
            double sigma = Math.Sqrt(_var[c] / Math.Max(_h2[c], VarianceFloor) / 2);
            _sigma2[c] = sigma * sigma;
            _w0[c] = _h2[c] / _var[c];
            bool steady = scale > 1.5 * sigma;
            _isSteady[c] = steady;
            if (steady)
            {
                steadyCount++;
            }
        }

        if (steadyCount < 12)
        {
            steadyCount = used;
            for (int c = 0; c < used; c++)
            {
                _isSteady[c] = true;
            }
        }

        int listed = 0;
        for (int c = 0; c < used; c++)
        {
            if (_isSteady[c])
            {
                _steady[listed++] = c;
            }
        }

        for (int c = used; c < Carriers; c++)
        {
            _isSteady[c] = false;
        }

        double drift = 0;
        for (int k = 0; k < n; k++)
        {
            bool isKnown = k < known;
            double tau = k > 0 ? _taus[k - 1] + drift : 0.0;
            int row = k * Carriers;
            for (int c = 0; c < Carriers; c++)
            {
                _yRe[c] = rawRe[row + c];
                _yIm[c] = rawIm[row + c];
            }

            Rotate(tau);

            // The ladder: the delay that leaves the steady carriers nearest their points.
            Array.Clear(_ladderMiss);
            for (int j = 0; j < listed; j++)
            {
                int c = _steady[j];
                double wr = (_yRe[c] * _hinvRe[c]) - (_yIm[c] * _hinvIm[c]);
                double wi = (_yRe[c] * _hinvIm[c]) + (_yIm[c] * _hinvRe[c]);
                double sigma2 = _sigma2[c];
                int t = c * LadderSteps;
                if (isKnown)
                {
                    double rr = _knownRe[row + c];
                    double ri = _knownIm[row + c];
                    for (int i = 0; i < LadderSteps; i++)
                    {
                        double zr = (wr * TurnRe[t + i]) - (wi * TurnIm[t + i]);
                        double zi = (wr * TurnIm[t + i]) + (wi * TurnRe[t + i]);
                        double dr = zr - rr;
                        double di = zi - ri;
                        _ladderMiss[i] += ((dr * dr) + (di * di)) / sigma2;
                    }
                }
                else
                {
                    for (int i = 0; i < LadderSteps; i++)
                    {
                        double zr = (wr * TurnRe[t + i]) - (wi * TurnIm[t + i]);
                        double zi = (wr * TurnIm[t + i]) + (wi * TurnRe[t + i]);
                        Nearest(zr, zi, scale, niM1, nqM1, mq != 0, out double rr, out double ri);
                        double dr = zr - rr;
                        double di = zi - ri;
                        _ladderMiss[i] += ((dr * dr) + (di * di)) / sigma2;
                    }
                }
            }

            int pick = 0;
            for (int i = 1; i < LadderSteps; i++)
            {
                if (_ladderMiss[i] < _ladderMiss[pick])
                {
                    pick = i;
                }
            }

            Rotate(Ladder[pick]);
            tau += Ladder[pick];

            // The fine measurement: a carrier votes only while its decided point could not have been pushed across a boundary by an error of the size still in doubt.
            for (int stage = 0; stage < Doubts.Length; stage++)
            {
                double doubt = Doubts[stage];
                double num = 0;
                double den = 0;
                int votes = 0;
                for (int c = 0; c < used; c++)
                {
                    double rr;
                    double ri;
                    if (isKnown)
                    {
                        rr = _knownRe[row + c];
                        ri = _knownIm[row + c];
                    }
                    else
                    {
                        if (!_isSteady[c])
                        {
                            continue;
                        }

                        double zr = (_yRe[c] * _hinvRe[c]) - (_yIm[c] * _hinvIm[c]);
                        double zi = (_yRe[c] * _hinvIm[c]) + (_yIm[c] * _hinvRe[c]);
                        Nearest(zr, zi, scale, niM1, nqM1, mq != 0, out rr, out ri);
                        double mag = Math.Sqrt((rr * rr) + (ri * ri));
                        if (!(mag * ((TwoPiF[c] * doubt) + 0.01) < 0.5 * scale))
                        {
                            continue;
                        }
                    }

                    // angle of y times the conjugate of (h times the reference)
                    double pr = (_hRe[c] * rr) - (_hIm[c] * ri);
                    double pi = (_hRe[c] * ri) + (_hIm[c] * rr);
                    double ang = Math.Atan2((_yIm[c] * pr) - (_yRe[c] * pi), (_yRe[c] * pr) + (_yIm[c] * pi));
                    if (!(Math.Abs(ang) < 0.35))
                    {
                        continue;
                    }

                    double w = _w0[c] * ((rr * rr) + (ri * ri));
                    if (w != 0)
                    {
                        votes++;
                    }

                    num += w * ang * Freq[c];
                    den += w * Freq[c] * Freq[c];
                }

                if (votes < 12)
                {
                    continue;
                }

                double step = -num / den / (2 * Math.PI);
                Rotate(step);
                tau += step;
            }

            for (int c = 0; c < Carriers; c++)
            {
                _corrRe[row + c] = _yRe[c];
                _corrIm[row + c] = _yIm[c];
            }

            double missSum = 0;
            for (int j = 0; j < listed; j++)
            {
                int c = _steady[j];
                double zr = (_yRe[c] * _hinvRe[c]) - (_yIm[c] * _hinvIm[c]);
                double zi = (_yRe[c] * _hinvIm[c]) + (_yIm[c] * _hinvRe[c]);
                double rr;
                double ri;
                if (isKnown)
                {
                    rr = _knownRe[row + c];
                    ri = _knownIm[row + c];
                }
                else
                {
                    Nearest(zr, zi, scale, niM1, nqM1, mq != 0, out rr, out ri);
                }

                double dr = zr - rr;
                double di = zi - ri;
                missSum += ((dr * dr) + (di * di)) / _sigma2[c];
            }

            _miss[k] = missSum / Math.Max(1, steadyCount);
            if (k > 0)
            {
                double move = tau - _taus[k - 1];
                drift = k == 1 ? move : (0.8 * drift) + (0.2 * move);
            }

            _taus[k] = tau;
        }
    }

    // Turns the working symbol's carriers by a delay: carrier c by 2 pi f delay.
    private void Rotate(double delay)
    {
        for (int c = 0; c < Carriers; c++)
        {
            double th = TwoPiF[c] * delay;
            double cs = Math.Cos(th);
            double sn = Math.Sin(th);
            double r = (_yRe[c] * cs) - (_yIm[c] * sn);
            _yIm[c] = (_yRe[c] * sn) + (_yIm[c] * cs);
            _yRe[c] = r;
        }
    }

    // The prototype's nearest() for one carrier of a rectangular constellation (levels spaced 2, in units of scale).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Nearest(double zr, double zi, double scale, double niM1, double nqM1, bool quadrature, out double rr, out double ri)
    {
        double vr = zr / scale;
        double lr = Math.Min(Math.Max(Math.Round(((vr + (niM1 + 1)) - 1) / 2), 0), niM1);
        rr = ((2 * lr) - niM1) * scale;
        if (quadrature)
        {
            double vi = zi / scale;
            double lq = Math.Min(Math.Max(Math.Round(((vi + (nqM1 + 1)) - 1) / 2), 0), nqM1);
            ri = ((2 * lq) - nqM1) * scale;
        }
        else
        {
            ri = 0;
        }
    }

    // Measures the path against the burst as the sender's peak limit left it: the decoded payload re-encoded, the opening, the limit, then
    // the channel and the noise on every carrier.
    private void Measure(FormatSpec spec, PayloadCode code, ReadOnlySpan<byte> info, int known, int n, int nb, int symbols, int perSymbol, int coded)
    {
        Span<byte> reCoded = _guess.AsSpan(0, coded);
        code.Encode(info, reCoded, _byteScratch);
        Span<byte> tx = _tx.AsSpan(0, coded);
        Interleaver.Interleave<byte>(reCoded, tx);
        _knownRe.AsSpan(0, known * Carriers).CopyTo(_refRe);
        _knownIm.AsSpan(0, known * Carriers).CopyTo(_refIm);
        for (int s = 0; s < symbols; s++)
        {
            int at = (known + s) * Carriers;
            Constellation.MapSymbol(tx.Slice(s * perSymbol, perSymbol), _loading, _refRe.AsSpan(at, Carriers), _refIm.AsSpan(at, Carriers));
        }

        Limit(_refRe, _refIm, n, spec.DriveDb);
        FormatOpening opening = spec.Opening;
        ReadOnlySpan<double> corrRe = _corrRe.AsSpan(0, n * Carriers);
        ReadOnlySpan<double> corrIm = _corrIm.AsSpan(0, n * Carriers);

        // Every symbol is now a pilot: the channel from all of them (as it is, no smoothing), the noise as it is.
        Channel(corrRe, corrIm, _refRe, _refIm, n, nb, null, _h2Re, _h2Im);
        Noise(corrRe, corrIm, _refRe, _refIm, n, nb, _h2Re, _h2Im, null, _v2);

        // The carriers beyond the format's: the channel from the opening's symbols alone, smoothed across 7, the noise a running median across 13.
        Channel(corrRe, corrIm, _refRe, _refIm, known, nb, _smooth3[(int)opening], _hkRe, _hkIm);
        Noise(corrRe, corrIm, _refRe, _refIm, known, nb, _hkRe, _hkIm, _median6, _vk);
        for (int c = 0; c < Carriers; c++)
        {
            if (c < spec.Carriers)
            {
                double h2 = (_h2Re[c] * _h2Re[c]) + (_h2Im[c] * _h2Im[c]);
                _measured[c] = h2 / Math.Max(_v2[c], VarianceFloor);
            }
            else
            {
                double h2 = (_hkRe[c] * _hkRe[c]) + (_hkIm[c] * _hkIm[c]);
                _measured[c] = h2 / Math.Max(_vk[c], VarianceFloor);
            }
        }
    }

    // What a grid holds once the sender's peak limit has acted: the grid (symbols times 333 carriers) is turned into the body, scaled to the
    // drive's level, clipped, and transformed back, in the grid's own units (divided by the scale).
    private void Limit(double[] gridRe, double[] gridIm, int symbols, double driveDb)
    {
        Span<double> body = BurstGrid.Transform(gridRe, gridIm, symbols, _fftRe, _fftIm, _body);
        double c = BurstGrid.Level(body, driveDb);
        for (int i = 0; i < body.Length; i++)
        {
            double v = body[i] * c;
            body[i] = v > BurstRenderer.PeakLimit ? BurstRenderer.PeakLimit : v < -BurstRenderer.PeakLimit ? -BurstRenderer.PeakLimit : v;
        }

        for (int s = 0; s < symbols; s++)
        {
            body.Slice((s * SymbolSamples) + BurstRenderer.CyclicPrefix, BurstRenderer.FftSize).CopyTo(_fftRe);
            Array.Clear(_fftIm);
            RealFft.Forward(_fftRe, _fftIm);
            int at = s * Carriers;
            for (int k = 0; k < Carriers; k++)
            {
                gridRe[at + k] = _fftRe[BurstRenderer.FirstBin + k] / c;
                gridIm[at + k] = _fftIm[BurstRenderer.FirstBin + k] / c;
            }
        }
    }

    private static double[] BuildFreq()
    {
        var f = new double[Carriers];
        for (int c = 0; c < f.Length; c++)
        {
            f[c] = (BurstRenderer.FirstBin + c) * 48000.0 / BurstRenderer.FftSize;
        }

        return f;
    }

    private static double[] BuildTwoPiF()
    {
        var f = new double[Carriers];
        for (int c = 0; c < f.Length; c++)
        {
            f[c] = 2 * Math.PI * ((BurstRenderer.FirstBin + c) * 48000.0 / BurstRenderer.FftSize);
        }

        return f;
    }

    // numpy.arange(start, stop, 0.25e-6): start + i * delta, with delta the difference of the first two values.
    private static double[] BuildLadder(double start, int steps)
    {
        double delta = (start + 0.25e-6) - start;
        var ladder = new double[steps];
        for (int i = 0; i < steps; i++)
        {
            ladder[i] = start + (i * delta);
        }

        return ladder;
    }

    // The ladder's turns for every carrier, carrier by carrier: [c * LadderSteps + i] is exp(j 2 pi f delay_i).
    private static double[] BuildTurn(bool imaginary)
    {
        var t = new double[Carriers * LadderSteps];
        for (int c = 0; c < Carriers; c++)
        {
            for (int i = 0; i < LadderSteps; i++)
            {
                double th = TwoPiF[c] * Ladder[i];
                t[(c * LadderSteps) + i] = imaginary ? Math.Sin(th) : Math.Cos(th);
            }
        }

        return t;
    }
}
