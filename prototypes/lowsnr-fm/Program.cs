using M0LTE.Fm;
using System.Numerics;

// Sub-threshold FM probe: slow audio-tone MFSK through the limiter-discriminator model, three receivers
// for one waveform.
//  A  audio:  tone energy in the discriminator audio (what "FT8 through an FM radio" does)
//  P  phase:  integrate the discriminator audio back to a phase, exp(j.), detect the tone's sidebands
//  S  sdr:    the same sideband detector on the IF complex envelope (an SDR tap ahead of the limiter)

string Arg(string k, string d) { int i = Array.IndexOf(args, "--" + k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
int rate = int.Parse(Arg("rate", "12000"));
double T = double.Parse(Arg("T", "0.1"));
int M = int.Parse(Arg("M", "8"));
double f0 = double.Parse(Arg("f0", "1200"));
int K = int.Parse(Arg("K", "64"));
int seeds = int.Parse(Arg("seeds", "4"));
string linkName = Arg("link", "tait");
double dev = double.Parse(Arg("dev", "2500"));
int harmonics = int.Parse(Arg("harm", "2"));
double rxHigh = double.Parse(Arg("rxhigh", "0"));
double hpf = double.Parse(Arg("hpf", "0"));       // soundcard coupling corner applied to the audio, Hz (0 = DC coupled)
double inv = double.Parse(Arg("inv", "0"));       // corner the receiver *assumes* when it undoes the coupling (0 = no inverse)
double cfo = double.Parse(Arg("cfo", "0"));       // carrier offset seen by the SDR receiver, Hz
double search = double.Parse(Arg("search", "400")); // +- offset search range for the sideband detectors, Hz
double leakHz = double.Parse(Arg("leak", "0.2"));  // the inverse's integrator leak, Hz
bool track = args.Contains("--track");
bool frame = args.Contains("--frame");             // one LDPC (512,256) codeword per burst, count frames
double llrTarget = double.Parse(Arg("llr", "2.5"));   // mean |LLR| the decoder is handed; it is fussy about the range
int bitsPerSymbol = (int)Math.Round(Math.Log2(M));
var ldpc = new M0LTE.FecLdpc.LdpcFrameCodec(M0LTE.FecLdpc.LdpcCodes.H_256_512_4, 256);
var ldpcLock = new object();   // the codec keeps decoder state, so one at a time across the seeds
if (frame) { K = (ldpc.CodedBits + bitsPerSymbol - 1) / bitsPerSymbol; }            // per-symbol offset, median-smoothed along the burst, instead of one per burst
double[] cnrs = Arg("cnr", "10,6,4,2,0,-2,-4,-6,-8,-10,-12,-14").Split(',').Select(double.Parse).ToArray();

FmLinkProfile link = linkName switch
{
    "tait" => TaitTm8100.Link(TaitBandwidth.Narrow, dev),
    "tait25" => TaitTm8100.Link(TaitBandwidth.Wide, dev),
    "mic" => FmLinkProfile.MicAndSpeaker(dev),
    "data" => FmLinkProfile.DataPort(dev),
    _ => throw new ArgumentException(linkName),
};
if (rxHigh > 0) link = link with { RxAudioHighHz = rxHigh };
double D = link.PeakDeviationHz;
int N = (int)Math.Round(T * rate);
double df = 1.0 / T;
double[] tones = Enumerable.Range(0, M).Select(k => f0 + (k * df)).ToArray();
Console.WriteLine($"link={linkName} dev={D} if={link.IfBandwidthHz} txaudio={link.TxAudioLowHz}-{link.TxAudioHighHz} rxaudio={link.RxAudioLowHz}-{link.RxAudioHighHz} emph={link.DeEmphasisMicroseconds}us rate={rate} T={T}s M={M} tones={tones[0]}..{tones[^1]} beta={D / tones[0]:0.00} K={K} seeds={seeds} harm={harmonics} hpf={hpf} inv={inv} leak={leakHz} track={track} cfo={cfo}");

// ---- transmit: continuous-phase audio MFSK, unit amplitude ----
byte[] Payload(int seed) { var rng = new Random(9000 + seed); var d = new byte[ldpc.DataBits]; for (int i = 0; i < d.Length; i++) d[i] = (byte)rng.Next(2); return d; }
int[] SymbolsFor(byte[] data)
{
    var coded = new byte[ldpc.CodedBits];
    lock (ldpcLock) ldpc.Encode(data, coded);
    var syms = new int[K];
    for (int k = 0; k < K; k++)
    {
        int v = 0;
        for (int b = 0; b < bitsPerSymbol; b++) { int idx = (k * bitsPerSymbol) + b; v = (v << 1) | (idx < coded.Length ? coded[idx] : 0); }
        syms[k] = v;
    }
    return syms;
}
(float[] audio, int[] symbols) Transmit(int seed)
{
    var rng = new Random(9000 + seed);
    var syms = frame ? SymbolsFor(Payload(seed)) : new int[K];
    var a = new float[K * N];
    double ph = 0;
    for (int k = 0; k < K; k++)
    {
        if (!frame) syms[k] = rng.Next(M);
        double w = 2 * Math.PI * tones[syms[k]] / rate;
        for (int n = 0; n < N; n++) { a[(k * N) + n] = (float)Math.Sin(ph); ph += w; }
    }
    return (a, syms);
}

// ---- the soundcard: an AC coupling, and the receiver's attempt to undo it ----
static float[] OnePoleHighPass(float[] x, double cornerHz, double fs)
{
    if (cornerHz <= 0) return x;
    double a = Math.Exp(-2 * Math.PI * cornerHz / fs);
    var y = new float[x.Length];
    double prevX = 0, prevY = 0;
    for (int n = 0; n < x.Length; n++) { prevY = (a * prevY) + (x[n] - prevX); prevX = x[n]; y[n] = (float)prevY; }
    return y;
}

// Inverse of the one-pole high pass, bar its DC: y = x + c * leaky_integral(x). Exact when the corner
// matches; the leak (0.2 Hz) is what stops a real integrator wandering off on any bias.
static float[] UndoHighPass(float[] x, double assumedCornerHz, double fs, double leakHz)
{
    if (assumedCornerHz <= 0) return x;
    double c = 1 - Math.Exp(-2 * Math.PI * assumedCornerHz / fs);   // per-sample recoil the HPF applied
    double leak = Math.Exp(-2 * Math.PI * leakHz / fs);
    var y = new float[x.Length];
    double acc = 0;
    for (int n = 0; n < x.Length; n++) { y[n] = (float)(x[n] + (c * acc)); acc = (leak * acc) + x[n]; }
    return y;
}

// ---- receivers ----
// discriminator audio (full deviation = 1) -> phase -> unit circle, no offset removal at all
static (float[] r, float[] i) Reconstruct(float[] y, double D, double fs, double emphasisUs)
{
    // A de-emphasised path is a one-pole low pass on the frequency; its inverse (pre-emphasis) is
    // applied first so what is integrated is the discriminator's frequency again.
    float[] f = y;
    if (emphasisUs > 0)
    {
        double a = 1.0 - Math.Exp(-1.0 / (fs * emphasisUs * 1e-6));
        f = new float[y.Length]; double prev = 0;
        for (int n = 0; n < y.Length; n++) { f[n] = (float)((y[n] - ((1 - a) * prev)) / a); prev = y[n]; }
    }
    var r = new float[y.Length]; var im = new float[y.Length];
    double th = 0, kk = 2 * Math.PI * D / fs;
    for (int n = 0; n < y.Length; n++)
    {
        th += kk * f[n]; if (th > Math.PI) th -= 2 * Math.PI; else if (th < -Math.PI) th += 2 * Math.PI;
        r[n] = (float)Math.Cos(th); im[n] = (float)Math.Sin(th);
    }
    return (r, im);
}

// Sideband receiver: per symbol, a zero-padded FFT of z; metric(k, offset) = sum over h of
// |Z(offset + h f_k)|^2 + |Z(offset - h f_k)|^2. The offset is common to the burst, so it is chosen
// once from the sum over symbols of the best hypothesis, then every symbol is decided at it.
int SidebandDecode(float[] zr, float[] zi, int start, int len, double fs, int[] syms, out double offsetHz) => SidebandDecodeM(zr, zi, start, len, fs, syms, out offsetHz, out _);
int SidebandDecodeM(float[] zr, float[] zi, int start, int len, double fs, int[] syms, out double offsetHz, out double[][] metrics)
{
    int nfft = 1; while (nfft < len * 8) nfft *= 2;
    double binHz = fs / nfft;
    int half = (int)Math.Round(search / binHz);
    var spectra = new double[K][];
    var re = new double[nfft]; var im = new double[nfft];
    for (int k = 0; k < K; k++)
    {
        Array.Clear(re); Array.Clear(im);
        for (int n = 0; n < len; n++) { re[n] = zr[start + (k * len) + n]; im[n] = zi[start + (k * len) + n]; }
        Fft.Transform(re, im);
        var p = new double[nfft];
        for (int b = 0; b < nfft; b++) p[b] = (re[b] * re[b]) + (im[b] * im[b]);
        spectra[k] = p;
    }
    double Metric(double[] p, int hyp, int off)
    {
        double s = 0;
        for (int h = 1; h <= harmonics; h++)
        {
            int b = (int)Math.Round(h * tones[hyp] / binHz);
            s += p[((off + b) % nfft + nfft) % nfft] + p[((off - b) % nfft + nfft) % nfft];
        }
        return s;
    }
    int bestOff = 0; double bestScore = double.NegativeInfinity;
    for (int off = -half; off <= half; off++)
    {
        double score = 0;
        for (int k = 0; k < K; k++)
        {
            double best = 0;
            for (int hyp = 0; hyp < M; hyp++) best = Math.Max(best, Metric(spectra[k], hyp, off));
            score += best;
        }
        if (score > bestScore) { bestScore = score; bestOff = off; }
    }
    offsetHz = bestOff * binHz;
    var offAt = new int[K];
    Array.Fill(offAt, bestOff);
    if (track)
    {
        // Per-symbol best offset within +-search of the burst's, then a running median over
        // seven symbols: an offset that wanders slowly is followed, a single symbol's noise is not.
        var raw = new int[K];
        for (int k = 0; k < K; k++)
        {
            double bm = double.NegativeInfinity;
            for (int off = -half; off <= half; off++)
                for (int hyp = 0; hyp < M; hyp++) { double m = Metric(spectra[k], hyp, off); if (m > bm) { bm = m; raw[k] = off; } }
        }
        for (int k = 0; k < K; k++)
        {
            var window = new List<int>();
            for (int j = Math.Max(0, k - 3); j <= Math.Min(K - 1, k + 3); j++) window.Add(raw[j]);
            window.Sort();
            offAt[k] = window[window.Count / 2];
        }
    }
    int errors = 0;
    metrics = new double[K][];
    for (int k = 0; k < K; k++)
    {
        metrics[k] = new double[M];
        int best = 0; double bm = double.NegativeInfinity;
        for (int hyp = 0; hyp < M; hyp++) { double m = Metric(spectra[k], hyp, offAt[k]); metrics[k][hyp] = m; if (m > bm) { bm = m; best = hyp; } }
        if (best != syms[k]) errors++;
    }
    return errors;
}

int AudioDecode(float[] y, int start, int[] syms) => AudioDecodeM(y, start, syms, out _);
int AudioDecodeM(float[] y, int start, int[] syms, out double[][] metrics)
{
    int errors = 0;
    metrics = new double[K][];
    for (int k = 0; k < K; k++)
    {
        metrics[k] = new double[M];
        int best = 0; double bm = double.NegativeInfinity;
        for (int hyp = 0; hyp < M; hyp++)
        {
            double w = -2 * Math.PI * tones[hyp] / rate, cr = 1, ci = 0, sr = 0, si = 0, c = Math.Cos(w), sn = Math.Sin(w);
            for (int n = 0; n < N; n++) { double x = y[start + (k * N) + n]; sr += x * cr; si += x * ci; double t = (cr * c) - (ci * sn); ci = (cr * sn) + (ci * c); cr = t; }
            double m = (sr * sr) + (si * si);
            metrics[k][hyp] = m;
            if (m > bm) { bm = m; best = hyp; }
        }
        if (best != syms[k]) errors++;
    }
    return errors;
}

// Max-log bit LLRs from per-tone energies, scaled by the burst's own noise level (the median tone
// energy, which at any ratio worth decoding at is a noise-only line), then the LDPC decode.
bool DecodeFrame(double[][] metrics, byte[] expected)
{
    var all = metrics.SelectMany(m => m).OrderBy(v => v).ToArray();
    double noise = all[all.Length / 2];
    var raw = new double[ldpc.CodedBits];
    for (int k = 0; k < K; k++)
        for (int b = 0; b < bitsPerSymbol; b++)
        {
            int idx = (k * bitsPerSymbol) + b;
            if (idx >= raw.Length) break;
            int shift = bitsPerSymbol - 1 - b;
            double m0 = double.NegativeInfinity, m1 = double.NegativeInfinity;
            for (int hyp = 0; hyp < M; hyp++) { if (((hyp >> shift) & 1) == 0) m0 = Math.Max(m0, metrics[k][hyp]); else m1 = Math.Max(m1, metrics[k][hyp]); }
            raw[idx] = (m0 - m1) / noise;
        }
    // The sum-product decoder here fails on LLRs that are too confident as readily as on ones that
    // are too timid (at -18 dB a scale of 2 decodes 8 of 8, 0.5 and 8 decode 1 and 5), so they are
    // normalised to a fixed mean magnitude rather than trusted at their nominal scale.
    double meanAbs = raw.Average(Math.Abs);
    double gain = meanAbs > 0 ? llrTarget / meanAbs : 1;
    var llr = new float[ldpc.CodedBits];
    for (int i = 0; i < llr.Length; i++) llr[i] = (float)Math.Clamp(raw[i] * gain, -12, 12);
    var outData = new byte[ldpc.DataBits];
    int iters, parity;
    lock (ldpcLock) iters = ldpc.Decode(llr, outData, out parity);
    bool ok = outData.AsSpan().SequenceEqual(expected);
    if (!ok && Environment.GetEnvironmentVariable("LOWSNR_DEBUG") != null)
    {
        int wrongSign = 0; var coded = new byte[ldpc.CodedBits]; lock (ldpcLock) ldpc.Encode(expected, coded);
        for (int i = 0; i < llr.Length; i++) if ((llr[i] > 0) != (coded[i] == 0)) wrongSign++;
        int zeros = llr.Count(v => v == 0), nans = llr.Count(float.IsNaN);
        Console.WriteLine($"    frame fail: iters {iters} parity {parity} noise {noise:g3} meanAbs {meanAbs:g3} rawMin {raw.Min():g3} rawMax {raw.Max():g3} wrongSign {wrongSign} zeros {zeros} nans {nans} dataDiff {outData.Zip(expected).Count(t => t.First != t.Second)}");
    }
    return ok;
}

// The IF envelope, offset by the stated carrier error and brought down to the audio rate for the same
// receiver P uses (it sits within +-5 kHz, so the audio rate holds it).
(float[] r, float[] i) IfToAudioRate(float[] i, float[] q, int ifRate, double offsetHz)
{
    int F = ifRate / rate;
    double cutoff = rate / 2.0 * 0.9;
    float[] kernel = M0LTE.Dsp.FilterDesign.LowPass(cutoff, ifRate, 255);
    var fr = new M0LTE.Dsp.FirFilter(kernel); var fi = new M0LTE.Dsp.FirFilter(kernel);
    var r = new float[i.Length / F]; var im = new float[i.Length / F];
    double w = 2 * Math.PI * offsetHz / ifRate;
    for (int n = 0; n < i.Length; n++)
    {
        double c = Math.Cos(w * n), s = Math.Sin(w * n);
        float xr = fr.Next((float)((i[n] * c) - (q[n] * s)));
        float xi = fi.Next((float)((i[n] * s) + (q[n] * c)));
        if (n % F == 0 && n / F < r.Length) { r[n / F] = xr; im[n / F] = xi; }
    }
    return (r, im);
}

if (args.Contains("--selftest"))
{
    (float[] ta, _) = Transmit(0);
    float[] clean = new FmChannel(link, rate, 1).Apply(ta, 20);
    float[] back = UndoHighPass(OnePoleHighPass(clean, hpf, rate), inv, rate, leakHz);
    double err = 0, pow = 0; for (int n = rate; n < clean.Length; n++) { err += (back[n] - clean[n]) * (back[n] - clean[n]); pow += clean[n] * clean[n]; }
    Console.WriteLine($"selftest hpf {hpf} inv {inv}: residual {10 * Math.Log10(err / pow):0.0} dB relative to the audio");
    return;
}

// ---- genie timing at infinite CNR, per receiver ----
var genie = new FmChannel(link, rate, 1);
(float[] ga, int[] gs) = Transmit(0);
float[] gy = genie.Apply(ga, double.PositiveInfinity);
(float[] gi, float[] gq) = genie.ApplyToIf(ga, double.PositiveInfinity);
(float[] gsr, float[] gsi) = IfToAudioRate(gi, gq, genie.IfRate, 0);
(float[] gzr, float[] gzi) = Reconstruct(gy, D, rate, link.DeEmphasisMicroseconds);
int nominal = (int)(0.1 * rate);
int BestOffset(Func<int, int> errorsAt, Func<int, double> tieBreak)
{
    int best = nominal; int be = int.MaxValue; double bt = double.NegativeInfinity;
    for (int o = nominal; o <= nominal + (rate / 20); o += Math.Max(1, rate / 2000))
    {
        int e = errorsAt(o);
        if (e < be) { be = e; best = o; bt = tieBreak(o); }
        else if (e == be) { double t = tieBreak(o); if (t > bt) { bt = t; best = o; } }
    }
    return best;
}
double AudioScore(int o) { double s = 0; for (int k = 0; k < K; k++) { double w = -2 * Math.PI * tones[gs[k]] / rate, cr = 1, ci = 0, sr = 0, si = 0, c = Math.Cos(w), sn = Math.Sin(w); for (int n = 0; n < N; n++) { double x = gy[o + (k * N) + n]; sr += x * cr; si += x * ci; double t = (cr * c) - (ci * sn); ci = (cr * sn) + (ci * c); cr = t; } s += (sr * sr) + (si * si); } return s; }
int offA = BestOffset(o => AudioDecode(gy, o, gs), AudioScore);
int offP = BestOffset(o => SidebandDecode(gzr, gzi, o, N, rate, gs, out _), o => -Math.Abs(o - offA));
int offS = BestOffset(o => SidebandDecode(gsr, gsi, o, N, rate, gs, out _), o => -Math.Abs(o - offA));
Console.WriteLine($"timing: audio +{(offA - nominal) * 1000.0 / rate:0.0} ms, phase +{(offP - nominal) * 1000.0 / rate:0.0} ms, sdr +{(offS - nominal) * 1000.0 / rate:0.0} ms; ifRate={genie.IfRate}");

if (frame) Console.WriteLine($"frame mode: {ldpc.DataBits} data bits, {ldpc.CodedBits} coded, {K} symbols, {K * T:0.0} s on air, {ldpc.DataBits / (K * T):0.0} bit/s net");
Console.WriteLine(frame ? "cnr_dB  SER_audio  SER_phase  SER_sdr   FRAMES  audio  phase  sdr  of N" : "cnr_dB  SER_audio  SER_phase  SER_sdr   offsetP  offsetS  (symbols)");
foreach (double cnr in cnrs)
{
    var results = new (int a, int p, int s, double op, double os)[seeds];
    var frames = new (bool a, bool p, bool s)[seeds];
    Parallel.For(0, seeds, seed =>
    {
        (float[] a, int[] s) = Transmit(seed);
        float[] y = new FmChannel(link, rate, 100 + seed).Apply(a, cnr);
        float[] coupled = UndoHighPass(OnePoleHighPass(y, hpf, rate), inv, rate, leakHz);
        (float[] zr, float[] zi) = Reconstruct(coupled, D, rate, link.DeEmphasisMicroseconds);
        var ch2 = new FmChannel(link, rate, 100 + seed);
        (float[] i, float[] q) = ch2.ApplyToIf(a, cnr);
        (float[] sr, float[] si) = IfToAudioRate(i, q, ch2.IfRate, cfo);
        int ea = AudioDecodeM(coupled, offA, s, out double[][] ma);
        int ep = SidebandDecodeM(zr, zi, offP, N, rate, s, out double op, out double[][] mp);
        int es = SidebandDecodeM(sr, si, offS, N, rate, s, out double os, out double[][] ms);
        results[seed] = (ea, ep, es, op, os);
        if (frame) { byte[] want = Payload(seed); frames[seed] = (DecodeFrame(ma, want), DecodeFrame(mp, want), DecodeFrame(ms, want)); }
    });
    int eA = results.Sum(r => r.a), eP = results.Sum(r => r.p), eS = results.Sum(r => r.s);
    int tot = seeds * K;
    if (frame)
        Console.WriteLine($"{cnr,6:0.0}  {eA / (double)tot,9:0.000}  {eP / (double)tot,9:0.000}  {eS / (double)tot,8:0.000}          {frames.Count(f => f.a),5}  {frames.Count(f => f.p),5}  {frames.Count(f => f.s),3}  of {seeds}");
    else
        Console.WriteLine($"{cnr,6:0.0}  {eA / (double)tot,9:0.000}  {eP / (double)tot,9:0.000}  {eS / (double)tot,8:0.000}  {results.Average(r => r.op),7:0.0}  {results.Average(r => r.os),7:0.0}  ({tot})");
}

static class Fft
{
    public static void Transform(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int a = i + j, b = i + j + (len / 2);
                    double tr = (re[b] * cr) - (im[b] * ci), ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double t = (cr * wr) - (ci * wi); ci = (cr * wi) + (ci * wr); cr = t;
                }
            }
        }
    }
}
