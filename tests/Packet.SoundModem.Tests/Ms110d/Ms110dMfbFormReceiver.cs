using System.Globalization;
using System.Numerics;
using M0LTE.Fec;
using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ms110d.Fec;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// W5a: the MFB-form receiver prototyped in the demodulator's own ring domain
/// (wn8-program-plan §4, evidence 2026-07-31-wn8-w5a). Everything a real receiver has,
/// nothing it doesn't: the shipped acquisition supplies CFO/timing; per-probe LS
/// estimates the composite T/2 impulse response (TX pulse ⊗ channel ⊗ RX front-end, no
/// parametric path knowledge) on ISI-clean probe interiors; per-tap linear
/// interpolation carries it between probes; detection is per-symbol matched projection
/// with iterative reconstruction-and-cancellation seeded from its own decodes. Truth is
/// used ONLY to score, plus one labelled calibration lane (truth-reconstruction
/// residual) that validates templates and alignment without touching the honest rungs.
/// Not a gate — an instrument. <c>MS110D_MFBRX=1</c>, <c>MS110D_MFBRX_SEED</c>
/// (default 508), <c>MS110D_MFBRX_WORKER/BURST</c> (default 0), <c>MS110D_MFBRX_ITERS</c>
/// (default 3), <c>MS110D_MFBRX_OUT</c> (default ".").
/// </summary>
public class Ms110dMfbFormReceiver
{
    private const int LMin = -6;   // composite-response support, T/2 half-chips
    private const int LMax = 16;
    private const int L = LMax - LMin;

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
    }

    [Fact]
    public void Mfb_Form_Receiver_On_The_Ring()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MFBRX") != "1",
            "set MS110D_MFBRX=1 for the W5a MFB-form receiver prototype");

        int wn = EnvInt("MS110D_MFBRX_WN", 8);
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_MFBRX_SNR"),
            out double s) ? s : 23;
        int baseSeed = EnvInt("MS110D_MFBRX_SEED", 500 + wn);
        int worker = EnvInt("MS110D_MFBRX_WORKER", 0);
        int burst = EnvInt("MS110D_MFBRX_BURST", 0);
        int iters = EnvInt("MS110D_MFBRX_ITERS", 3);
        string outDir = Environment.GetEnvironmentVariable("MS110D_MFBRX_OUT") ?? ".";

        // Bit-exact corpse reconstruction (the banked construction).
        int workerSeed = baseSeed + (worker * 1_000_000);
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        double blockSeconds = il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;
        var random = new Random(workerSeed);
        var payload = new byte[payloadBitsPerBurst];
        for (int b = 0; b <= burst; b++)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)random.Next(2);
            }
        }

        float[] audio = tx.Modulate(payload);
        int channelSeed = workerSeed + (1000 * burst) + 1;
        var channel = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor);
        float[] rx = channel.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
        int txBlocks = txBits.Length / il.InputBits;
        ConvolutionalCode code = ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, tx.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        var viterbi = new TailBitingViterbiDecoder(code);
        int u256 = tx.Mode.U;
        int k32 = tx.Mode.K;
        int frames = il.Frames;

        var fetchedBlocks = new byte[txBlocks][];
        for (int b = 0; b < txBlocks; b++)
        {
            fetchedBlocks[b] = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
        }

        // The per-frame scramble nibbles (register reset each data frame — identical
        // sequence every frame).
        var nibs = new int[u256];
        var scrambler = new Ms110dScrambler();
        scrambler.Reset();
        for (int u = 0; u < u256; u++)
        {
            nibs[u] = scrambler.NextQam(0, 4);
        }

        // The demod runs first: its acquisition supplies CFO/timing, and the block-ready
        // seam lets us pull each block's ring span while it is resident.
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        var spans = new List<(int Block, long[] FrameChips, long Hc0, Cf[] Ring)>();
        demod.InstrumentBlockReady = (blockIndex, frameChips) =>
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            long hc0 = (2 * (frameChips[0] - k32)) + (2 * LMin);
            long hcEnd = (2 * (frameChips[^1] + u256 + k32)) + (2 * LMax);
            var ring = new Cf[hcEnd - hc0];
            for (long hc = hc0; hc < hcEnd; hc++)
            {
                ring[hc - hc0] = demod.InstrumentReadT2(hc);
            }

            spans.Add((blockIndex, frameChips, hc0, ring));
        };
        demod.Process(rx);
        spans.Count.Should().Be(txBlocks, "every block must acquire and be pulled");

        using var summary = new StreamWriter(Path.Combine(outDir,
            FormattableString.Invariant($"mfbrx-summary-wn{wn}-w{worker}-b{burst}-seed{baseSeed}.txt")));
        summary.WriteLine(FormattableString.Invariant(
            $"WN{wn} @ {snrDb} dB baseSeed {baseSeed} worker {worker} burst {burst} (channelSeed {channelSeed}) MFB-form receiver, L={L} [{LMin},{LMax})"));

        var rungTotals = new long[iters + 1];
        Span<double> metric = stackalloc double[16];
        var truthResidBlocks = new List<string>();
        var anchorResidBlocks = new List<string>();
        var rungLines = new List<string>[iters + 1];
        for (int rung = 0; rung <= iters; rung++)
        {
            rungLines[rung] = new List<string>();
        }

        foreach ((int b, long[] frameChips, long hc0, Cf[] ringCf) in spans)
        {
            int n = ringCf.Length;
            var ring = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                ring[i] = new Complex(ringCf[i].Re, ringCf[i].Im);
            }

            // --- Probe-anchor composite-FIR estimation (label-free) -----------------
            // Anchors: the preceding probe of every frame plus the following probe of
            // the last frame. Rows are the probe's ISI-clean interior (every chip whose
            // response reaches the row lies inside the probe).
            var anchorChip = new List<double>();
            var anchorH = new List<Complex[]>();
            double anchorResidSum = 0;
            long anchorResidRows = 0;
            var gram = new Complex[L, L];
            var rhs = new Complex[L];
            for (int p = 0; p <= frames; p++)
            {
                bool preceding = p < frames;
                long ps = preceding ? frameChips[p] - k32 : frameChips[^1] + u256;
                bool boundary = preceding
                    ? (p + 1) % frames == 0
                    : (frames + 1) % frames == 0;
                Cf[] probe = MiniProbe.Get(k32, boundary);

                Array.Clear(rhs);
                for (int i = 0; i < L; i++)
                {
                    for (int j = 0; j < L; j++)
                    {
                        gram[i, j] = Complex.Zero;
                    }
                }

                long rowLo = (2 * ps) + LMax;
                long rowHi = (2 * (ps + k32)) + LMin;
                for (long hc = rowLo; hc < rowHi; hc++)
                {
                    Complex row = ring[hc - hc0];
                    for (int i = 0; i < L; i++)
                    {
                        long src = hc - (LMin + i);
                        if ((src & 1) != 0)
                        {
                            continue; // chips sit on even half-positions only
                        }

                        long c = src / 2;
                        Cf x = probe[c - ps];
                        var phiI = new Complex(x.Re, x.Im);
                        rhs[i] += Complex.Conjugate(phiI) * row;
                        for (int j = i; j < L; j++)
                        {
                            long srcJ = hc - (LMin + j);
                            if ((srcJ & 1) != 0)
                            {
                                continue;
                            }

                            Cf xj = probe[(srcJ / 2) - ps];
                            Complex g = Complex.Conjugate(phiI) * new Complex(xj.Re, xj.Im);
                            gram[i, j] += g;
                            if (j != i)
                            {
                                gram[j, i] += Complex.Conjugate(g);
                            }
                        }
                    }
                }

                double trace = 0;
                for (int i = 0; i < L; i++)
                {
                    trace += gram[i, i].Real;
                }

                double ridge = Math.Max(1e-9, 1e-3 * trace / L);
                for (int i = 0; i < L; i++)
                {
                    gram[i, i] += ridge;
                }

                var h = (Complex[])rhs.Clone();
                if (!SolveHermitian(gram, h))
                {
                    continue;
                }

                // Anchor-fit residual on its own rows (the label-free noise estimate).
                for (long hc = rowLo; hc < rowHi; hc++)
                {
                    Complex model = Complex.Zero;
                    for (int i = 0; i < L; i++)
                    {
                        long src = hc - (LMin + i);
                        if ((src & 1) != 0)
                        {
                            continue;
                        }

                        Cf x = probe[(src / 2) - ps];
                        model += h[i] * new Complex(x.Re, x.Im);
                    }

                    Complex d = ring[hc - hc0] - model;
                    anchorResidSum += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                    anchorResidRows++;
                }

                anchorChip.Add(ps + (k32 / 2.0));
                anchorH.Add(h);
            }

            double sigmaAnchor = anchorResidSum / Math.Max(1, anchorResidRows);
            anchorResidBlocks.Add(FormattableString.Invariant($"b{b}:{sigmaAnchor:E2}"));

            // Per-chip interpolated response h(t) — per-tap linear between anchors.
            Complex[] HAt(double chip)
            {
                int hi = anchorChip.FindIndex(ax => ax >= chip);
                if (hi < 0)
                {
                    return anchorH[^1];
                }

                if (hi == 0)
                {
                    return anchorH[0];
                }

                double frac = (chip - anchorChip[hi - 1]) / (anchorChip[hi] - anchorChip[hi - 1]);
                var h = new Complex[L];
                for (int i = 0; i < L; i++)
                {
                    h[i] = (anchorH[hi - 1][i] * (1 - frac)) + (anchorH[hi][i] * frac);
                }

                return h;
            }

            // Wire truth for this block (scoring + the truth-recon calibration lane).
            byte[] fetched = fetchedBlocks[b];
            var truthWire = new Complex[frames * u256];
            var dataChip = new long[frames * u256];
            int bit = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int u = 0; u < u256; u++)
                {
                    int number = (fetched[bit] << 3) | (fetched[bit + 1] << 2)
                        | (fetched[bit + 2] << 1) | fetched[bit + 3];
                    bit += 4;
                    Cf w = Ms110dTables.Qam16[number ^ nibs[u]];
                    truthWire[(f * u256) + u] = new Complex(w.Re, w.Im);
                    dataChip[(f * u256) + u] = frameChips[f] + u;
                }
            }

            // Reconstruction of the whole span from a wire-symbol assignment: probes
            // always known, data from the given assignment (null entries contribute 0).
            Complex[] Reconstruct(Complex[]? dataWire)
            {
                var recon = new Complex[n];
                void AddChip(long c, Complex x)
                {
                    Complex[] h = HAt(c);
                    for (int i = 0; i < L; i++)
                    {
                        long hc = (2 * c) + LMin + i;
                        if (hc >= hc0 && hc < hc0 + n)
                        {
                            recon[hc - hc0] += h[i] * x;
                        }
                    }
                }

                for (int p = 0; p <= frames; p++)
                {
                    bool preceding = p < frames;
                    long ps = preceding ? frameChips[p] - k32 : frameChips[^1] + u256;
                    bool boundary = preceding ? (p + 1) % frames == 0 : (frames + 1) % frames == 0;
                    Cf[] probe = MiniProbe.Get(k32, boundary);
                    for (int c = 0; c < k32; c++)
                    {
                        AddChip(ps + c, new Complex(probe[c].Re, probe[c].Im));
                    }
                }

                if (dataWire is not null)
                {
                    for (int d = 0; d < dataWire.Length; d++)
                    {
                        AddChip(dataChip[d], dataWire[d]);
                    }
                }

                return recon;
            }

            // Calibration lane: residual of the TRUTH reconstruction over the data
            // region — validates templates, alignment, and the interpolated h(t) in one
            // label-exact number (compare against the anchor-fit noise floor).
            {
                Complex[] recon = Reconstruct(truthWire);
                double resid = 0;
                long rows = 0;
                for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[^1] + u256); hc++)
                {
                    Complex d = ring[hc - hc0] - recon[hc - hc0];
                    resid += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                    rows++;
                }

                truthResidBlocks.Add(FormattableString.Invariant($"b{b}:{resid / rows:E2}"));
            }

            // --- The detection rung ladder -----------------------------------------
            var llrs = new float[il.SizeBits];
            var dec = new byte[il.InputBits];
            Complex[]? decisions = null; // wire-symbol assignment from the last decode

            for (int rung = 0; rung <= iters; rung++)
            {
                Complex[]? recon = decisions is null ? null : Reconstruct(decisions);
                double sigma2;
                if (recon is null)
                {
                    sigma2 = 0; // set per-symbol below for R0
                }
                else
                {
                    double resid = 0;
                    long rows = 0;
                    for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[^1] + u256); hc++)
                    {
                        Complex d = ring[hc - hc0] - recon[hc - hc0];
                        resid += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                        rows++;
                    }

                    sigma2 = resid / rows;
                }

                // R0's crude global price: the median squared distance of the raw
                // projections to their nearest constellation point (ISI-inclusive).
                var proj = new Complex[frames * u256];
                var gramT = new double[frames * u256];
                var r0Dist = recon is null ? new List<double>(frames * u256) : null;
                for (int d = 0; d < proj.Length; d++)
                {
                    long c = dataChip[d];
                    Complex[] h = HAt(c);
                    double g2 = 0;
                    Complex acc = Complex.Zero;
                    for (int i = 0; i < L; i++)
                    {
                        long hc = (2 * c) + LMin + i;
                        if (hc < hc0 || hc >= hc0 + n)
                        {
                            continue;
                        }

                        Complex row = recon is null
                            ? ring[hc - hc0]
                            : ring[hc - hc0] - recon[hc - hc0] + (h[i] * decisions![d]);
                        acc += Complex.Conjugate(h[i]) * row;
                        g2 += (h[i].Real * h[i].Real) + (h[i].Imaginary * h[i].Imaginary);
                    }

                    proj[d] = acc / Math.Max(g2, 1e-12);
                    gramT[d] = g2;
                    if (r0Dist is not null)
                    {
                        double bestD = double.MaxValue;
                        for (int q = 0; q < 16; q++)
                        {
                            Cf cq = Ms110dTables.Qam16[q];
                            double dr = proj[d].Real - cq.Re;
                            double di = proj[d].Imaginary - cq.Im;
                            bestD = Math.Min(bestD, (dr * dr) + (di * di));
                        }

                        r0Dist.Add(bestD * g2);
                    }
                }

                if (r0Dist is not null)
                {
                    r0Dist.Sort();
                    sigma2 = Math.Max(r0Dist[r0Dist.Count / 2], 1e-9);
                }

                // LLRs and decode.
                for (int d = 0; d < proj.Length; d++)
                {
                    int u = d % u256;
                    for (int q = 0; q < 16; q++)
                    {
                        Cf cq = Ms110dTables.Qam16[q ^ nibs[u]];
                        double dr = proj[d].Real - cq.Re;
                        double di = proj[d].Imaginary - cq.Im;
                        metric[q] = ((dr * dr) + (di * di)) * gramT[d] / sigma2;
                    }

                    for (int bb = 0; bb < 4; bb++)
                    {
                        double m0 = double.MaxValue, m1 = double.MaxValue;
                        for (int q = 0; q < 16; q++)
                        {
                            if (((q >> (3 - bb)) & 1) != 0)
                            {
                                m1 = Math.Min(m1, metric[q]);
                            }
                            else
                            {
                                m0 = Math.Min(m0, metric[q]);
                            }
                        }

                        llrs[(d * 4) + bb] = (float)(m1 - m0);
                    }
                }

                Ms110dFraming.DecodeBlock(viterbi, puncture, interleaver, llrs, dec);
                int errs = 0;
                for (int i = 0; i < dec.Length; i++)
                {
                    errs += dec[i] != txBits[(b * il.InputBits) + i] ? 1 : 0;
                }

                rungTotals[rung] += errs;
                rungLines[rung].Add($"b{b}:{errs}");

                // Re-encode the decode as the next rung's cancellation assignment.
                byte[] reFetched = Ms110dFraming.EncodeBlock(code, puncture, interleaver, dec);
                decisions = new Complex[frames * u256];
                int rbit = 0;
                for (int f = 0; f < frames; f++)
                {
                    for (int u = 0; u < u256; u++)
                    {
                        int number = (reFetched[rbit] << 3) | (reFetched[rbit + 1] << 2)
                            | (reFetched[rbit + 2] << 1) | reFetched[rbit + 3];
                        rbit += 4;
                        Cf w = Ms110dTables.Qam16[number ^ nibs[u]];
                        decisions[(f * u256) + u] = new Complex(w.Re, w.Im);
                    }
                }
            }
        }

        summary.WriteLine($"anchor-fit residual per block: {string.Join(" ", anchorResidBlocks)}");
        summary.WriteLine($"truth-recon residual per block: {string.Join(" ", truthResidBlocks)}");
        for (int rung = 0; rung <= iters; rung++)
        {
            summary.WriteLine(FormattableString.Invariant(
                $"R{rung} coded errors {rungTotals[rung]}/{(long)txBlocks * il.InputBits}: {string.Join(" ", rungLines[rung])}"));
        }
    }

    private static bool SolveHermitian(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
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
