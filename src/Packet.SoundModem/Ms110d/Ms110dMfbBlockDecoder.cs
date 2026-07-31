using System.Numerics;
using M0LTE.Fec;
using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d.Fec;

namespace Packet.SoundModem.Ms110d;

/// <summary>
/// The MFB-form block decoder for QAM16 (wn8-program W5b2, evidence
/// 2026-07-31-wn8-w5a/-w5b1/-w5b2): the measured successor to the §B3.4 turbo
/// exclusion. Per burst, a ridged wide LS over the first block's probes accumulates a
/// per-tap energy profile (phases decorrelate across fading, delays do not) and places
/// a 26-tap detection window — acquisition centres the chip clock on whichever path
/// wins the preamble (the §B3.5b phenomenon, measured at WN8 on the disjoint
/// specimen), so the window cannot be fixed. Per block: label-free per-probe
/// composite-FIR anchors on ISI-clean probe interiors, per-tap linear interpolation,
/// per-symbol matched projection priced by the believed Gram, and the measured
/// schedule — SISO-soft cancellation rungs (wrong decisions self-attenuate, the §B3.3
/// lesson in this architecture), one convergence-gated decision-directed anchor
/// re-fit (mid-frame anchors from the loop's own decisions; the gate is the W5b1
/// label-quality condition — an ungated re-fit on a still-poor block poisons it), then
/// hard-cancellation rungs to an EXACT fixed point. No fixed point by the cap reverts
/// to the first-pass decode (the §B3.3 revert principle). Constants are structural or
/// cap-class (support arithmetic, percent-scale gates, wall-clock caps), documented in
/// place; the W5c off-rig direction check measures them off the D.6.1 geometry.
/// </summary>
internal sealed class Ms110dMfbBlockDecoder
{
    // Wide delay-profile scan bounds and the placed-window width, T/2 half-chips:
    // ±(2.3 ms spread + raised-cosine tails) at 4800 Hz — support arithmetic, not
    // tuning. 38 ISI-clean rows per probe against 26 unknowns.
    private const int ScanMin = -18;
    private const int ScanMax = 19;
    private const int ScanLen = ScanMax - ScanMin;
    private const int WinLen = 26;

    // Schedule caps (cap-class: they trade wall-clock, never correctness — the exact
    // fixed point / cycle-accept / revert triple does the real termination) and the
    // re-fit gate: the W5b2 corpse measured refit-good handovers at ≤2% decode churn
    // and the W5b1 poison case at ~30% — 5% sits in the measured gap with margin on
    // both sides.
    private const int SoftCap = 30;
    // 72: the W5b2 corpse measured the three deep-fade-lottery blocks (canonical
    // b3/b6, disjoint b4) needing ~45-60 rungs while 20/22 blocks converge by ~15-35;
    // at 48 exactly those three cap-reverted. The §B3.4 Amendment 3 asymmetry argument
    // applies verbatim: a cap revert throws away a repaired block, extra rungs only
    // cost wall-clock on the rare stragglers.
    private const int TotalCap = 72;
    private const double RefitChurnGate = 0.05;

    private readonly Ms110dMode _mode;
    private readonly Ms110dInterleaverParams _il;
    private readonly ConvolutionalCode _code;
    private readonly PunctureSpec _puncture;
    private readonly Ms110dInterleaver _interleaver;
    private readonly TailBitingViterbiDecoder _viterbi;
    private readonly TailBitingSisoDecoder _siso;

    private readonly int[] _nibs;
    private readonly Complex[] _ring;
    private readonly Complex[] _decisions;
    private readonly Complex[] _proj;
    private readonly double[] _gramT;
    private readonly long[] _dataChip;
    private readonly float[] _llrs;
    private readonly byte[] _dec;
    private readonly byte[] _prevDec;
    private readonly byte[] _prevDec2;
    private readonly float[] _softPunctured;
    private readonly float[] _softMother;
    private readonly float[] _softMotherPost;
    private readonly float[] _softWire;
    private readonly List<double> _anchorChip = new();
    private readonly List<Complex[]> _anchorH = new();
    private readonly List<double> _probeAnchorChip = new();
    private readonly List<Complex[]> _probeAnchorH = new();
    private readonly Complex[,] _gram;
    private readonly Complex[] _rhs;
    private readonly Complex[] _phiRow;
    private readonly double[] _metric = new double[16];
    private readonly double[] _p0 = new double[4];

    private bool _windowSet;
    private int _lMin;

    public Ms110dMfbBlockDecoder(
        Ms110dMode mode, Ms110dInterleaverParams il, ConvolutionalCode code,
        PunctureSpec puncture, Ms110dInterleaver interleaver,
        TailBitingViterbiDecoder viterbi, TailBitingSisoDecoder siso)
    {
        _mode = mode;
        _il = il;
        _code = code;
        _puncture = puncture;
        _interleaver = interleaver;
        _viterbi = viterbi;
        _siso = siso;

        int u = mode.U;
        int k = mode.K;
        int spanHc = (2 * (((il.Frames - 1) * (u + k)) + u + (2 * k))) + (2 * (ScanMax - ScanMin));
        _ring = new Complex[spanHc];
        _decisions = new Complex[il.Frames * u];
        _proj = new Complex[il.Frames * u];
        _gramT = new double[il.Frames * u];
        _dataChip = new long[il.Frames * u];
        _llrs = new float[il.SizeBits];
        _dec = new byte[il.InputBits];
        _prevDec = new byte[il.InputBits];
        _prevDec2 = new byte[il.InputBits];
        _softPunctured = new float[il.SizeBits];
        _softMother = new float[2 * il.InputBits];
        _softMotherPost = new float[2 * il.InputBits];
        _softWire = new float[il.SizeBits];
        _gram = new Complex[WinLen, WinLen];
        _rhs = new Complex[WinLen];
        _phiRow = new Complex[WinLen];

        // The per-frame scramble nibbles: the register resets at every data frame
        // start (D.5.1.3), so the sequence is identical each frame.
        _nibs = new int[u];
        var scrambler = new Ms110dScrambler();
        scrambler.Reset();
        for (int i = 0; i < u; i++)
        {
            _nibs[i] = scrambler.NextQam(0, 4);
        }
    }

    /// <summary>Clears the per-burst window state (a new burst may lock on the other
    /// path — the window must be re-scanned).</summary>
    public void ResetBurst()
    {
        _windowSet = false;
    }

    /// <summary>Decodes one QAM16 block in place. Returns true when the schedule
    /// reached an exact fixed point (the decode is <paramref name="info"/>); false
    /// means no fixed point by the cap and the caller must keep its first-pass decode
    /// (the revert principle).</summary>
    public bool DecodeBlock(Ms110dDemodulator demod, IReadOnlyList<long> frameChips, byte[] info)
    {
        int u = _mode.U;
        int k = _mode.K;
        int frames = _il.Frames;
        Action<string>? diag = demod.FrameDiagnosticsForInstruments;

        // Pull the block's ring span through the shipped CFO/timing-corrected read.
        long hc0 = (2 * (frameChips[0] - k)) + (2 * ScanMin);
        long hcEnd = (2 * (frameChips[frames - 1] + u + k)) + (2 * ScanMax);
        int n = (int)(hcEnd - hc0);
        for (long hc = hc0; hc < hcEnd; hc++)
        {
            Cf v = demod.RingReadT2(hc);
            _ring[hc - hc0] = new Complex(v.Re, v.Im);
        }

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < u; i++)
            {
                _dataChip[(f * u) + i] = frameChips[f] + i;
            }
        }

        if (!_windowSet)
        {
            ScanWindow(frameChips, hc0);
            _windowSet = true;
        }

        int lMin = _lMin;
        int lMax = lMin + WinLen;

        // Probe anchors: the preceding probe of every frame plus the following probe
        // of the last frame, each an LS over its ISI-clean interior.
        _probeAnchorChip.Clear();
        _probeAnchorH.Clear();
        for (int p = 0; p <= frames; p++)
        {
            bool preceding = p < frames;
            long ps = preceding ? frameChips[p] - k : frameChips[frames - 1] + u;
            bool boundary = preceding ? (p + 1) % frames == 0 : (frames + 1) % frames == 0;
            Cf[] probe = MiniProbe.Get(k, boundary);
            ClearNormal(WinLen);
            long rowLo = (2 * ps) + lMax;
            long rowHi = (2 * (ps + k)) + lMin;
            for (long hc = rowLo; hc < rowHi; hc++)
            {
                for (int i = 0; i < WinLen; i++)
                {
                    long src = hc - (lMin + i);
                    if ((src & 1) != 0)
                    {
                        _phiRow[i] = Complex.Zero;
                        continue;
                    }

                    Cf x = probe[(src / 2) - ps];
                    _phiRow[i] = new Complex(x.Re, x.Im);
                }

                AccumulateRow(_ring[hc - hc0], WinLen);
            }

            if (SolveRidged(WinLen, 1e-3))
            {
                _probeAnchorChip.Add(ps + (k / 2.0));
                _probeAnchorH.Add((Complex[])_rhs.Clone());
            }
        }

        RebuildAnchors(midFrame: false, frameChips);

        // The measured schedule.
        bool haveDecisions = false;
        bool converged = false;
        bool cycleAccepted = false;
        bool refitDone = false;
        bool refitApplied = false;
        int handoverChurn = -1;
        int rung = 0;
        int lastChurn = int.MaxValue;
        for (; rung <= TotalCap; rung++)
        {
            bool softPhase = rung < SoftCap && !refitDone;
            if (!softPhase && !refitDone)
            {
                refitDone = true; // handover — re-fit first if the gate admits it
                handoverChurn = lastChurn;
                if (haveDecisions && lastChurn <= (int)(RefitChurnGate * _il.InputBits))
                {
                    RefitAnchors(frameChips, hc0);
                    RebuildAnchors(midFrame: true, frameChips);
                    refitApplied = true;
                }
            }

            Project(frameChips, hc0, n, lMin, haveDecisions);
            BuildLlrs(lMin);
            Array.Copy(_prevDec, _prevDec2, _prevDec.Length);
            Array.Copy(_dec, _prevDec, _dec.Length);
            Ms110dFraming.DecodeBlock(_viterbi, _puncture, _interleaver, _llrs, _dec);
            int churn = 0;
            if (rung > 0)
            {
                for (int i = 0; i < _dec.Length; i++)
                {
                    churn += _dec[i] != _prevDec[i] ? 1 : 0;
                }
            }
            else
            {
                churn = _dec.Length;
            }

            lastChurn = churn;
            diag?.Invoke(FormattableString.Invariant(
                $"mfb-rung r{rung} churn={churn} soft={(softPhase ? 1 : 0)} sigma={_sigma2:E2}"));
            if (rung > 0 && churn == 0)
            {
                if (softPhase)
                {
                    // A stable soft decode is a handover signal, not the final fixed
                    // point — force the re-fit + hard tail to confirm it exactly.
                    refitDone = true;
                    handoverChurn = lastChurn;
                    if (lastChurn <= (int)(RefitChurnGate * _il.InputBits))
                    {
                        RefitAnchors(frameChips, hc0);
                        RebuildAnchors(midFrame: true, frameChips);
                        refitApplied = true;
                    }

                    SetDecisionsHard();
                    haveDecisions = true;
                    continue;
                }

                converged = true;
                break;
            }

            // An exact period-2 limit cycle in the hard tail: two near-identical
            // decodes swapping forever (measured on the deep-fade-lottery blocks —
            // b3 canonical cycles at churn 53 with σ² alternating by 0.4%). Accept the
            // cycle member whose reconstruction explains the ring better — a
            // label-free likelihood selection. The §B3.4 confident-wrong attractor
            // cannot satisfy decode == decode-two-rungs-ago exactly, so the revert
            // protection stands for genuine wander.
            if (!softPhase && rung > 1 && churn > 0 &&
                _dec.AsSpan().SequenceEqual(_prevDec2))
            {
                double sigmaB = _sigma2; // priced _prevDec's reconstruction this rung
                SetDecisionsHard();      // from _dec — price the other cycle member
                double sigmaA = ReconResidual(frameChips, hc0, n, lMin);
                if (sigmaB < sigmaA)
                {
                    Array.Copy(_prevDec, _dec, _dec.Length);
                }

                converged = true;
                cycleAccepted = true;
                break;
            }

            if (softPhase)
            {
                SetDecisionsSoft();
            }
            else
            {
                SetDecisionsHard();
            }

            haveDecisions = true;
        }

        diag?.Invoke(
            FormattableString.Invariant(
                $"mfb-block rungs={rung} window=[{lMin},{lMin + WinLen}) conv={(cycleAccepted ? 2 : converged ? 1 : 0)}") +
            FormattableString.Invariant(
                $" handoverChurn={handoverChurn} refit={(refitApplied ? 1 : 0)} finalChurn={lastChurn}"));
        if (converged)
        {
            Array.Copy(_dec, info, info.Length);
        }

        return converged;
    }

    // ------------------------------------------------------------------ estimation

    private void ScanWindow(IReadOnlyList<long> frameChips, long hc0)
    {
        // Ridged wide LS per probe over the first 8 probes, tap energies accumulated.
        int k = _mode.K;
        Span<double> profile = stackalloc double[ScanLen];
        profile.Clear();
        var gram = new Complex[ScanLen, ScanLen];
        var rhs = new Complex[ScanLen];
        var phi = new Complex[ScanLen];
        for (int p = 0; p < 8; p++)
        {
            long ps = frameChips[p] - k;
            bool boundary = (p + 1) % _il.Frames == 0;
            Cf[] probe = MiniProbe.Get(k, boundary);
            Array.Clear(rhs);
            for (int i = 0; i < ScanLen; i++)
            {
                for (int j = 0; j < ScanLen; j++)
                {
                    gram[i, j] = Complex.Zero;
                }
            }

            for (long hc = (2 * ps) + ScanMax; hc < (2 * (ps + k)) + ScanMin; hc++)
            {
                for (int i = 0; i < ScanLen; i++)
                {
                    long src = hc - (ScanMin + i);
                    if ((src & 1) != 0)
                    {
                        phi[i] = Complex.Zero;
                        continue;
                    }

                    Cf x = probe[(src / 2) - ps];
                    phi[i] = new Complex(x.Re, x.Im);
                }

                Complex row = _ring[hc - hc0];
                for (int i = 0; i < ScanLen; i++)
                {
                    Complex pi = Complex.Conjugate(phi[i]);
                    rhs[i] += pi * row;
                    for (int j = 0; j < ScanLen; j++)
                    {
                        gram[i, j] += pi * phi[j];
                    }
                }
            }

            double trace = 0;
            for (int i = 0; i < ScanLen; i++)
            {
                trace += gram[i, i].Real;
            }

            double ridge = Math.Max(1e-9, 3e-2 * trace / ScanLen);
            for (int i = 0; i < ScanLen; i++)
            {
                gram[i, i] += ridge;
            }

            if (!SolveHermitian(gram, rhs, ScanLen))
            {
                continue;
            }

            for (int i = 0; i < ScanLen; i++)
            {
                profile[i] += (rhs[i].Real * rhs[i].Real) + (rhs[i].Imaginary * rhs[i].Imaginary);
            }
        }

        double peak = 0;
        for (int i = 0; i < ScanLen; i++)
        {
            peak = Math.Max(peak, profile[i]);
        }

        int pkLo = 0;
        int pkHi = ScanLen - 1;
        for (int i = 0; i < ScanLen; i++)
        {
            if (profile[i] >= 0.1 * peak)
            {
                pkLo = i;
                break;
            }
        }

        for (int i = ScanLen - 1; i >= 0; i--)
        {
            if (profile[i] >= 0.1 * peak)
            {
                pkHi = i;
                break;
            }
        }

        _lMin = ScanMin + pkLo - 7;
        if (ScanMin + pkHi + 8 > _lMin + WinLen)
        {
            _lMin = (ScanMin + pkHi + 8) - WinLen;
        }
    }

    private void RefitAnchors(IReadOnlyList<long> frameChips, long hc0)
    {
        // Two mid-frame decision anchors per frame, rows drawn wholly from decided
        // data chips — the anchor grid triples and the interpolation gap through deep
        // fades drops from ~120 ms to ~40 ms.
        int u = _mode.U;
        int lMin = _lMin;
        int lMax = lMin + WinLen;
        _anchorChip.Clear();
        _anchorH.Clear();
        _anchorChip.AddRange(_probeAnchorChip);
        _anchorH.AddRange(_probeAnchorH);
        for (int f = 0; f < _il.Frames; f++)
        {
            for (int half = 0; half < 2; half++)
            {
                long start = frameChips[f] + 16 + (half * 128);
                long end = start + 96;
                ClearNormal(WinLen);
                for (long hc = (2 * start) + lMax; hc < (2 * end) + lMin; hc++)
                {
                    for (int i = 0; i < WinLen; i++)
                    {
                        long src = hc - (lMin + i);
                        if ((src & 1) != 0)
                        {
                            _phiRow[i] = Complex.Zero;
                            continue;
                        }

                        _phiRow[i] = _decisions[((long)f * u) + ((src / 2) - frameChips[f])];
                    }

                    AccumulateRow(_ring[hc - hc0], WinLen);
                }

                if (SolveRidged(WinLen, 1e-3))
                {
                    _anchorChip.Add((start + end) / 2.0);
                    _anchorH.Add((Complex[])_rhs.Clone());
                }
            }
        }

        SortAnchors();
    }

    private void RebuildAnchors(bool midFrame, IReadOnlyList<long> frameChips)
    {
        if (!midFrame)
        {
            _anchorChip.Clear();
            _anchorH.Clear();
            _anchorChip.AddRange(_probeAnchorChip);
            _anchorH.AddRange(_probeAnchorH);
        }

        SortAnchors();
    }

    private void SortAnchors()
    {
        // Insertion-sort the paired lists by anchor time (nearly sorted already).
        for (int i = 1; i < _anchorChip.Count; i++)
        {
            double key = _anchorChip[i];
            Complex[] keyH = _anchorH[i];
            int j = i - 1;
            while (j >= 0 && _anchorChip[j] > key)
            {
                _anchorChip[j + 1] = _anchorChip[j];
                _anchorH[j + 1] = _anchorH[j];
                j--;
            }

            _anchorChip[j + 1] = key;
            _anchorH[j + 1] = keyH;
        }
    }

    private void InterpolateH(double chip, Complex[] h)
    {
        int hi = 0;
        while (hi < _anchorChip.Count && _anchorChip[hi] < chip)
        {
            hi++;
        }

        if (hi >= _anchorChip.Count)
        {
            _anchorH[^1].CopyTo(h, 0);
            return;
        }

        if (hi == 0)
        {
            _anchorH[0].CopyTo(h, 0);
            return;
        }

        double frac = (chip - _anchorChip[hi - 1]) / (_anchorChip[hi] - _anchorChip[hi - 1]);
        Complex[] a = _anchorH[hi - 1];
        Complex[] b = _anchorH[hi];
        for (int i = 0; i < WinLen; i++)
        {
            h[i] = (a[i] * (1 - frac)) + (b[i] * frac);
        }
    }

    // ------------------------------------------------------------------ detection

    private readonly Complex[] _hAt = new Complex[WinLen];
    private Complex[]? _recon;
    private double _sigma2;

    /// <summary>Rebuilds the block reconstruction from the current
    /// <see cref="_decisions"/> (probes always known) and returns the mean squared
    /// residual per complex sample over the data span — the label-free noise price and
    /// the likelihood proxy the cycle-accept selection uses.</summary>
    private double ReconResidual(IReadOnlyList<long> frameChips, long hc0, int n, int lMin)
    {
        int u = _mode.U;
        int k = _mode.K;
        int frames = _il.Frames;
        _recon ??= new Complex[_ring.Length];
        Array.Clear(_recon, 0, n);
        void AddChip(long c, Complex x)
        {
            InterpolateH(c, _hAt);
            for (int i = 0; i < WinLen; i++)
            {
                long hc = (2 * c) + lMin + i;
                if (hc >= hc0 && hc < hc0 + n)
                {
                    _recon[hc - hc0] += _hAt[i] * x;
                }
            }
        }

        for (int p = 0; p <= frames; p++)
        {
            bool preceding = p < frames;
            long ps = preceding ? frameChips[p] - k : frameChips[frames - 1] + u;
            bool boundary = preceding ? (p + 1) % frames == 0 : (frames + 1) % frames == 0;
            Cf[] probe = MiniProbe.Get(k, boundary);
            for (int c = 0; c < k; c++)
            {
                AddChip(ps + c, new Complex(probe[c].Re, probe[c].Im));
            }
        }

        for (int d = 0; d < _decisions.Length; d++)
        {
            AddChip(_dataChip[d], _decisions[d]);
        }

        double resid = 0;
        long rows = 0;
        for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[frames - 1] + u); hc++)
        {
            Complex diff = _ring[hc - hc0] - _recon[hc - hc0];
            resid += (diff.Real * diff.Real) + (diff.Imaginary * diff.Imaginary);
            rows++;
        }

        return Math.Max(resid / rows, 1e-12);
    }

    private void Project(IReadOnlyList<long> frameChips, long hc0, int n, int lMin, bool cancel)
    {
        int u = _mode.U;
        _recon ??= new Complex[_ring.Length];
        if (cancel)
        {
            _sigma2 = ReconResidual(frameChips, hc0, n, lMin);
        }

        var r0Dist = cancel ? null : new List<double>(_proj.Length);
        for (int d = 0; d < _proj.Length; d++)
        {
            long c = _dataChip[d];
            InterpolateH(c, _hAt);
            double g2 = 0;
            Complex acc = Complex.Zero;
            for (int i = 0; i < WinLen; i++)
            {
                long hc = (2 * c) + lMin + i;
                if (hc < hc0 || hc >= hc0 + n)
                {
                    continue;
                }

                Complex row = cancel
                    ? _ring[hc - hc0] - _recon[hc - hc0] + (_hAt[i] * _decisions[d])
                    : _ring[hc - hc0];
                acc += Complex.Conjugate(_hAt[i]) * row;
                g2 += (_hAt[i].Real * _hAt[i].Real) + (_hAt[i].Imaginary * _hAt[i].Imaginary);
            }

            _proj[d] = acc / Math.Max(g2, 1e-12);
            _gramT[d] = g2;
            if (r0Dist is not null)
            {
                double best = double.MaxValue;
                for (int q = 0; q < 16; q++)
                {
                    Cf cq = Ms110dTables.Qam16[q];
                    double dr = _proj[d].Real - cq.Re;
                    double di = _proj[d].Imaginary - cq.Im;
                    best = Math.Min(best, (dr * dr) + (di * di));
                }

                r0Dist.Add(best * g2);
            }
        }

        if (r0Dist is not null)
        {
            // The cold rung's crude global price: the median ISI-inclusive squared
            // distance to the nearest point. Every later rung prices with the measured
            // reconstruction residual.
            r0Dist.Sort();
            _sigma2 = Math.Max(r0Dist[r0Dist.Count / 2], 1e-9);
        }
    }

    private void BuildLlrs(int lMin)
    {
        int u = _mode.U;
        for (int d = 0; d < _proj.Length; d++)
        {
            int nib = _nibs[d % u];
            for (int q = 0; q < 16; q++)
            {
                Cf cq = Ms110dTables.Qam16[q ^ nib];
                double dr = _proj[d].Real - cq.Re;
                double di = _proj[d].Imaginary - cq.Im;
                _metric[q] = ((dr * dr) + (di * di)) * _gramT[d] / _sigma2;
            }

            for (int bb = 0; bb < 4; bb++)
            {
                double m0 = double.MaxValue, m1 = double.MaxValue;
                for (int q = 0; q < 16; q++)
                {
                    if (((q >> (3 - bb)) & 1) != 0)
                    {
                        m1 = Math.Min(m1, _metric[q]);
                    }
                    else
                    {
                        m0 = Math.Min(m0, _metric[q]);
                    }
                }

                _llrs[(d * 4) + bb] = (float)(m1 - m0); // positive ⇒ bit 0
            }
        }
    }

    private void SetDecisionsHard()
    {
        byte[] fetched = Ms110dFraming.EncodeBlock(_code, _puncture, _interleaver, _dec);
        int u = _mode.U;
        int bit = 0;
        for (int d = 0; d < _decisions.Length; d++)
        {
            int number = (fetched[bit] << 3) | (fetched[bit + 1] << 2)
                | (fetched[bit + 2] << 1) | fetched[bit + 3];
            bit += 4;
            Cf w = Ms110dTables.Qam16[number ^ _nibs[d % u]];
            _decisions[d] = new Complex(w.Re, w.Im);
        }
    }

    private void SetDecisionsSoft()
    {
        // SISO per-bit posteriors → per-symbol E[x] (independent-bit approximation,
        // nib-permuted). Wrong decisions self-attenuate toward zero.
        _interleaver.Deinterleave(_llrs, _softPunctured);
        Ms110dPuncture.Depuncture(_puncture, _softPunctured, _softMother);
        _siso.Decode(_softMother, _softMotherPost);
        Ms110dPuncture.Apply(_puncture, _softMotherPost, _softPunctured);
        _interleaver.Interleave(_softPunctured, _softWire);
        int u = _mode.U;
        for (int d = 0; d < _decisions.Length; d++)
        {
            for (int bb = 0; bb < 4; bb++)
            {
                double l = Math.Clamp(_softWire[(d * 4) + bb], -30f, 30f);
                _p0[bb] = 1.0 / (1.0 + Math.Exp(-l));
            }

            int nib = _nibs[d % u];
            Complex ex = Complex.Zero;
            for (int m = 0; m < 16; m++)
            {
                double pm = 1.0;
                for (int bb = 0; bb < 4; bb++)
                {
                    bool one = ((m >> (3 - bb)) & 1) != 0;
                    pm *= one ? 1.0 - _p0[bb] : _p0[bb];
                }

                Cf w = Ms110dTables.Qam16[m ^ nib];
                ex += pm * new Complex(w.Re, w.Im);
            }

            _decisions[d] = ex;
        }
    }

    // ------------------------------------------------------------------ linear algebra

    private void ClearNormal(int len)
    {
        Array.Clear(_rhs, 0, len);
        for (int i = 0; i < len; i++)
        {
            for (int j = 0; j < len; j++)
            {
                _gram[i, j] = Complex.Zero;
            }
        }
    }

    private void AccumulateRow(Complex row, int len)
    {
        for (int i = 0; i < len; i++)
        {
            Complex pi = Complex.Conjugate(_phiRow[i]);
            _rhs[i] += pi * row;
            for (int j = 0; j < len; j++)
            {
                _gram[i, j] += pi * _phiRow[j];
            }
        }
    }

    private bool SolveRidged(int len, double ridgeScale)
    {
        double trace = 0;
        for (int i = 0; i < len; i++)
        {
            trace += _gram[i, i].Real;
        }

        double ridge = Math.Max(1e-9, ridgeScale * trace / len);
        for (int i = 0; i < len; i++)
        {
            _gram[i, i] += ridge;
        }

        return SolveHermitian(_gram, _rhs, len);
    }

    private static bool SolveHermitian(Complex[,] a, Complex[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (a[r, col].Magnitude > a[pivot, col].Magnitude)
                {
                    pivot = r;
                }
            }

            if (a[pivot, col].Magnitude < 1e-12)
            {
                return false;
            }

            if (pivot != col)
            {
                for (int c = 0; c < n; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                Complex factor = a[r, col] / a[col, col];
                if (factor == Complex.Zero)
                {
                    continue;
                }

                for (int c = col; c < n; c++)
                {
                    a[r, c] -= factor * a[col, c];
                }

                b[r] -= factor * b[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            Complex acc = b[r];
            for (int c = r + 1; c < n; c++)
            {
                acc -= a[r, c] * b[c];
            }

            b[r] = acc / a[r, r];
        }

        return true;
    }
}
