namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>
/// Receives a burst from a far station whose sample clock is not ours, following the clock through the burst: the prototype's
/// <c>modem.receive_tracked(resample, design, None, tracker=lab.track)</c> as the station calls it (<c>passes=8</c>,
/// <c>least=3</c>, <c>settle=2e-6</c>, <c>repair=False</c>, <c>fine_passes=3</c>), over <see cref="PayloadReceiver"/>,
/// <see cref="DelayResampler"/> and <see cref="BlockTracker"/>.
/// </summary>
/// <remarks>
/// <para>Each pass resamples the capture along the delay curve found so far (none at first), receives it, and takes each symbol's
/// delay from the receiver. The loop stops when no symbol moved by more than 2 microseconds after at least three passes, or at
/// the eighth; until then a three-point median of the symbols' delays is added to the curve. When the payload's constellation is
/// denser than QPSK a fine track follows: the burst is rebuilt from the decided symbols, the resampled capture is measured
/// against it in blocks a few milliseconds apart, and the capture is received again along the refined curve, up to three times; a
/// correction spanning more than 60 microseconds is not believed (the round is dropped and the loop ends), one under 0.3
/// microseconds ends it.</para>
/// <para>Where the prototype receives a trial and then drops it for spanning too much, this receives nothing: the result is the
/// same (the earlier receive stays the receiver's last), the pass is counted all the same.</para>
/// <para>The resampled capture is rounded to float for the receiver, which reads floats. One instance per receiver; not
/// thread-safe. Every buffer for the longest burst is allocated by the constructor, so a call allocates nothing once the code
/// plans for the burst's shape are cached.</para>
/// </remarks>
public sealed class TrackedReceiver
{
    /// <summary>The most passes of the per-symbol loop.</summary>
    public const int MaxSymbolPasses = 8;

    /// <summary>The fewest passes of the per-symbol loop before it may stop.</summary>
    public const int LeastSymbolPasses = 3;

    /// <summary>The most rounds of the fine track.</summary>
    public const int MaxFinePasses = 3;

    private const int Carriers = BurstRenderer.Carriers;
    private const int SymbolSamples = BurstRenderer.SymbolSamples;
    private const int Lead = BurstRenderer.DefaultLeadSamples;
    private const int MaxSymbols = 9 + StandardFormats.MaxSymbols;
    private const int MaxSamples = Lead + (MaxSymbols * SymbolSamples);
    private const double Settle = 2e-6;
    private const double Believable = 60e-6;
    private const double Converged = 0.3e-6;

    private readonly PayloadReceiver _receiver = new();
    private readonly BlockTracker _tracker = new(MaxSamples);
    private readonly float[] _wave = new float[MaxSamples];
    private readonly double[] _reference = new double[MaxSamples];
    private readonly double[] _gridRe = new double[MaxSymbols * Carriers];
    private readonly double[] _gridIm = new double[MaxSymbols * Carriers];
    private readonly double[] _body = new double[MaxSymbols * SymbolSamples];
    private readonly double[] _fftRe = new double[BurstRenderer.FftSize];
    private readonly double[] _fftIm = new double[BurstRenderer.FftSize];
    private readonly byte[] _loading = new byte[Carriers];
    private readonly byte[] _headerCoded = new byte[666];
    private readonly double[] _times = new double[MaxSymbols];
    private readonly double[] _curve = new double[MaxSymbols];
    private readonly double[] _knots;
    private readonly double[] _along;
    private readonly double[] _fineTimes;
    private readonly double[] _fineTau;
    private readonly double[] _trial;

    private bool _valid;
    private int _symbols;
    private int _passes;
    private int _symbolPasses;
    private bool _fineRan;
    private int _knotCount;

    /// <summary>Makes a receiver, with every buffer for the longest burst.</summary>
    public TrackedReceiver()
    {
        int blocks = _tracker.MaxBlocks;
        _knots = new double[blocks];
        _along = new double[blocks];
        _fineTimes = new double[blocks];
        _fineTau = new double[blocks];
        _trial = new double[blocks];
    }

    /// <summary>Whether the last call computed anything (it was accepted).</summary>
    public bool HasResult => _valid;

    /// <summary>Receives passes the last call made: the per-symbol passes, then one for each round of the fine track that was begun (a round dropped for spanning too much counts).</summary>
    public int Passes => _valid ? _passes : 0;

    /// <summary>Passes of the per-symbol loop alone.</summary>
    public int SymbolPasses => _valid ? _symbolPasses : 0;

    /// <summary>Whether the fine track ran: the payload's constellation is denser than QPSK.</summary>
    public bool FineTrackRan => _valid && _fineRan;

    /// <summary>The delay curve the per-symbol loop settled on, one value for each symbol (the opening's first) in seconds: the capture was resampled along it, and a positive value reads the capture later.</summary>
    public ReadOnlySpan<double> Curve => _valid ? _curve.AsSpan(0, _symbols) : default;

    /// <summary>The knots (seconds from the burst's first sample) of the curve the fine track ended on: the last round it believed, or the symbols' own times when it believed none. Empty when it did not run.</summary>
    public ReadOnlySpan<double> FineKnots => _valid && _fineRan ? _knots.AsSpan(0, _knotCount) : default;

    /// <summary>The delay at each of <see cref="FineKnots"/>, in seconds.</summary>
    public ReadOnlySpan<double> FineDelays => _valid && _fineRan ? _along.AsSpan(0, _knotCount) : default;

    /// <summary>The underlying receiver's per-symbol delays from the last receive, in seconds (what remained to take out of the final resample).</summary>
    public ReadOnlySpan<double> Delays => _receiver.Delays;

    /// <summary>Each symbol's miss from the last receive; see <see cref="PayloadReceiver.Miss"/>.</summary>
    public ReadOnlySpan<double> Miss => _receiver.Miss;

    /// <summary>Symbols the burst's opening has; see <see cref="PayloadReceiver.KnownSymbols"/>.</summary>
    public int KnownSymbols => _receiver.KnownSymbols;

    /// <summary>The measured quality of the path on each of 333 carriers when the last check passed; see <see cref="PayloadReceiver.Quality"/>.</summary>
    public ReadOnlySpan<double> Quality => _receiver.Quality;

    /// <summary>Whether the last burst's quality collapsed part-way; see <see cref="PayloadReceiver.Sudden"/>.</summary>
    public bool Sudden => _receiver.Sudden;

    /// <summary>The carriers the tone guard raised the noise estimate on in the last receive.</summary>
    public int GuardedCarriers => _receiver.GuardedCarriers;

    /// <summary>The decided symbols' real parts from the last receive; see <see cref="PayloadReceiver.DecidedReal"/>.</summary>
    public ReadOnlySpan<double> DecidedReal => _receiver.DecidedReal;

    /// <summary>The decided symbols' imaginary parts from the last receive.</summary>
    public ReadOnlySpan<double> DecidedImaginary => _receiver.DecidedImaginary;

    /// <summary>
    /// Receives the burst whose first training sample is <paramref name="start"/> in the capture, which must hold
    /// <see cref="PayloadReceiver.SamplesNeeded"/> samples from there (the burst's own; what lies before it and after it is read
    /// where the delays reach, and the capture's first and last samples stand for what lies beyond them). Arguments and result as
    /// <see cref="PayloadReceiver.Receive"/>, for the last receive of the loop.
    /// </summary>
    /// <exception cref="ArgumentException">The header's bits are fewer than 51, or the payload span is smaller than <see cref="PayloadReceiver.PayloadRoom"/>.</exception>
    public PayloadReceiveResult Receive(ReadOnlySpan<float> capture, int start, in BurstHeader header, ReadOnlySpan<byte> headerBits, Span<byte> payload)
    {
        _valid = false;
        int need = PayloadReceiver.SamplesNeeded(header);
        if (need < 0)
        {
            return _receiver.Receive(capture, start, header, headerBits, payload);
        }

        if (start < 0 || capture.Length - start < need)
        {
            // Refused as the receiver refuses it (it also clears its own last result and checks the arguments).
            return _receiver.Receive(capture, -1, header, headerBits, payload);
        }

        FormatSpec spec = StandardFormats.Describe(header.Format);
        int n = need / SymbolSamples;
        int length = Lead + need;
        int at = start - Lead;
        for (int i = 0; i < n; i++)
        {
            _times[i] = ((i * SymbolSamples) + (SymbolSamples / 2.0)) / BurstRenderer.SampleRate;
        }

        _curve.AsSpan(0, n).Clear();
        PayloadReceiveResult result = default;
        int passes = 0;
        for (int p = 0; p < MaxSymbolPasses; p++)
        {
            DelayResampler.Resample(capture, at, length, _times.AsSpan(0, n), _curve.AsSpan(0, n), Lead, _wave);
            result = _receiver.Receive(_wave.AsSpan(0, length), Lead, header, headerBits, payload);
            passes = p + 1;
            ReadOnlySpan<double> tau = _receiver.Delays;
            double largest = 0;
            for (int i = 0; i < n; i++)
            {
                largest = Math.Max(largest, Math.Abs(tau[i]));
            }

            if ((p >= LeastSymbolPasses - 1 && largest < Settle) || p == MaxSymbolPasses - 1)
            {
                for (int i = 0; i < n; i++)
                {
                    _curve[i] += tau[i];
                }

                break;
            }

            // A symbol whose delay stands apart from both neighbours is pulled back to them: the median of three.
            for (int i = 0; i < n; i++)
            {
                double a = tau[Math.Max(i - 1, 0)];
                double b = tau[i];
                double c = tau[Math.Min(i + 1, n - 1)];
                _curve[i] += Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
            }
        }

        _symbols = n;
        _symbolPasses = passes;
        _fineRan = spec.ConstellationBits > 2;
        _knotCount = n;
        if (_fineRan)
        {
            _times.AsSpan(0, n).CopyTo(_knots);
            _curve.AsSpan(0, n).CopyTo(_along);
            for (int round = 0; round < MaxFinePasses; round++)
            {
                BuildReference(header, headerBits, spec, n);
                DelayResampler.Resample(capture, at, length, _knots.AsSpan(0, _knotCount), _along.AsSpan(0, _knotCount), Lead, _wave);
                int m = _tracker.Track(_wave.AsSpan(0, length), _reference.AsSpan(0, length), _fineTimes, _fineTau);
                passes++;
                if (m == 0)
                {
                    break;
                }

                double low = _fineTau[0];
                double high = _fineTau[0];
                int segment = 0;
                for (int i = 0; i < m; i++)
                {
                    low = Math.Min(low, _fineTau[i]);
                    high = Math.Max(high, _fineTau[i]);
                    _trial[i] = DelayResampler.Interpolate(_fineTimes[i], _knots.AsSpan(0, _knotCount), _along.AsSpan(0, _knotCount), ref segment) + _fineTau[i];
                }

                double ptp = high - low;
                if (ptp > Believable)
                {
                    break;
                }

                DelayResampler.Resample(capture, at, length, _fineTimes.AsSpan(0, m), _trial.AsSpan(0, m), Lead, _wave);
                result = _receiver.Receive(_wave.AsSpan(0, length), Lead, header, headerBits, payload);
                _fineTimes.AsSpan(0, m).CopyTo(_knots);
                _trial.AsSpan(0, m).CopyTo(_along);
                _knotCount = m;
                if (ptp < Converged)
                {
                    break;
                }
            }
        }

        _passes = passes;
        _valid = true;
        return result;
    }

    // The burst rebuilt from the known opening and the last receive's decided symbols, behind the lead-in's silence.
    private void BuildReference(in BurstHeader header, ReadOnlySpan<byte> headerBits, FormatSpec spec, int n)
    {
        FormatOpening opening = spec.Opening;
        int band = HeaderReader.OpeningCarriers(opening);
        int known = _receiver.KnownSymbols;
        double gain = Math.Sqrt((double)spec.Carriers / band);
        BurstGrid.BuildOpening(opening, headerBits, gain, _gridRe, _gridIm, _loading, _headerCoded);
        _receiver.DecidedReal.CopyTo(_gridRe.AsSpan(known * Carriers));
        _receiver.DecidedImaginary.CopyTo(_gridIm.AsSpan(known * Carriers));
        BurstGrid.Transform(_gridRe, _gridIm, n, _fftRe, _fftIm, _body);
        _reference.AsSpan(0, Lead).Clear();
        _body.AsSpan(0, n * SymbolSamples).CopyTo(_reference.AsSpan(Lead));
    }
}
