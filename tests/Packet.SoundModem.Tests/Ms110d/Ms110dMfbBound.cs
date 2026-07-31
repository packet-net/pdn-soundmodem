using System.Globalization;
using M0LTE.Dsp;
using M0LTE.Fec;
using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ms110d.Fec;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// W1b matched-filter bound (wn8-program-plan §4, evidence 2026-07-31-wn8-w1b): coded
/// BER of matched-filter detection on the EXACT recorded channel with every other
/// symbol's interference cancelled exactly — an upper bound on any receiver-only
/// architecture at the operating point. No demodulator involvement at all: the corpse
/// is rebuilt bit-exactly (the autopsy rig's construction), the noiseless faded signal
/// comes from a same-seed <see cref="WattersonChannel"/> at SNR=∞ (gains are drawn
/// before noise — the B0 genie precedent), and the per-symbol statistic is bias-free by
/// construction: ŷ_u = x_u + the noise projected onto the symbol's exact-channel
/// templates, whitened by the per-symbol 2×2 Gram (exact elliptical pricing — fades
/// price as honest erasures). The envelope LPF inside the channel is not modelled, so
/// the projection is very slightly mismatched — a PESSIMISTIC bound, the safe
/// direction for both registered reads. Not a gate — an instrument.
/// <c>MS110D_MFB=1</c>, <c>MS110D_MFB_WN</c> (default 8), <c>MS110D_MFB_SEED</c>
/// (default 500+WN), <c>MS110D_MFB_WORKER</c>/<c>MS110D_MFB_BURST</c> (default 0),
/// <c>MS110D_MFB_OUT</c> (default ".").
/// </summary>
public class Ms110dMfbBound
{
    private static readonly Dictionary<int, double> PoorSnr = new()
    {
        { 2, 5 }, { 5, 11 }, { 6, 14 }, { 7, 19 }, { 8, 23 }, { 13, 11 },
    };

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
    }

    [Fact]
    public void Matched_Filter_Bound_On_The_Exact_Channel()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MFB") != "1",
            "set MS110D_MFB=1 for the W1b matched-filter bound");

        int wn = EnvInt("MS110D_MFB_WN", 8);
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_MFB_SNR"),
            out double s) ? s : PoorSnr[wn];
        int baseSeed = EnvInt("MS110D_MFB_SEED", 500 + wn);
        int worker = EnvInt("MS110D_MFB_WORKER", 0);
        int burst = EnvInt("MS110D_MFB_BURST", 0);
        string outDir = Environment.GetEnvironmentVariable("MS110D_MFB_OUT") ?? ".";

        // Bit-exact corpse reconstruction (the Ms110dTailAutopsy construction).
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
        Cf[] wireSymbols = tx.BuildSymbols(payload);
        int channelSeed = workerSeed + (1000 * burst) + 1;
        var noisy = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor) { RecordGains = true };
        float[] rx = noisy.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        // The exact noiseless faded signal: a same-seed channel at SNR=∞ draws the
        // identical gain realization (gains before noise) and skips the noise.
        var exact = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor) { RecordGains = true };
        float[] noiseless = exact.Apply(audio, double.PositiveInfinity);
        IReadOnlyList<Cf[]> gains = exact.LastPathGains!;
        IReadOnlyList<Cf[]> gainsNoisy = noisy.LastPathGains!;
        for (int k = 0; k < gains.Count; k++)
        {
            (gains[k][0] - gainsNoisy[k][0]).Cnorm().Should().Be(0f,
                "the same-seed ∞-SNR channel must reproduce the identical gain draw");
        }

        // r = rx − R over the channel span is exactly the additive noise.
        int n = audio.Length;
        var r = new float[n];
        double noisePower = 0;
        for (int i = 0; i < n; i++)
        {
            r[i] = rx[2400 + i] - noiseless[i];
            noisePower += (double)r[i] * r[i];
        }

        double sigma2 = noisePower / n; // per real sample

        // TX truth per block (the autopsy's own construction): fetched wire bits and
        // the scramble nibbles that map symbol numbers to wire constellation points.
        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
        int txBlocks = txBits.Length / il.InputBits;
        ConvolutionalCode code = ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, tx.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        var viterbi = new TailBitingViterbiDecoder(code);
        int u256 = tx.Mode.U;
        int k32 = tx.Mode.K;

        // Global symbol index of data symbol (b, f, u): preamble, the preamble-ending
        // probe, then [U data + K probe] frames, then the 32-chip EOT probe extension
        // (settings default — Ms110dModulator.BuildSymbols/AppendProbe). Verified
        // exactly below against the wire symbol stream.
        int preambleLen = wireSymbols.Length - (txBlocks * il.Frames * (u256 + k32)) - k32 - 32;
        preambleLen.Should().BeGreaterThan(0);
        int DataIndex(int b, int f, int u) =>
            preambleLen + k32 + (((b * il.Frames) + f) * (u256 + k32)) + u;

        var scrambleNibbles = new int[il.Frames * u256];
        var scrambler = new Ms110dScrambler();
        for (int f = 0; f < il.Frames; f++)
        {
            scrambler.Reset();
            for (int u = 0; u < u256; u++)
            {
                scrambleNibbles[(f * u256) + u] = scrambler.NextQam(0, 4);
            }
        }

        // The channel-static pulse templates: the modulator's SRRC (unit energy, 65
        // taps, symbol n's pulse starting at audio sample 4n — Ms110dModulator.Shape)
        // times the TX amplitude, fractionally delayed per path with the channel's own
        // windowed-sinc kernel. Real-valued; complex time variation rides on g_k.
        float[] basePulse = DesignPulse(settings.Amplitude);
        var pulses = new (float[] Taps, int First)[2];
        double[] delays = [0.0, 2.0 * 9600 / 1000.0];
        for (int k = 0; k < 2; k++)
        {
            pulses[k] = DelayPulse(basePulse, delays[k]);
        }

        float pathScale = (float)(1.0 / Math.Sqrt(2.0));

        // Calibration lane (i): rebuild the noiseless faded signal from the wire
        // symbols + templates and measure the model residual. Validates layout, pulse
        // model, carrier convention, and gain alignment in one label-exact shot.
        var rebuilt = new float[n];
        for (int sym = 0; sym < wireSymbols.Length; sym++)
        {
            AddSymbol(rebuilt, wireSymbols[sym], sym, pulses, gains, pathScale);
        }

        double refPower = 0, residPower = 0;
        for (int i = 0; i < n; i++)
        {
            refPower += (double)noiseless[i] * noiseless[i];
            residPower += ((double)rebuilt[i] - noiseless[i]) * (rebuilt[i] - noiseless[i]);
        }

        double modelResidual = Math.Sqrt(residPower / refPower);

        // The MFB per data symbol: whiten the projected noise by the 2×2 template Gram,
        // add the true symbol back (genie-exact interference cancellation), price each
        // candidate by the per-symbol Mahalanobis metric, decode through the outer code.
        var blockErrs = new List<string>();
        long codedErrors = 0;
        long sliceErrors = 0, sliceSymbols = 0;
        double predSum = 0, measSum = 0;
        var llrs = new float[il.SizeBits];
        var dec = new byte[il.InputBits];
        Span<float> metric = stackalloc float[16];
        using var summary = new StreamWriter(Path.Combine(outDir,
            FormattableString.Invariant($"mfb-summary-wn{wn}-w{worker}-b{burst}-seed{baseSeed}.txt")));
        for (int b = 0; b < txBlocks; b++)
        {
            byte[] fetched = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
            int bit = 0;
            for (int f = 0; f < il.Frames; f++)
            {
                for (int u = 0; u < u256; u++)
                {
                    int number = (fetched[bit] << 3) | (fetched[bit + 1] << 2)
                        | (fetched[bit + 2] << 1) | fetched[bit + 3];
                    int nib = scrambleNibbles[(f * u256) + u];
                    int sym = DataIndex(b, f, u);
                    Cf wire = Ms110dTables.Qam16[number ^ nib];
                    (wireSymbols[sym] - wire).Cnorm().Should().Be(0f,
                        "the data-index layout must match the modulator's wire stream");

                    (Cf y, float m11, float m12, float m22) =
                        Project(r, wire, sym, pulses, gains, pathScale);

                    // Predicted vs measured statistic-noise check (lane iii).
                    float det = (m11 * m22) - (m12 * m12);
                    predSum += sigma2 * (m11 + m22) / Math.Max(det, 1e-12f);
                    measSum += (y - wire).Cnorm();

                    int best = 0;
                    float bestMetric = float.MaxValue;
                    for (int cand = 0; cand < 16; cand++)
                    {
                        Cf d = y - Ms110dTables.Qam16[cand ^ nib];
                        // Mahalanobis under noise covariance σ²·M⁻¹: dᵀ·M·d / 2σ².
                        float q = (m11 * d.Re * d.Re) + (2f * m12 * d.Re * d.Im)
                            + (m22 * d.Im * d.Im);
                        metric[cand] = q / (float)(2.0 * sigma2);
                        if (metric[cand] < bestMetric)
                        {
                            bestMetric = metric[cand];
                            best = cand;
                        }
                    }

                    sliceSymbols++;
                    sliceErrors += best == number ? 0 : 1;
                    for (int bb = 0; bb < 4; bb++)
                    {
                        float m0 = float.MaxValue, m1 = float.MaxValue;
                        for (int cand = 0; cand < 16; cand++)
                        {
                            bool one = ((cand >> (3 - bb)) & 1) != 0;
                            if (one)
                            {
                                m1 = Math.Min(m1, metric[cand]);
                            }
                            else
                            {
                                m0 = Math.Min(m0, metric[cand]);
                            }
                        }

                        llrs[bit + bb] = m1 - m0; // positive ⇒ bit 0, the house convention
                    }

                    bit += 4;
                }
            }

            Ms110dFraming.DecodeBlock(viterbi, puncture, interleaver, llrs, dec);
            int errs = 0;
            for (int i = 0; i < dec.Length; i++)
            {
                errs += dec[i] != txBits[(b * il.InputBits) + i] ? 1 : 0;
            }

            codedErrors += errs;
            blockErrs.Add($"b{b}:{errs}");
        }

        long totalInfo = (long)txBlocks * il.InputBits;
        summary.WriteLine(FormattableString.Invariant(
            $"WN{wn} @ {snrDb} dB baseSeed {baseSeed} worker {worker} burst {burst} (channelSeed {channelSeed}) MFB"));
        summary.WriteLine(FormattableString.Invariant(
            $"model residual RMS(rebuilt-exact)/RMS(exact) = {modelResidual:E3} (envelope-LPF unmodelled; pessimistic)"));
        summary.WriteLine(FormattableString.Invariant(
            $"noise sigma2/sample {sigma2:E4}; statistic noise predicted {predSum / sliceSymbols:E4} vs measured {measSum / sliceSymbols:E4}"));
        summary.WriteLine(FormattableString.Invariant(
            $"MF slicer SER {(double)sliceErrors / sliceSymbols:E3} ({sliceErrors}/{sliceSymbols})"));
        summary.WriteLine(FormattableString.Invariant(
            $"MFB coded errors {codedErrors}/{totalInfo} = {(double)codedErrors / totalInfo:E3}"));
        summary.WriteLine($"MFB coded errors per block: {string.Join(" ", blockErrs)}");
    }

    private static void AddSymbol(
        float[] output, Cf symbol, int symbolIndex,
        (float[] Taps, int First)[] pulses, IReadOnlyList<Cf[]> gains, float pathScale)
    {
        int basePos = symbolIndex * 4;
        for (int k = 0; k < pulses.Length; k++)
        {
            (float[] taps, int first) = pulses[k];
            for (int m = 0; m < taps.Length; m++)
            {
                int i = basePos + first + m;
                if (i < 0 || i >= output.Length)
                {
                    continue;
                }

                Cf g = InterpGain(gains[k], i / 100.0);
                Cf env = symbol * (taps[m] * pathScale) * g;
                double phase = 2.0 * Math.PI * 3.0 * i / 16.0;
                output[i] += (float)((env.Re * Math.Cos(phase)) - (env.Im * Math.Sin(phase)));
            }
        }
    }

    /// <summary>Projects the noise onto the symbol's two real passband templates
    /// (responses to x = 1 and x = j), whitens by the template Gram, and adds the true
    /// symbol back: ŷ = x + M⁻¹·⟨r, e⟩. Returns ŷ and the Gram entries (the per-symbol
    /// noise covariance is σ²·M⁻¹).</summary>
    private static (Cf Y, float M11, float M12, float M22) Project(
        float[] r, Cf truth, int symbolIndex,
        (float[] Taps, int First)[] pulses, IReadOnlyList<Cf[]> gains, float pathScale)
    {
        int basePos = symbolIndex * 4;
        int first = Math.Min(pulses[0].First, pulses[1].First);
        int last = Math.Max(pulses[0].First + pulses[0].Taps.Length,
            pulses[1].First + pulses[1].Taps.Length);
        double m11 = 0, m12 = 0, m22 = 0, v1 = 0, v2 = 0;
        for (int m = first; m < last; m++)
        {
            int i = basePos + m;
            if (i < 0 || i >= r.Length)
            {
                continue;
            }

            var c = Cf.Zero;
            for (int k = 0; k < pulses.Length; k++)
            {
                int t = m - pulses[k].First;
                if (t >= 0 && t < pulses[k].Taps.Length)
                {
                    c += InterpGain(gains[k], i / 100.0) * (pulses[k].Taps[t] * pathScale);
                }
            }

            double phase = 2.0 * Math.PI * 3.0 * i / 16.0;
            double cos = Math.Cos(phase);
            double sin = Math.Sin(phase);
            // e1 = response to x=1, e2 = response to x=j (passband real).
            double e1 = (c.Re * cos) - (c.Im * sin);
            double e2 = -((c.Im * cos) + (c.Re * sin));
            m11 += e1 * e1;
            m12 += e1 * e2;
            m22 += e2 * e2;
            v1 += r[i] * e1;
            v2 += r[i] * e2;
        }

        double det = Math.Max((m11 * m22) - (m12 * m12), 1e-12);
        double n1 = ((m22 * v1) - (m12 * v2)) / det;
        double n2 = ((m11 * v2) - (m12 * v1)) / det;
        return (truth + new Cf((float)n1, (float)n2), (float)m11, (float)m12, (float)m22);
    }

    private static Cf InterpGain(Cf[] trajectory, double x)
    {
        if (trajectory.Length == 1)
        {
            return trajectory[0];
        }

        double clamped = Math.Clamp(x, 0, trajectory.Length - 1);
        int i0 = (int)clamped;
        int i1 = Math.Min(trajectory.Length - 1, i0 + 1);
        float frac = (float)(clamped - i0);
        return (trajectory[i0] * (1f - frac)) + (trajectory[i1] * frac);
    }

    /// <summary>The modulator's own SRRC (Ms110dModulator.DesignPulse: 65 taps, unit
    /// energy) scaled by the TX amplitude.</summary>
    private static float[] DesignPulse(float amplitude)
    {
        const int taps = (16 * 4) + 1;
        var pulse = new float[taps];
        double centre = (taps - 1) / 2.0;
        double energy = 0;
        for (int i = 0; i < taps; i++)
        {
            double t = (i - centre) / 4.0;
            pulse[i] = (float)FilterDesign.RootRaisedCosine(t, Ms110dModulator.RollOff);
            energy += pulse[i] * pulse[i];
        }

        float norm = (float)(amplitude / Math.Sqrt(energy));
        for (int i = 0; i < taps; i++)
        {
            pulse[i] *= norm;
        }

        return pulse;
    }

    /// <summary>Fractionally delays the pulse with the channel's own windowed-sinc
    /// kernel (WattersonChannel.FractionalDelay, half = 16). Returns the taps and the
    /// index of the first tap relative to the symbol's base sample (4·symbolIndex).</summary>
    private static (float[] Taps, int First) DelayPulse(float[] pulse, double delaySamples)
    {
        if (delaySamples == 0)
        {
            return (pulse, 0);
        }

        int whole = (int)Math.Floor(delaySamples);
        double frac = delaySamples - whole;
        const int half = 16;
        int first = whole - half;
        var taps = new float[pulse.Length + (2 * half) + 1];
        for (int m = 0; m < taps.Length; m++)
        {
            double acc = 0;
            for (int j = -half + 1; j <= half; j++)
            {
                int k = (first + m) - whole - j;
                if (k < 0 || k >= pulse.Length)
                {
                    continue;
                }

                double u = j - frac;
                double w = Math.Abs(u) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * u) / (Math.PI * u) * (0.5 + (0.5 * Math.Cos(Math.PI * u / half)));
                acc += pulse[k] * w;
            }

            taps[m] = (float)acc;
        }

        return (taps, first);
    }
}
