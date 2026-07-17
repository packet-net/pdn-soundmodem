using Packet.SoundModem.Ofdm;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>
/// First-light round-trip proof for the FreeDV datac OFDM chain: <see cref="DatacTransmitter"/> →
/// audio → <see cref="DatacReceiver"/> → payload. It closes the loop the two half-ports
/// (modulator + demodulator, each already validated against codec2 1.2.0 git 310777b) leave open,
/// asserting that a payload transmitted through the full LDPC + CRC + interleaver + OFDM TX chain
/// is recovered byte-for-byte with a valid CRC — clean, and under a carrier-frequency and a
/// sample-clock offset. A separate case cross-checks the transmitter's full-chain output against
/// codec2's own <c>freedv_rawdatacomptx</c> vector (<c>samples/freedv/datac0_clean.s16</c>).
/// </summary>
public class DatacRoundTripTests(ITestOutputHelper output)
{
    // gen_datac0.c framing: a couple of frames of lead-in silence, then the continuous packet
    // stream, then a few frames of trailing silence so the streaming demod can drain.
    private const int LeadFrames = 2;
    private const int TrailFrames = 3;

    /// <summary>A distinct, deterministic payload per packet so recovery proves real data flow,
    /// not a fixed frame echoed back.</summary>
    private static byte[] Payload(int nbytes, int packet)
    {
        var p = new byte[nbytes];
        for (int i = 0; i < nbytes; i++)
        {
            p[i] = (byte)(((packet * 31) + (i * 17) + 5) & 0xff);
        }

        return p;
    }

    /// <summary>Builds a continuous streaming transmission of <paramref name="payloads"/> (one
    /// modulator instance ⇒ persistent BPF), framed with lead/trail silence and rotated by an
    /// optional carrier offset (gen_datac0.c: <c>re·cos φ − im·sin φ</c>, phase continuous across
    /// the whole stream), returned as 8&#160;kHz real PCM.</summary>
    private static short[] BuildStream(OfdmMode mode, IReadOnlyList<byte[]> payloads, double foffHz)
    {
        var tx = new DatacTransmitter(mode);
        var audio = new List<Cf>();
        for (int i = 0; i < LeadFrames * mode.SamplesPerFrame; i++)
        {
            audio.Add(Cf.Zero);
        }

        foreach (byte[] p in payloads)
        {
            audio.AddRange(tx.ModulatePacket(p));
        }

        for (int i = 0; i < TrailFrames * mode.SamplesPerFrame; i++)
        {
            audio.Add(Cf.Zero);
        }

        double dphi = 2.0 * Math.PI * foffHz / mode.Fs;
        var samples = new short[audio.Count];
        for (int i = 0; i < audio.Count; i++)
        {
            double phase = dphi * i;
            double rr = (audio[i].Re * Math.Cos(phase)) - (audio[i].Im * Math.Sin(phase));
            double v = Math.Round(rr, MidpointRounding.AwayFromZero);
            samples[i] = v >= 32767.0 ? (short)32767 : v <= -32768.0 ? (short)-32768 : (short)v;
        }

        return samples;
    }

    /// <summary>Matches decoded packets to the transmitted payloads: every CRC-valid result must
    /// carry one of the sent payloads, and they must arrive in transmit order. Returns how many
    /// distinct sent payloads were recovered.</summary>
    private int AssertPayloadsRecoveredInOrder(
        IReadOnlyList<byte[]> sent, IReadOnlyList<DatacRxResult> results, int payloadBytes)
    {
        int matched = 0;
        int next = 0;
        foreach (DatacRxResult r in results)
        {
            if (!r.CrcOk)
            {
                continue;
            }

            // Advance through the sent list to the payload this result carries (packets may be
            // dropped during acquisition, but never reordered).
            while (next < sent.Count && !r.Payload.SequenceEqual(sent[next]))
            {
                next++;
            }

            next.Should().BeLessThan(sent.Count, "a CRC-valid packet must carry a transmitted payload");
            r.Payload.Length.Should().Be(payloadBytes);
            matched++;
            next++;
        }

        return matched;
    }

    [Fact]
    public void RoundTrips_Datac0_Clean_Stream()
    {
        const int n = 10;
        var mode = OfdmMode.Datac0;
        var payloads = Enumerable.Range(0, n).Select(p => Payload(14, p)).ToList();

        short[] stream = BuildStream(mode, payloads, foffHz: 0.0);
        IReadOnlyList<DatacRxResult> results = new DatacReceiver(mode).Process(stream);
        int crcOk = results.Count(r => r.CrcOk);
        output.WriteLine($"clean: transmitted {n}, decoded {results.Count}, {crcOk} CRC-OK");

        int matched = AssertPayloadsRecoveredInOrder(payloads, results, payloads[0].Length);
        matched.Should().Be(n, "every packet in a clean self-round-trip must decode to its payload");
    }

    [Fact]
    public void RoundTrips_Datac0_With_Carrier_Frequency_Offset()
    {
        const int n = 10;
        var mode = OfdmMode.Datac0;
        var payloads = Enumerable.Range(0, n).Select(p => Payload(14, p)).ToList();

        // +25 Hz: within the demod's ±60 Hz acquisition range; the coarse (±40) + fine + tracking
        // estimator must lock and the loop must still recover the data.
        short[] stream = BuildStream(mode, payloads, foffHz: 25.0);
        var rx = new DatacReceiver(mode);
        IReadOnlyList<DatacRxResult> results = rx.Process(stream);
        int crcOk = results.Count(r => r.CrcOk);
        output.WriteLine($"foff+25: transmitted {n}, decoded {results.Count}, {crcOk} CRC-OK, foffEst={rx.Demod.FoffEstHz:F2} Hz");

        int matched = AssertPayloadsRecoveredInOrder(payloads, results, payloads[0].Length);
        matched.Should().BeGreaterThanOrEqualTo(n - 1, "a +25 Hz carrier offset must not cost more than the acquisition packet");
        rx.Demod.FoffEstHz.Should().BeApproximately(25.0f, 3.0f, "the frequency estimator must converge on the injected offset");
    }

    [Theory]
    [InlineData(600)]
    [InlineData(-600)]
    public void RoundTrips_Datac0_With_Sample_Clock_Offset(int ppm)
    {
        const int n = 10;
        var mode = OfdmMode.Datac0;
        var payloads = Enumerable.Range(0, n).Select(p => Payload(14, p)).ToList();

        short[] clean = BuildStream(mode, payloads, foffHz: 0.0);
        short[] skewed = Resample(clean, 1.0 + (ppm / 1_000_000.0));

        IReadOnlyList<DatacRxResult> results = new DatacReceiver(mode).Process(skewed);
        int crcOk = results.Count(r => r.CrcOk);
        output.WriteLine($"clock {ppm:+#;-#} ppm: transmitted {n}, decoded {results.Count}, {crcOk} CRC-OK");

        int matched = AssertPayloadsRecoveredInOrder(payloads, results, payloads[0].Length);
        matched.Should().BeGreaterThanOrEqualTo(7, "integer sample-clock tracking must ride out a {0} ppm skew", ppm);
    }

    [Fact]
    public void RoundTrips_Datac3_Clean_Stream()
    {
        // datac3: different geometry (Np=29, 126-byte payload, the H_1024_2048_4f LDPC) — proves
        // the transmitter is mode-generic, not datac0-special. One packet is plenty of data here.
        const int n = 3;
        var mode = OfdmMode.Datac3;
        var payloads = Enumerable.Range(0, n).Select(p => Payload(126, p)).ToList();

        short[] stream = BuildStream(mode, payloads, foffHz: 0.0);
        IReadOnlyList<DatacRxResult> results = new DatacReceiver(mode).Process(stream);
        int crcOk = results.Count(r => r.CrcOk);
        output.WriteLine($"datac3 clean: transmitted {n}, decoded {results.Count}, {crcOk} CRC-OK");

        int matched = AssertPayloadsRecoveredInOrder(payloads, results, payloads[0].Length);
        matched.Should().Be(n, "every datac3 packet in a clean self-round-trip must decode to its payload");
    }

    [Fact]
    public void Transmitter_Matches_Codec2_Rawdatacomptx_FullChain()
    {
        // Cross-validate the WHOLE TX chain (CRC + LDPC + interleaver + OFDM + clipper/BPF) against
        // codec2 1.2.0's own freedv_rawdatacomptx output — the checked-in datac0_clean.s16, which
        // gen_datac0.c produced from the standard payload (i*17+5)&0xff as [2 lead frames of
        // silence][10 continuous packets][3 trail frames]. Reproducing the same 10-packet region
        // (float real part vs the reference int16) closes the full-chain loop without libcodec2.
        var mode = OfdmMode.Datac0;
        short[] reference = ReadS16("datac0_clean.s16");

        var payload = new byte[14];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(((i * 17) + 5) & 0xff);
        }

        // Sanity: our appended CRC must equal codec2's — the whole 16-byte datac0_clean.frame.
        byte[] refFrame = File.ReadAllBytes(Sample("datac0_clean.frame"));
        ushort crc = M0LTE.Fec.FreedvCrc16.Compute(payload);
        byte[] mineFrame = [.. payload, (byte)(crc >> 8), (byte)(crc & 0xff)];
        mineFrame.Should().Equal(refFrame, "our payload+CRC frame must match codec2's transmitted frame");

        var tx = new DatacTransmitter(mode);
        var mine = new List<float>();
        for (int p = 0; p < 10; p++)
        {
            mine.AddRange(tx.ModulatePacket(payload).Select(s => s.Re));
        }

        int lead = LeadFrames * mode.SamplesPerFrame;                 // silence prefix in the fixture
        int packetRegion = 10 * mode.SamplesPerPacket;
        mine.Count.Should().Be(packetRegion);
        reference.Length.Should().BeGreaterThanOrEqualTo(lead + packetRegion);

        double maxAbs = 0, sumSq = 0, dot = 0, refEnergy = 0, mineEnergy = 0, refPeak = 0;
        for (int i = 0; i < packetRegion; i++)
        {
            double r = reference[lead + i];
            double x = mine[i];
            double d = x - r;
            maxAbs = Math.Max(maxAbs, Math.Abs(d));
            sumSq += d * d;
            dot += r * x;
            refEnergy += r * r;
            mineEnergy += x * x;
            refPeak = Math.Max(refPeak, Math.Abs(r));
        }

        double rms = Math.Sqrt(sumSq / packetRegion);
        double xcorr = dot / Math.Sqrt(refEnergy * mineEnergy);
        output.WriteLine(
            $"vs codec2 rawdatacomptx: n={packetRegion} refPeak={refPeak:F0} maxAbs={maxAbs:F2} LSB RMS={rms:F3} xcorr={xcorr:F8}");

        // Only the reference's own int16 quantisation (and cosf/sinf last-ULP) separates the two —
        // the same tiered-exactness contract the modulator oracle test asserts.
        maxAbs.Should().BeLessThanOrEqualTo(2, "residual is codec2's int16 quantisation of our matching float waveform");
        rms.Should().BeLessThan(1.0, "full-chain waveform RMS error on the ±16384 scale");
        xcorr.Should().BeGreaterThanOrEqualTo(0.99999, "full-chain waveform must correlate with codec2's");
    }

    /// <summary>Linear-interpolation resampler used only to emulate a sample-clock offset (mirrors
    /// <c>DatacDemodTests.Resample</c>).</summary>
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

    private static short[] ReadS16(string name)
    {
        byte[] raw = File.ReadAllBytes(Sample(name));
        var samples = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);
        return samples;
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
