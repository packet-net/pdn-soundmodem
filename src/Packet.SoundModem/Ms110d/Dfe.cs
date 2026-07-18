using M0LTE.Ofdm;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// Fractionally-spaced (T/2) decision-feedback equalizer for the Appendix D serial-tone
/// receiver — textbook DFE (Proakis, <i>Digital Communications</i>, ch. 9) with NLMS
/// adaptation (Haykin, <i>Adaptive Filter Theory</i>); GPL-clean, designed from the spec's
/// probe structure only. Tap counts per mini-probe class follow design §2.5:
/// K=48 → 32 FF / 22 FB, K=32 → 24 FF / 12 FB, K=24 → 16 FF / 6 FB.
/// </summary>
/// <remarks>
/// Convention: the feed-forward window holds T/2 input samples newest-first
/// (<c>window[i] = x[2n + lead − i]</c>); the feedback window holds prior symbol decisions
/// (<c>past[j] = d̂[n−1−j]</c>). Output y = Σ ff·window + Σ fb·past — feedback signs live in
/// the taps. Initial taps come from a regularized least-squares solve over the known
/// preamble tail + first probe (<see cref="BeginTraining"/>/<see cref="AddTrainingRow"/>/
/// <see cref="SolveTraining"/>); per-probe refresh uses <see cref="Nlms"/>.
/// </remarks>
public sealed class Dfe
{
    private readonly Cf[] _ff;
    private readonly Cf[] _fb;
    private Cf[,]? _gram;
    private Cf[]? _rhs;
    private int _trainingRows;

    /// <summary>Creates a DFE with the given tap counts.</summary>
    public Dfe(int ffTaps, int fbTaps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ffTaps, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(fbTaps);
        _ff = new Cf[ffTaps];
        _fb = new Cf[fbTaps];
    }

    /// <summary>Feed-forward (T/2) tap count.</summary>
    public int FfTaps => _ff.Length;

    /// <summary>Feedback (symbol-spaced) tap count.</summary>
    public int FbTaps => _fb.Length;

    /// <summary>Equalizes one symbol.</summary>
    public Cf Equalize(ReadOnlySpan<Cf> window, ReadOnlySpan<Cf> past)
    {
        var y = Cf.Zero;
        for (int i = 0; i < _ff.Length; i++)
        {
            y += _ff[i] * window[i];
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            y += _fb[j] * past[j];
        }

        return y;
    }

    /// <summary>One normalized-LMS update toward <paramref name="desired"/>; returns the
    /// pre-update equalizer output.</summary>
    public Cf Nlms(ReadOnlySpan<Cf> window, ReadOnlySpan<Cf> past, Cf desired, float mu)
    {
        Cf y = Equalize(window, past);
        Cf error = desired - y;
        float norm = 1e-6f;
        for (int i = 0; i < _ff.Length; i++)
        {
            norm += window[i].Cnorm();
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            norm += past[j].Cnorm();
        }

        float g = mu / norm;
        Cf scaled = error * g;
        for (int i = 0; i < _ff.Length; i++)
        {
            _ff[i] += scaled * window[i].Conj();
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            _fb[j] += scaled * past[j].Conj();
        }

        return y;
    }

    /// <summary>Copies the current taps (FF then FB) into a new array.</summary>
    public Cf[] SnapshotTaps()
    {
        var taps = new Cf[_ff.Length + _fb.Length];
        _ff.CopyTo(taps, 0);
        _fb.CopyTo(taps, _ff.Length);
        return taps;
    }

    /// <summary>Installs taps from a <see cref="SnapshotTaps"/> array.</summary>
    public void LoadTaps(ReadOnlySpan<Cf> taps)
    {
        taps[.._ff.Length].CopyTo(_ff);
        taps[_ff.Length..].CopyTo(_fb);
    }

    /// <summary>Installs the linear interpolation (1−α)·a + α·b of two snapshots — the
    /// per-symbol tap trajectory across a data block bracketed by two solved probes.</summary>
    public void LoadInterpolatedTaps(ReadOnlySpan<Cf> a, ReadOnlySpan<Cf> b, float alpha)
    {
        float inverse = 1 - alpha;
        for (int i = 0; i < _ff.Length; i++)
        {
            _ff[i] = (a[i] * inverse) + (b[i] * alpha);
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            int i = _ff.Length + j;
            _fb[j] = (a[i] * inverse) + (b[i] * alpha);
        }
    }

    /// <summary>Starts accumulating least-squares training rows (clears any previous
    /// accumulation).</summary>
    public void BeginTraining()
    {
        int n = _ff.Length + _fb.Length;
        _gram = new Cf[n, n];
        _rhs = new Cf[n];
        _trainingRows = 0;
    }

    /// <summary>Adds one training row: the FF window and known past symbols observed when
    /// the known symbol <paramref name="desired"/> was current. <paramref name="weight"/>
    /// scales the row's least-squares influence — known probe symbols get authoritative
    /// weight, decision-directed rows advisory weight (wrong decisions under a rotated
    /// constellation are self-confirming and must never outvote the probes).</summary>
    public void AddTrainingRow(ReadOnlySpan<Cf> window, ReadOnlySpan<Cf> past, Cf desired, float weight = 1f)
    {
        if (_gram is null || _rhs is null)
        {
            throw new InvalidOperationException("call BeginTraining first");
        }

        int n = _ff.Length + _fb.Length;
        Span<Cf> row = stackalloc Cf[n];
        for (int i = 0; i < _ff.Length; i++)
        {
            row[i] = window[i];
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            row[_ff.Length + j] = past[j];
        }

        for (int i = 0; i < n; i++)
        {
            Cf ci = row[i].Conj() * weight;
            for (int j = i; j < n; j++)
            {
                _gram[i, j] += ci * row[j];
            }

            _rhs[i] += ci * desired;
        }

        _trainingRows++;
    }

    /// <summary>Solves the accumulated regularized normal equations and installs the taps.
    /// Returns false (leaving taps unchanged) if the system was degenerate.
    /// With <paramref name="anchorToCurrentTaps"/> the ridge pulls toward the CURRENT taps
    /// instead of zero — the per-probe tracking update on fading channels (a Kalman-style
    /// prior: K fresh rows dominate the directions the probe observed, the anchor carries
    /// everything else).</summary>
    public bool SolveTraining(float regularization = 1e-3f, bool anchorToCurrentTaps = false)
    {
        if (_gram is null || _rhs is null ||
            (!anchorToCurrentTaps && _trainingRows < _ff.Length + _fb.Length) ||
            _trainingRows == 0)
        {
            _gram = null;
            _rhs = null;
            return false;
        }

        int n = _ff.Length + _fb.Length;
        double trace = 0;
        for (int i = 0; i < n; i++)
        {
            trace += _gram[i, i].Re;
        }

        float lambda = (float)(regularization * trace / n) + 1e-9f;
        for (int i = 0; i < n; i++)
        {
            _gram[i, i] += new Cf(lambda, 0);
            if (anchorToCurrentTaps)
            {
                Cf current = i < _ff.Length ? _ff[i] : _fb[i - _ff.Length];
                _rhs[i] += current * lambda;
            }

            for (int j = 0; j < i; j++)
            {
                _gram[i, j] = _gram[j, i].Conj(); // fill the lower triangle
            }
        }

        var solution = new Cf[n];
        if (!CholeskySolve(_gram, _rhs, solution))
        {
            _gram = null;
            _rhs = null;
            return false;
        }

        Array.Copy(solution, 0, _ff, 0, _ff.Length);
        Array.Copy(solution, _ff.Length, _fb, 0, _fb.Length);
        _gram = null;
        _rhs = null;
        return true;
    }

    /// <summary>Hermitian positive-definite solve via complex Cholesky (in place on copies).</summary>
    private static bool CholeskySolve(Cf[,] a, Cf[] b, Cf[] x)
    {
        int n = b.Length;
        var l = new Cf[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                Cf sum = a[i, j];
                for (int k = 0; k < j; k++)
                {
                    sum -= l[i, k] * l[j, k].Conj();
                }

                if (i == j)
                {
                    if (sum.Re <= 0)
                    {
                        return false;
                    }

                    l[i, j] = new Cf(MathF.Sqrt(sum.Re), 0);
                }
                else
                {
                    float inv = 1f / l[j, j].Re;
                    l[i, j] = sum * inv;
                }
            }
        }

        // Forward substitution L y = b, then backward Lᴴ x = y.
        var y = new Cf[n];
        for (int i = 0; i < n; i++)
        {
            Cf sum = b[i];
            for (int k = 0; k < i; k++)
            {
                sum -= l[i, k] * y[k];
            }

            y[i] = sum * (1f / l[i, i].Re);
        }

        for (int i = n - 1; i >= 0; i--)
        {
            Cf sum = y[i];
            for (int k = i + 1; k < n; k++)
            {
                sum -= l[k, i].Conj() * x[k];
            }

            x[i] = sum * (1f / l[i, i].Re);
        }

        return true;
    }
}
