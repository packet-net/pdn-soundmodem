using Packet.SoundModem.Ofdm;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>
/// End-to-end validation of the FreeDV datac0 OFDM demodulator against codec2 1.2.0 (git 310777b)
/// reference transmissions checked into <c>samples/freedv/</c>. Each <c>.s16</c> is a continuous
/// stream of identical datac0 modem packets produced by <c>freedv_rawdatacomptx</c> (see
/// <c>samples/freedv/README.md</c> for the exact command), which codec2's own streaming RX
/// decodes; here our pure-managed demodulator + streaming sync state machine + LDPC + CRC must
/// recover the same 16-byte frame (14-byte payload + big-endian CRC-16) with the CRC passing.
/// </summary>
/// <remarks>
/// Per the tiered-exactness contract, the clean and pure-frequency-offset streams are decoded
/// exactly (every packet, right bytes); the noisy streams are validated statistically (nearly all
/// packets CRC-valid), never bit-exact.
/// </remarks>
public class DatacDemodTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    /// <summary>Creates the test with xUnit's output sink.</summary>
    public DatacDemodTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string SamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        return Path.Combine(root, "samples", "freedv");
    }

    private static short[] ReadS16(string name)
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(SamplesDir(), name));
        var samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private static byte[] ExpectedFrame() => File.ReadAllBytes(Path.Combine(SamplesDir(), "datac0_clean.frame"));

    private IReadOnlyList<DatacRxResult> Decode(short[] samples)
    {
        var rx = new DatacReceiver(OfdmMode.Datac0);
        return rx.Process(samples);
    }

    [Fact]
    public void Decodes_Codec2_Datac3_Clean_Stream()
    {
        // datac3 exercises the same code path with different geometry (Nc=9, Np=29, 126-byte
        // payload, the H_1024_2048_4f LDPC) — proving the demod is mode-generic, not datac0-special.
        short[] samples = ReadS16("datac3_clean.s16");
        byte[] expected = File.ReadAllBytes(Path.Combine(SamplesDir(), "datac3_clean.frame"));

        var rx = new DatacReceiver(OfdmMode.Datac3);
        IReadOnlyList<DatacRxResult> results = rx.Process(samples);
        _out.WriteLine($"datac3 clean: decoded {results.Count} packets, {results.Count(r => r.CrcOk)} CRC-OK");

        results.Should().HaveCount(4, "every packet in the 4-packet datac3 stream must decode");
        results.Should().OnlyContain(r => r.CrcOk && r.Bytes.SequenceEqual(expected));
    }

    [Fact]
    public void Decodes_Codec2_Datac0_Clean_Stream()
    {
        IReadOnlyList<DatacRxResult> results = Decode(ReadS16("datac0_clean.s16"));
        _out.WriteLine($"clean: decoded {results.Count} packets, {results.Count(r => r.CrcOk)} CRC-OK");

        // codec2's own streaming RX decodes 10/10; a faithful port must match exactly.
        results.Should().HaveCount(10, "every packet in the 10-packet clean stream must decode");
        results.Should().OnlyContain(r => r.CrcOk, "every clean-stream CRC must pass");
        results.Should().OnlyContain(r => r.Bytes.SequenceEqual(ExpectedFrame()),
            "every clean frame must match codec2's transmitted bytes exactly");
    }

    [Fact]
    public void Decodes_Datac0_With_Carrier_Frequency_Offset()
    {
        // +45 Hz needs coarse grid (40) + fine (~5) — exercises the full acquisition freq range.
        var rx = new DatacReceiver(OfdmMode.Datac0);
        IReadOnlyList<DatacRxResult> results = rx.Process(ReadS16("datac0_foff.s16"));
        _out.WriteLine($"foff+45: decoded {results.Count} packets, {results.Count(r => r.CrcOk)} CRC-OK, foffEst={rx.Demod.FoffEstHz:F2} Hz");

        results.Should().HaveCount(10, "a +45 Hz offset is within the ±60 Hz acquisition range");
        results.Should().OnlyContain(r => r.CrcOk && r.Bytes.SequenceEqual(ExpectedFrame()));

        // Sub-block check: the coarse (±40 grid) + fine (±20) + tracking estimate must lock onto
        // the injected offset, not merely decode.
        rx.Demod.FoffEstHz.Should().BeApproximately(45.0f, 2.0f, "the frequency estimator must converge on the offset");
    }

    [Fact]
    public void Frequency_Estimator_Reads_Zero_On_A_Clean_Stream()
    {
        var rx = new DatacReceiver(OfdmMode.Datac0);
        rx.Process(ReadS16("datac0_clean.s16"));
        rx.Demod.FoffEstHz.Should().BeApproximately(0.0f, 1.0f, "no offset was injected");
    }

    [Theory]
    [InlineData("datac0_noise.s16", "AWGN, σ≈6000 (~+7 dB in-band)")]
    [InlineData("datac0_noise_foff.s16", "AWGN σ≈3000 + 35 Hz offset")]
    public void Decodes_Datac0_Under_Noise(string fixture, string label)
    {
        IReadOnlyList<DatacRxResult> results = Decode(ReadS16(fixture));
        int crcOk = results.Count(r => r.CrcOk);
        _out.WriteLine($"{label}: decoded {results.Count} packets, {crcOk} CRC-OK");

        // 20-packet streams; codec2 decodes 19/20 (first packet lost to acquisition). Statistical
        // validation: nearly all packets recovered, and every CRC-OK packet is bit-correct.
        crcOk.Should().BeGreaterThanOrEqualTo(17, "the datac0 LDPC recovers almost every packet at this SNR");
        results.Where(r => r.CrcOk).Should().OnlyContain(r => r.Bytes.SequenceEqual(ExpectedFrame()),
            "any packet that passes CRC must carry the correct payload");
    }

    [Theory]
    [InlineData(600)]
    [InlineData(-600)]
    public void Decodes_Datac0_With_Sample_Clock_Offset(int ppm)
    {
        // Resample the clean stream to emulate a TX/RX sample-clock mismatch — the classic
        // "loopback passes, hardware fails" case. The demod has no fractional resampler; it
        // absorbs the drift by nudging nin ±SamplesPerSymbol/4 (codec2 ofdm.c:1937-1961).
        short[] clean = ReadS16("datac0_clean.s16");
        short[] skewed = Resample(clean, 1.0 + (ppm / 1_000_000.0));

        IReadOnlyList<DatacRxResult> results = Decode(skewed);
        int crcOk = results.Count(r => r.CrcOk);
        _out.WriteLine($"clock {ppm:+#;-#} ppm: decoded {results.Count} packets, {crcOk} CRC-OK");

        crcOk.Should().BeGreaterThanOrEqualTo(7, "integer sample-clock tracking must ride out a {0} ppm skew", ppm);
        results.Where(r => r.CrcOk).Should().OnlyContain(r => r.Bytes.SequenceEqual(ExpectedFrame()));
    }

    /// <summary>Linear-interpolation resampler used only to emulate a sample-clock offset.</summary>
    private static short[] Resample(short[] input, double ratio)
    {
        int outLen = (int)(input.Length / ratio);
        var outp = new short[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i * ratio;
            int i0 = (int)srcPos;
            double frac = srcPos - i0;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            outp[i] = (short)Math.Round((input[i0] * (1.0 - frac)) + (input[i1] * frac));
        }

        return outp;
    }
}
