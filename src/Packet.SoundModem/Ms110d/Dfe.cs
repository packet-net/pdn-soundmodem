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
/// preamble tail + first probe (<see cref="BeginTraining"/>/
/// <see cref="AddTrainingRow(ReadOnlySpan{Cf}, ReadOnlySpan{Cf}, Cf, float)"/>/
/// <see cref="SolveTraining"/>); per-probe refresh uses <see cref="Nlms"/>.
/// </remarks>
public sealed class Dfe
{
    private readonly Cf[] _ff;
    private readonly Cf[] _fb;
    private readonly Cf[,] _gramStore;
    private readonly Cf[] _rhsStore;
    private readonly Cf[,] _cholL;
    private readonly Cf[] _cholY;
    private readonly Cf[] _solution;
    private readonly Cf[,] _seedGram;
    private readonly Cf[] _seedColumn;
    private readonly Cf[,] _tirGram;
    private readonly Cf[] _tirRhs;
    private readonly Cf[] _tirSol;
    private readonly Cf[] _tirWin;
    private Cf[,]? _gram;
    private Cf[]? _rhs;
    private int _trainingRows;
    private float _trainingWeightSum;
    private float _trainingTargetEnergy;
    private Cf[,]? _savedGram;
    private Cf[]? _savedRhs;
    private int _savedTrainingRows;
    private float _savedTrainingWeightSum;
    private float _savedTrainingTargetEnergy;

    /// <summary>Creates a DFE with the given tap counts.</summary>
    public Dfe(int ffTaps, int fbTaps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ffTaps, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(fbTaps);
        _ff = new Cf[ffTaps];
        _fb = new Cf[fbTaps];
        int n = ffTaps + fbTaps;
        _gramStore = new Cf[n, n];
        _rhsStore = new Cf[n];
        _cholL = new Cf[n, n];
        _cholY = new Cf[n];
        _solution = new Cf[n];
        _seedGram = new Cf[n, n];
        _seedColumn = new Cf[n];
        _tirGram = new Cf[n, n];
        _tirRhs = new Cf[n];
        _tirSol = new Cf[n];
        _tirWin = new Cf[n];
    }

    /// <summary>Feed-forward (T/2) tap count.</summary>
    public int FfTaps => _ff.Length;

    /// <summary>Diagnostic: Σ|ff|² of the current feed-forward taps — the equalizer's
    /// white-noise power gain (§B3.3 fade-crossing corpse instrument).</summary>
    public float FfEnergy
    {
        get
        {
            float e = 0f;
            foreach (Cf t in _ff)
            {
                e += t.Cnorm();
            }

            return e;
        }
    }

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

    /// <summary>Advances the taps along an interpolated base trajectory: adds
    /// (<paramref name="to"/> − <paramref name="from"/>) to the current taps, so a
    /// deviation the RLS recursion has accumulated on top of the previous base carries
    /// over to the new one unchanged (phase-b-plan §B2.1: the base carries the
    /// probe-anchored channel trajectory, RLS tracks only the residual).</summary>
    public void TranslateTaps(ReadOnlySpan<Cf> from, ReadOnlySpan<Cf> to)
    {
        for (int i = 0; i < _ff.Length; i++)
        {
            _ff[i] += to[i] - from[i];
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            int i = _ff.Length + j;
            _fb[j] += to[i] - from[i];
        }
    }

    /// <summary>Rotates every tap by <paramref name="rotor"/> (a unit phasor): the §B2.1
    /// per-probe phase re-anchor. The channel's common rotation moves the feed-forward
    /// response AND the post-cursor ISI the feedback taps cancel, so the full tap vector
    /// rotates together.</summary>
    public void RotateTaps(Cf rotor)
    {
        for (int i = 0; i < _ff.Length; i++)
        {
            _ff[i] *= rotor;
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            _fb[j] *= rotor;
        }
    }

    /// <summary>Starts accumulating least-squares training rows (clears any previous
    /// accumulation).</summary>
    public void BeginTraining()
    {
        Array.Clear(_gramStore);
        Array.Clear(_rhsStore);
        _gram = _gramStore;
        _rhs = _rhsStore;
        _trainingRows = 0;
        _trainingWeightSum = 0;
        _trainingTargetEnergy = 0;
    }

    /// <summary>Saves the in-progress training accumulation (Gram/RHS/row count) so a
    /// nested re-training pass (turbo re-equalization) can run without destroying the
    /// rows accumulated for the NEXT probe solve. Restore with
    /// <see cref="RestoreTraining"/>.</summary>
    public void SnapshotTraining()
    {
        _savedGram ??= new Cf[_gramStore.GetLength(0), _gramStore.GetLength(1)];
        _savedRhs ??= new Cf[_rhsStore.Length];
        Array.Copy(_gramStore, _savedGram, _gramStore.Length);
        Array.Copy(_rhsStore, _savedRhs, _rhsStore.Length);
        _savedTrainingRows = _trainingRows;
        _savedTrainingWeightSum = _trainingWeightSum;
        _savedTrainingTargetEnergy = _trainingTargetEnergy;
    }

    /// <summary>Restores the accumulation saved by <see cref="SnapshotTraining"/>.</summary>
    public void RestoreTraining()
    {
        if (_savedGram is null || _savedRhs is null)
        {
            throw new InvalidOperationException("RestoreTraining without SnapshotTraining");
        }

        Array.Copy(_savedGram, _gramStore, _gramStore.Length);
        Array.Copy(_savedRhs, _rhsStore, _rhsStore.Length);
        _gram = _gramStore;
        _rhs = _rhsStore;
        _trainingRows = _savedTrainingRows;
        _trainingWeightSum = _savedTrainingWeightSum;
        _trainingTargetEnergy = _savedTrainingTargetEnergy;
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
        _trainingWeightSum += weight;
        _trainingTargetEnergy += weight * desired.Cnorm();
    }

    /// <summary>Soft-label variant of
    /// <see cref="AddTrainingRow(ReadOnlySpan{Cf}, ReadOnlySpan{Cf}, Cf, float)"/> for rows
    /// whose <paramref name="past"/> entries are expectations E[x] rather than known
    /// symbols. The EM-correct Gram needs E[x·x̄] = |E[x]|² + Var on the feedback
    /// <b>diagonal</b> (cross terms factor under symbol independence; feed-forward columns
    /// are received data, not latents) — <paramref name="pastVariance"/> supplies the
    /// per-entry variance added there. Zero variances reduce bit-identically to the base
    /// overload.</summary>
    public void AddTrainingRow(
        ReadOnlySpan<Cf> window, ReadOnlySpan<Cf> past, ReadOnlySpan<float> pastVariance,
        Cf desired, float weight = 1f)
    {
        AddTrainingRow(window, past, desired, weight);
        for (int j = 0; j < _fb.Length; j++)
        {
            _gram![_ff.Length + j, _ff.Length + j] += new Cf(weight * pastVariance[j], 0);
        }
    }

    /// <summary>Solves the accumulated regularized normal equations and installs the taps.
    /// Returns false (leaving taps unchanged) if the system was degenerate.
    /// With <paramref name="anchorToCurrentTaps"/> the ridge pulls toward the CURRENT taps
    /// instead of zero — the per-probe tracking update on fading channels (a Kalman-style
    /// prior: K fresh rows dominate the directions the probe observed, the anchor carries
    /// everything else). <paramref name="ffNoisePower"/> (channel-truth genie only) adds
    /// σ²·Σweight to the feed-forward Gram diagonal — the term noisy rows contribute
    /// implicitly — so a solve over noise-free rows still yields the MMSE equalizer rather
    /// than the zero-forcing one (feedback regressors are decisions, noise-free either way).</summary>
    public bool SolveTraining(float regularization = 1e-3f, bool anchorToCurrentTaps = false, float ffNoisePower = 0f)
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
        if (ffNoisePower > 0)
        {
            float noiseDiag = ffNoisePower * _trainingWeightSum;
            for (int i = 0; i < _ff.Length; i++)
            {
                _gram[i, i] += new Cf(noiseDiag, 0);
            }
        }

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

        if (!CholeskyFactor(_gram, n))
        {
            _gram = null;
            _rhs = null;
            return false;
        }

        CholeskySubstitute(_rhs, _solution, n);
        Array.Copy(_solution, 0, _ff, 0, _ff.Length);
        Array.Copy(_solution, _ff.Length, _fb, 0, _fb.Length);
        _gram = null;
        _rhs = null;
        return true;
    }

    /// <summary>Result of <see cref="SolveTrainingTir"/>. <see cref="Lag"/> = 0 means the
    /// null (full-inversion) candidate won — feed-forward taps installed, no designed echo.
    /// <see cref="Lag"/> &gt; 0 means the shortened solve was accepted: the post-FF response
    /// is ≈ x[u] + <see cref="Coefficient"/>·x[u−Lag] (+ <see cref="Coefficient2"/>·x[u−Lag2]
    /// when <see cref="Lag2"/> &gt; 0 — the §B3.3 straddle pair, Lag2 = Lag ± 1) and the
    /// echo model must carry those lags. <see cref="SseNull"/>/<see cref="SseTir"/> are the
    /// exact (unridged) residual sums for diagnostics.</summary>
    public readonly record struct TirSolve(
        bool Solved, int Lag, Cf Coefficient, int Lag2, Cf Coefficient2, float SseNull, float SseTir);

    /// <summary>Target-impulse-response (channel-shortening) variant of
    /// <see cref="SolveTraining"/> for the §B3.3 turbo re-solve. Rows must have been
    /// accumulated with the symbol history in the feedback columns (not zeros). Solves the
    /// unit-tap-constrained shortening problem min |ff·window + b·x[u−d] − x[u]|² —
    /// jointly linear in (ff, b) because the lag-0 target tap is pinned at 1 — for the
    /// feed-forward-only null candidate and every single-lag candidate d ≤
    /// <paramref name="maxLag"/>, and installs the best feed-forward taps (feedback taps
    /// are left untouched; the turbo equalizes with zeroed feedback). A shortened candidate
    /// is accepted only when it beats the null by 4·ln(L)·SSE₀/rows — 4× the noise-only
    /// expectation of the best-of-L free-parameter reduction (the same construction as the
    /// chain-BCJR 0.04·|h1|² echo floor), so echo-free frames keep today's full-inversion
    /// solve exactly. Ridge and anchor-to-current-taps semantics mirror
    /// <see cref="SolveTraining"/> with λ scaled by subset trace/size; the accumulation is
    /// consumed either way.</summary>
    public TirSolve SolveTrainingTir(float regularization, float ffNoisePower, int maxLag, bool allowPair = true)
    {
        if (_gram is null || _rhs is null || _trainingRows == 0)
        {
            _gram = null;
            _rhs = null;
            return new TirSolve(false, 0, Cf.Zero, 0, Cf.Zero, 0f, 0f);
        }

        int f = _ff.Length;
        maxLag = Math.Min(maxLag, _fb.Length);
        Span<int> one = stackalloc int[1];
        Span<int> two = stackalloc int[2];
        float sseNull = SolveSubset(default, regularization, ffNoisePower, _tirSol);
        if (float.IsNaN(sseNull))
        {
            _gram = null;
            _rhs = null;
            return new TirSolve(false, 0, Cf.Zero, 0, Cf.Zero, 0f, 0f);
        }

        Array.Copy(_tirSol, _tirWin, f);
        int bestLag = 0;
        var bestB = Cf.Zero;
        float bestSse = float.MaxValue;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            one[0] = lag - 1;
            float sse = SolveSubset(one, regularization, ffNoisePower, _tirSol);
            if (!float.IsNaN(sse) && sse < bestSse)
            {
                bestSse = sse;
                bestLag = lag;
                bestB = _tirSol[f];
                Array.Copy(_tirSol, _tirWin, f);
            }
        }

        float threshold = 4f * MathF.Log(Math.Max(2, maxLag)) * sseNull / _trainingRows;
        bool accept = bestLag > 0 && bestSse < sseNull - threshold;
        int lag2 = 0;
        var bestB2 = Cf.Zero;
        if (accept && !allowPair)
        {
            // Pair candidates suppressed (§B3.3: label-trust gate — the demod's hard
            // iteration 0 cancels the adjacent tap with re-encoded labels that can be
            // ~half wrong, injecting unpriced observation error; measured flipping a
            // marginal WN6 block out of convergence at 146× the point's BER). The
            // accepted single-lag solve stands unchanged.
        }
        else if (accept)
        {
            // §B3.3 straddle pair: a fractional-delay physical echo (the Poor channel's
            // 2 ms path ≈ 4.8 T) splits across the two lags bracketing it, so the two
            // candidates tied to the ESTABLISHED lag are {d−1, d} and {d, d+1}. One extra
            // free parameter over the accepted single-lag solve and only two candidates
            // (no L-fold search), so the margin drops the ln L factor: the noise-only
            // best-of-two reduction is ≈ (1 + ln 2)·SSE₀/rows and 4·SSE₀/rows keeps a
            // >2× safety factor — the same construction as the single-lag acceptance.
            float pairGate = bestSse - (4f * sseNull / _trainingRows);
            for (int side = -1; side <= 1; side += 2)
            {
                int neighbor = bestLag + side;
                if (neighbor < 1 || neighbor > maxLag)
                {
                    continue;
                }

                two[0] = Math.Min(bestLag, neighbor) - 1;
                two[1] = Math.Max(bestLag, neighbor) - 1;
                float sse = SolveSubset(two, regularization, ffNoisePower, _tirSol);
                if (!float.IsNaN(sse) && sse < pairGate)
                {
                    pairGate = sse;
                    bestSse = sse;
                    lag2 = neighbor;
                    bestB = _tirSol[f + (neighbor < bestLag ? 1 : 0)];
                    bestB2 = _tirSol[f + (neighbor < bestLag ? 0 : 1)];
                    Array.Copy(_tirSol, _tirWin, f);
                }
            }
        }
        else
        {
            // Reinstate the null solution (the winner loop overwrote _tirWin).
            sseNull = SolveSubset(default, regularization, ffNoisePower, _tirWin);
            bestLag = 0;
            bestB = Cf.Zero;
            bestSse = sseNull;
        }

        Array.Copy(_tirWin, 0, _ff, 0, f);
        _gram = null;
        _rhs = null;

        // The LS coefficients sit on the SUBTRACTED side of the target
        // (ff·window ≈ x[u] − b·x[u−d] − b₂·x[u−d₂]), so the post-FF echo
        // coefficients are −b/−b₂.
        return new TirSolve(
            true, bestLag, new Cf(-bestB.Re, -bestB.Im),
            lag2, new Cf(-bestB2.Re, -bestB2.Im), sseNull, bestSse);
    }

    /// <summary>Solves the regularized subset system {feed-forward taps} ∪ {feedback
    /// columns <paramref name="fbIndices"/>} (empty for feed-forward only) from the live
    /// training accumulation without consuming it, leaving the solution in
    /// <paramref name="sol"/>. Returns the exact unridged residual sum
    /// Σw·|desired − sol·row|² (targetEnergy − 2Re(solᴴr) + solᴴG₀sol, with the
    /// ridge/anchor and genie noise-diagonal terms backed out), or NaN if the subset was
    /// degenerate. <paramref name="fbIndices"/> must be ascending (the index map must
    /// preserve order so only the stored upper triangle of the accumulation is read).</summary>
    private float SolveSubset(ReadOnlySpan<int> fbIndices, float regularization, float ffNoisePower, Cf[] sol)
    {
        int f = _ff.Length;
        int m = f + fbIndices.Length;
        Cf[,] gram = _gram!;
        Cf[] rhs = _rhs!;

        // Subset copy (index map preserves order, so only the stored upper triangle of the
        // accumulation is read; the lower triangle is filled by conjugation here).
        for (int i = 0; i < m; i++)
        {
            int gi = i < f ? i : f + fbIndices[i - f];
            for (int j = i; j < m; j++)
            {
                int gj = j < f ? j : f + fbIndices[j - f];
                Cf v = gram[gi, gj];
                _tirGram[i, j] = v;
                if (j != i)
                {
                    _tirGram[j, i] = v.Conj();
                }
            }
        }

        float noiseDiag = ffNoisePower > 0 ? ffNoisePower * _trainingWeightSum : 0f;
        if (noiseDiag > 0)
        {
            for (int i = 0; i < f; i++)
            {
                _tirGram[i, i] += new Cf(noiseDiag, 0);
            }
        }

        double trace = 0;
        for (int i = 0; i < m; i++)
        {
            trace += _tirGram[i, i].Re;
        }

        float lambda = (float)(regularization * trace / m) + 1e-9f;
        for (int k = 0; k < m; k++)
        {
            int gk = k < f ? k : f + fbIndices[k - f];
            Cf anchor = k < f ? _ff[k] : _fb[fbIndices[k - f]];
            _tirGram[k, k] += new Cf(lambda, 0);
            _tirRhs[k] = rhs[gk] + (anchor * lambda);
        }

        if (!CholeskyFactor(_tirGram, m))
        {
            return float.NaN;
        }

        CholeskySubstitute(_tirRhs, sol, m);

        // Exact data residual of this solution: back the ridge and noise-diagonal terms
        // out of the quadratic form so candidates compare on what the rows actually say.
        double lin = 0, quad = 0, nrm = 0, nrmFf = 0;
        for (int i = 0; i < m; i++)
        {
            int gi = i < f ? i : f + fbIndices[i - f];
            lin += (sol[i].Conj() * rhs[gi]).Re;
            var acc = Cf.Zero;
            for (int j = 0; j < m; j++)
            {
                acc += _tirGram[i, j] * sol[j];
            }

            quad += (sol[i].Conj() * acc).Re;
            nrm += sol[i].Cnorm();
            if (i < f)
            {
                nrmFf += sol[i].Cnorm();
            }
        }

        double sse = _trainingTargetEnergy + quad - (2 * lin) - (lambda * nrm) - (noiseDiag * nrmFf);
        return (float)Math.Max(0, sse);
    }

    /// <summary>Cholesky-factorizes the leading <paramref name="n"/>×<paramref name="n"/>
    /// block of the Hermitian positive-definite matrix into the preallocated lower-triangle
    /// scratch; returns false if not positive-definite. Every scratch entry is written
    /// before it is read, so no clearing between calls.</summary>
    private bool CholeskyFactor(Cf[,] a, int n)
    {
        Cf[,] l = _cholL;
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

        return true;
    }

    /// <summary>Solves L·Lᴴ·x = b (size <paramref name="n"/>) against the factor left by
    /// <see cref="CholeskyFactor"/>: forward substitution L y = b, then backward
    /// Lᴴ x = y.</summary>
    private void CholeskySubstitute(Cf[] b, Cf[] x, int n)
    {
        Cf[,] l = _cholL;
        Cf[] y = _cholY;
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
    }

    // ------------------------------------------------------------------ RLS tracking (Phase B)

    private Cf[,]? _p;
    private float _lambda;
    private readonly Cf[] _pxScratch = new Cf[64];

    /// <summary>Initializes the RLS inverse-correlation matrix P as a scaled identity.
    /// Called after the acquisition batch-LS solve seeds the taps.</summary>
    public void BeginRls(float lambda, float pInit = 1.0f)
    {
        int n = _ff.Length + _fb.Length;
        _p = new Cf[n, n];
        for (int i = 0; i < n; i++)
        {
            _p[i, i] = new Cf(pInit, 0);
        }

        _lambda = lambda;
    }

    /// <summary>Seeds P from the inverse of the accumulated training Gram (MMSE-calibrated
    /// initialization — design §2.5: "RLS subsumes the MMSE-init"). Falls back to scaled
    /// identity if the Gram is degenerate. <paramref name="ffNoisePower"/> as in
    /// <see cref="SolveTraining"/> (channel-truth genie only).</summary>
    public void SeedRlsFromTraining(float regularization, float pFallback = 1.0f, float ffNoisePower = 0f)
    {
        if (_gram is null || _p is null)
        {
            return;
        }

        int n = _ff.Length + _fb.Length;
        Cf[,] gram = _seedGram;
        Array.Copy(_gram, gram, _gram.Length);
        if (ffNoisePower > 0)
        {
            float noiseDiag = ffNoisePower * _trainingWeightSum;
            for (int i = 0; i < _ff.Length; i++)
            {
                gram[i, i] += new Cf(noiseDiag, 0);
            }
        }

        double trace = 0;
        for (int i = 0; i < n; i++)
        {
            trace += gram[i, i].Re;
        }

        float ridge = (float)(regularization * trace / n) + 1e-9f;
        for (int i = 0; i < n; i++)
        {
            gram[i, i] += new Cf(ridge, 0);
            for (int j = 0; j < i; j++)
            {
                gram[i, j] = gram[j, i].Conj();
            }
        }

        // Invert by solving each identity column against a single factorization — the
        // factor depends only on the matrix, so reusing it across columns is bit-identical
        // to refactorizing per column.
        bool ok = CholeskyFactor(gram, n);
        if (ok)
        {
            Cf[] e = _seedColumn;
            Array.Clear(e);
            for (int col = 0; col < n; col++)
            {
                e[col] = new Cf(1, 0);
                CholeskySubstitute(e, _solution, n);
                for (int row = 0; row < n; row++)
                {
                    _p[row, col] = _solution[row];
                }

                e[col] = Cf.Zero;
            }
        }

        if (!ok)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    _p[i, j] = i == j ? new Cf(pFallback, 0) : Cf.Zero;
                }
            }
        }
    }

    /// <summary>One recursive-least-squares update toward <paramref name="desired"/>;
    /// returns the pre-update equalizer output. <paramref name="weight"/> is the row's
    /// least-squares influence (probe rows authoritative, DD rows advisory) and enters the
    /// recursion consistently — gain denominator AND P update — as weighted RLS requires;
    /// see the derivation comment in the body.</summary>
    public Cf RlsUpdate(ReadOnlySpan<Cf> window, ReadOnlySpan<Cf> past, Cf desired, float weight = 1f)
    {
        Cf y = Equalize(window, past);
        if (_p is null)
        {
            return y;
        }

        int n = _ff.Length + _fb.Length;
        Span<Cf> u = stackalloc Cf[n];
        for (int i = 0; i < _ff.Length; i++)
        {
            u[i] = window[i];
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            u[_ff.Length + j] = past[j];
        }

        // px = P · u*
        for (int i = 0; i < n; i++)
        {
            var acc = Cf.Zero;
            for (int j = 0; j < n; j++)
            {
                acc += _p[i, j] * u[j].Conj();
            }

            _pxScratch[i] = acc;
        }

        // Weighted RLS. A row of weight w enters the exponentially-forgotten LS cost as
        // w·|d − uᵀθ|², i.e. an effective regressor √w·u — so w must scale BOTH the gain
        // denominator and the P (inverse-correlation) update:
        //   denom = λ + w·uᵀPu*,  θ += w·e·Pu*/denom,  P ← λ⁻¹(P − w·(Pu*)(Pu*)ᴴ/denom).
        // The previous code scaled only the tap step and always applied the FULL P update,
        // so every advisory (w = 0.1) row shrank P as if a full-confidence row had arrived
        // and long static/AWGN spans progressively froze adaptation (issue #64). A
        // consequence of the correct form: uniform weights cancel — all-0.1 rows adapt
        // exactly like all-1.0 rows once the P0 prior washes out, as scale invariance of
        // least squares demands. w = 1 reduces bit-identically to textbook RLS.
        var uPx = Cf.Zero;
        for (int j = 0; j < n; j++)
        {
            uPx += u[j] * _pxScratch[j]; // uᵀPu*: real-positive for Hermitian P
        }

        float denom = _lambda + (weight * uPx.Re);
        if (denom <= 1e-12f)
        {
            return y;
        }

        float invDenom = 1f / denom;

        Cf error = desired - y;
        Cf scaledError = error * (weight * invDenom);
        for (int i = 0; i < _ff.Length; i++)
        {
            _ff[i] += _pxScratch[i] * scaledError;
        }

        for (int j = 0; j < _fb.Length; j++)
        {
            _fb[j] += _pxScratch[_ff.Length + j] * scaledError;
        }

        float invLambda = 1f / _lambda;
        float kScale = weight * invDenom;
        for (int i = 0; i < n; i++)
        {
            Cf ki = _pxScratch[i] * kScale;
            for (int j = 0; j < n; j++)
            {
                _p[i, j] = (_p[i, j] - (ki * _pxScratch[j].Conj())) * invLambda;
            }
        }

        return y;
    }

    /// <summary>Enforces Hermitian symmetry on P and caps the diagonal to prevent
    /// null-space divergence (the probe is rank-deficient — ≤16 distinct patterns for
    /// 36+ taps — so P grows without bound in the unobserved subspace over long bursts).
    /// Call once per frame.</summary>
    public void SymmetrizeP(float pMax = 10f)
    {
        if (_p is null)
        {
            return;
        }

        int n = _ff.Length + _fb.Length;
        for (int i = 0; i < n; i++)
        {
            float diag = Math.Min(_p[i, i].Re, pMax);
            _p[i, i] = new Cf(diag, 0);
            for (int j = i + 1; j < n; j++)
            {
                Cf avg = (_p[i, j] + _p[j, i].Conj()) * 0.5f;
                _p[i, j] = avg;
                _p[j, i] = avg.Conj();
            }
        }
    }
}
