using System.Globalization;
using Packet.SoundModem.Ofdm;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>
/// Interop oracle: the modulator's output compared against codec2 1.2.0 (git 310777b) reference
/// TX vectors checked in under <c>samples/freedv/</c> (see that folder's PROVENANCE.md). No
/// libcodec2 at test time — the vectors were generated once by <c>tools/oracle/oracle_harness.c</c>.
/// </summary>
/// <remarks>
/// The exactness contract is tiered (docs/ofdm-design.md §7.5): integer checkpoints
/// (<c>tx_nlower</c>, sample counts, <c>uw_ind_sym[]</c>) are asserted exactly; the float
/// waveform is asserted within a documented tolerance (max-abs LSB on the ±16384 scale, RMS, and
/// cross-correlation), because C <c>cosf/sinf/cabsf</c> and .NET <c>MathF</c> diverge in the last
/// ULP — literal bit-equality is unattainable, not a shortcut (§1.3).
/// </remarks>
public class OfdmModulatorOracleTests(ITestOutputHelper output)
{
    // Modes with checked-in oracle vectors, and the tolerances the pinned vectors meet. The .s16
    // tolerances are in int16 LSBs on the ±16384 scale; xcorr is the normalised cross-correlation
    // floor. Measured: xcorr = 1.0 to 8 d.p., max-abs ≈ 1.0–1.5 LSB, and RMS ≈ 0.58 — which is
    // sqrt(1/3), the exact signature of the reference's own C float→short truncation. In other
    // words the entire residual is the int16 quantisation of the reference itself; the underlying
    // float waveforms agree to ~1e-6. datac14's raw preamble (M=144, non-power-of-two idft) even
    // matches bit-for-bit.
    [Theory]
    [InlineData("datac0")]
    [InlineData("datac1")]
    [InlineData("datac3")]
    [InlineData("datac14")]
    public void Modulator_Matches_Codec2_Reference(string mode)
    {
        var m = OfdmMode.ForName(mode);
        var mod = new OfdmModulator(m);
        Meta meta = ReadMeta(mode);

        // (a) exact integer checkpoints
        meta.BitsPerPacket.Should().Be(m.BitsPerPacket);
        meta.SamplesPerPacket.Should().Be(m.SamplesPerPacket);
        meta.SamplesPerFrame.Should().Be(m.SamplesPerFrame);
        meta.TxNlower.Should().Be(m.TxNlower);
        meta.Nuwbits.Should().Be(m.Nuwbits);
        mod.UwIndexSymbols.Should().Equal(meta.UwIndSym);

        // The payload is the seed-1 LCG codec2's ofdm_generate_payload_data_bits emits.
        byte[] bits = OfdmTxTables.LcgBits(m.BitsPerPacket, 1);

        // (b) full packet (clipper + BPF) — the freedv_rawdatatx waveform
        short[] refPacket = LoadS16(Sample($"{mode}_packet.s16"));
        float[] mine = Array.ConvertAll(mod.ModulatePacketBits(bits), s => s.Re);
        Compare(mode, "packet", refPacket, mine, maxAbsLsb: 3, xcorrFloor: 0.99999);

        // (c) pre-BPF stage-2 output (amp_scale → clip_gain1 → clip → final clip)
        short[] refNoBpf = LoadS16(Sample($"{mode}_packet_nobpf.s16"));
        float[] mineNoBpf = Array.ConvertAll(mod.ModulatePacketBitsNoBpf(bits), s => s.Re);
        Compare(mode, "packet_nobpf", refNoBpf, mineNoBpf, maxAbsLsb: 3, xcorrFloor: 0.99999);

        // (d) raw preamble frame (pure idft+CP, amp_scale=1) — tightest, no clipper nonlinearity
        (float[] preRe, float[] preIm) = LoadF32Complex(Sample($"{mode}_preamble_raw.f32"));
        Cf[] minePre = mod.PreambleRaw.ToArray();
        minePre.Length.Should().Be(preRe.Length);
        double preMax = 0;
        for (int i = 0; i < preRe.Length; i++)
        {
            preMax = Math.Max(preMax, Math.Abs(preRe[i] - minePre[i].Re));
            preMax = Math.Max(preMax, Math.Abs(preIm[i] - minePre[i].Im));
        }

        output.WriteLine($"{mode} preamble_raw: maxAbs={preMax:E3} (scale ~1)");
        preMax.Should().BeLessThan(1e-3, "raw preamble is pure idft+CP; only cosf/sinf ULPs differ");
    }

    private void Compare(string mode, string tag, short[] reference, float[] mine, int maxAbsLsb, double xcorrFloor)
    {
        mine.Length.Should().Be(reference.Length, "{0} {1} length", mode, tag);

        double maxAbs = 0, sumSq = 0, dot = 0, refEnergy = 0, mineEnergy = 0, refPeak = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            double r = reference[i];
            double x = mine[i];
            double d = x - r;
            maxAbs = Math.Max(maxAbs, Math.Abs(d));
            sumSq += d * d;
            dot += r * x;
            refEnergy += r * r;
            mineEnergy += x * x;
            refPeak = Math.Max(refPeak, Math.Abs(r));
        }

        double rms = Math.Sqrt(sumSq / reference.Length);
        double xcorr = dot / Math.Sqrt(refEnergy * mineEnergy);

        output.WriteLine(
            $"{mode} {tag}: n={reference.Length} refPeak={refPeak:F0} maxAbs={maxAbs:F2} LSB " +
            $"RMS={rms:F3} xcorr={xcorr:F8}");

        maxAbs.Should().BeLessThanOrEqualTo(maxAbsLsb, "{0} {1} max sample error (±16384 scale)", mode, tag);
        rms.Should().BeLessThan(1.0, "{0} {1} RMS error on the ±16384 scale", mode, tag);
        xcorr.Should().BeGreaterThanOrEqualTo(xcorrFloor, "{0} {1} waveform correlation", mode, tag);
    }

    private static short[] LoadS16(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var outp = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, outp, 0, outp.Length * 2);
        return outp;
    }

    private static (float[] Re, float[] Im) LoadF32Complex(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        int n = raw.Length / 8;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = BitConverter.ToSingle(raw, (i * 8) + 0);
            im[i] = BitConverter.ToSingle(raw, (i * 8) + 4);
        }

        return (re, im);
    }

    private sealed record Meta(
        int BitsPerPacket, int SamplesPerPacket, int SamplesPerFrame,
        int TxNlower, int Nuwbits, int[] UwIndSym);

    private static Meta ReadMeta(string mode)
    {
        var fields = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(Sample($"{mode}_meta.txt")))
        {
            int sp = line.IndexOf(' ', StringComparison.Ordinal);
            if (sp > 0)
            {
                fields[line[..sp]] = line[(sp + 1)..];
            }
        }

        int Int(string k) => int.Parse(fields[k], CultureInfo.InvariantCulture);
        int[] uw = fields["uw_ind_sym"].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();

        // tx_nlower is printed as a float ("19"); parse via double then round.
        int nlower = (int)Math.Round(double.Parse(fields["tx_nlower"], CultureInfo.InvariantCulture));
        return new Meta(
            Int("bits_per_packet"), Int("samples_per_packet"), Int("samples_per_frame"),
            nlower, Int("nuwbits"), uw);
    }

    private static string Sample(string name) => Path.Combine(FindRepoRoot(), "samples", "freedv", name);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
