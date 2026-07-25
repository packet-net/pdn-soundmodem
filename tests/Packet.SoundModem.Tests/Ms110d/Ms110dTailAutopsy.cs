using M0LTE.Fec;
using M0LTE.Ofdm;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ms110d.Fec;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// Env-gated corpse-reproduction rig for the B3 catastrophic-burst tail autopsy
/// (phase-b-plan §B3, issue #69). The mask harness's per-burst census
/// (<c>MS110D_MASK_BURST_LOG</c>) localizes which bursts die; this rig rebuilds ONE such
/// burst bit-exactly — same payload draw sequence, same channel seed, same settings as
/// <see cref="Ms110dMaskTests"/> — and dumps every first-pass equalized data symbol
/// NEXT TO ITS TRUE transmitted constellation point, so the death mechanism (rotation
/// lock, fade misalignment, timing walk-off…) is measurable instead of guessed.
/// Not a gate — an instrument. <c>MS110D_AUTOPSY=1</c>, <c>MS110D_AUTOPSY_WN</c>
/// (default 5), <c>MS110D_AUTOPSY_SNR</c> (default the WN's Poor mask),
/// <c>MS110D_AUTOPSY_SEED</c> (base seed, default 500+WN — add any census seed offset
/// yourself), <c>MS110D_AUTOPSY_WORKER</c> (default 0), <c>MS110D_AUTOPSY_BURST</c>
/// (default 0), <c>MS110D_AUTOPSY_OUT</c> (directory, default "."),
/// <c>MS110D_AUTOPSY_GENIE=1</c> for the genie-aided companion dump.
/// </summary>
public class Ms110dTailAutopsy
{
    private static readonly Dictionary<int, double> PoorSnr = new()
    {
        { 0, -1 }, { 1, 3 }, { 2, 5 }, { 3, 7 }, { 4, 10 }, { 5, 11 }, { 6, 14 },
        { 7, 19 }, { 8, 23 }, { 13, 11 },
    };

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
    }

    /// <summary>Mirror of the modulator's private Transcode (Tables D-III/D-IV/D-V):
    /// fetched wire bits at <paramref name="bit"/> → descrambled 8PSK ring index.</summary>
    private static int RingIndex(byte[] fetched, ref int bit, Ms110dModulation modulation)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            return fetched[bit++] == 0 ? 0 : 4;
        }

        if (modulation == Ms110dModulation.Psk8)
        {
            int tribit = (fetched[bit++] << 2) | (fetched[bit++] << 1) | fetched[bit++];
            return Ms110dTables.Transcode8Psk[tribit];
        }

        int msb = fetched[bit++];
        int lsb = fetched[bit++];
        return ((msb << 1) | lsb) switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 };
    }

    [Fact]
    public void Mask_Burst_Corpse_Dump()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_AUTOPSY") != "1",
            "set MS110D_AUTOPSY=1 for the tail-autopsy corpse dump");

        int wn = EnvInt("MS110D_AUTOPSY_WN", 5);
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_AUTOPSY_SNR"), out double s)
            ? s : PoorSnr[wn];
        int baseSeed = EnvInt("MS110D_AUTOPSY_SEED", 500 + wn);
        int worker = EnvInt("MS110D_AUTOPSY_WORKER", 0);
        int burst = EnvInt("MS110D_AUTOPSY_BURST", 0);
        string outDir = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_OUT") ?? ".";
        bool genie = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_GENIE") == "1";

        // Bit-exact reproduction of Ms110dMaskTests.RunPointWorker's burst construction.
        int workerSeed = baseSeed + (worker * 1_000_000);
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        tx.Mode.Modulation.Should().NotBe(Ms110dModulation.Qam16,
            "the tail autopsy targets the sub-8PSK points; QAM16 truth mapping is not wired");
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        double blockSeconds = wn == 0
            ? il.Frames * 32.0 / 2400
            : il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;

        var random = new Random(workerSeed);
        var payload = new byte[payloadBitsPerBurst];
        for (int b = 0; b <= burst; b++)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)random.Next(2); // prior bursts advance the draw sequence
            }
        }

        float[] audio = tx.Modulate(payload);
        int channelSeed = workerSeed + (1000 * burst) + 1;
        var channel = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor)
        {
            RecordGains = true,
        };
        float[] rx = channel.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        // Per-symbol truth: re-encode the TX stream to wire bits per block (the same
        // telemetry path as the mask harness) and transcode to descrambled ring points.
        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
        int txBlocks = txBits.Length / il.InputBits;
        ConvolutionalCode code = ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, tx.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        int symbolsPerBlock = il.Frames * tx.Mode.U;
        var refRing = new int[txBlocks * symbolsPerBlock];
        var fetchedBlocks = new byte[txBlocks][];
        for (int b = 0; b < txBlocks; b++)
        {
            byte[] fetched = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
            fetchedBlocks[b] = fetched;
            int bit = 0;
            for (int sym = 0; sym < symbolsPerBlock; sym++)
            {
                refRing[(b * symbolsPerBlock) + sym] = RingIndex(fetched, ref bit, tx.Mode.Modulation);
            }
        }

        string tag = $"wn{wn}-w{worker}-b{burst}{(genie ? "-genie" : "")}";
        using var symbols = new StreamWriter(Path.Combine(outDir, $"autopsy-symbols-{tag}.csv"));
        symbols.WriteLine("index,re,im,refIdx,refRe,refIm");
        using var frames = new StreamWriter(Path.Combine(outDir, $"autopsy-frames-{tag}.log"));
        using var bitErrs = new StreamWriter(Path.Combine(outDir, $"autopsy-biterrs-{tag}.csv"));
        bitErrs.WriteLine("block,symbolInBlock,bitInSymbol");

        int symbolIndex = 0;
        int bitsPerSymbol = tx.Mode.BitsPerSymbol;
        long uncodedErrors = 0, uncodedBits = 0;
        var decoded = new List<byte>(payload.Length + 64);
        Ms110dBurstEndReason? endReason = null;
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions
        {
            DisableTurbo = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_NOTURBO") == "1",
            TrackRidge = float.TryParse(
                Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TRACK_RIDGE"), out float tr)
                ? tr : null,
        });
        demod.BurstCompleted += bu => endReason = bu.Reason;
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.DataSymbolEqualized += y =>
        {
            int i = symbolIndex++;
            if (i < refRing.Length)
            {
                Cf p = Ms110dTables.Psk8[refRing[i]];
                symbols.WriteLine($"{i},{y.Re:F5},{y.Im:F5},{refRing[i]},{p.Re:F5},{p.Im:F5}");
            }
            else
            {
                symbols.WriteLine($"{i},{y.Re:F5},{y.Im:F5},-1,0,0"); // post-EOM, no truth
            }
        };
        demod.FrameDiagnostics += line => frames.WriteLine(line);
        demod.FirstPassBlockLlrs += (blockIndex, llrs) =>
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            byte[] fetched = fetchedBlocks[blockIndex];
            int compareBits = Math.Min(llrs.Length, fetched.Length);
            uncodedBits += compareBits;
            for (int i = 0; i < compareBits; i++)
            {
                if ((llrs[i] > 0 ? 0 : 1) != fetched[i])
                {
                    uncodedErrors++;
                    bitErrs.WriteLine($"{blockIndex},{i / bitsPerSymbol},{i % bitsPerSymbol}");
                }
            }
        };

        if (genie)
        {
            float[] clean = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor).Apply(
                audio, double.PositiveInfinity, leadInSamples: 2400, leadOutSamples: 2400);
            for (int i = 0; i < rx.Length; i += 4800)
            {
                int length = Math.Min(4800, rx.Length - i);
                demod.WriteGenie(clean.AsSpan(i, length));
                demod.Process(rx.AsSpan(i, length));
            }
        }
        else
        {
            demod.Process(rx);
        }

        using (var gainsOut = new StreamWriter(Path.Combine(outDir, $"autopsy-gains-{tag}.csv")))
        {
            gainsOut.WriteLine("index,p0re,p0im,p1re,p1im");
            IReadOnlyList<Cf[]> gains = channel.LastPathGains!;
            int count = Math.Max(gains[0].Length, gains[1].Length);
            for (int i = 0; i < count; i++)
            {
                Cf g0 = gains[0][Math.Min(i, gains[0].Length - 1)];
                Cf g1 = gains[1][Math.Min(i, gains[1].Length - 1)];
                gainsOut.WriteLine($"{i},{g0.Re:F5},{g0.Im:F5},{g1.Re:F5},{g1.Im:F5}");
            }
        }

        long codedErrors = 0, firstErr = -1, lastErr = -1;
        int compared = Math.Min(decoded.Count, payload.Length);
        for (int i = 0; i < compared; i++)
        {
            if (decoded[i] != payload[i])
            {
                codedErrors++;
                if (firstErr < 0)
                {
                    firstErr = i;
                }

                lastErr = i;
            }
        }

        File.WriteAllText(
            Path.Combine(outDir, $"autopsy-decoded-{tag}.txt"),
            string.Concat(decoded.Select(b => b.ToString())) + "\n" +
            string.Concat(payload.Select(b => b.ToString())) + "\n");
        File.WriteAllText(
            Path.Combine(outDir, $"autopsy-summary-{tag}.txt"),
            $"WN{wn} @ {snrDb} dB baseSeed {baseSeed} worker {worker} burst {burst} " +
            $"(channelSeed {channelSeed}) genie={genie}\n" +
            $"payload {payload.Length} bits in {txBlocks} blocks ({symbolsPerBlock} symbols/block), " +
            $"decoded {decoded.Count}, coded errors {codedErrors} " +
            $"(first {firstErr}, last {lastErr}), uncoded {uncodedErrors}/{uncodedBits}, " +
            $"collapses {demod.CollapseResolves}, turbo {demod.TurboConverged}c/" +
            $"{demod.TurboReverted}r/{demod.TurboAborted}a/{demod.TurboSkipped}s, " +
            $"end={endReason}, {symbolIndex} symbols dumped, " +
            $"lock={demod.Lock?.WaveformNumber}/{demod.Lock?.Interleaver}/K{demod.Lock?.ConstraintLength}" +
            $"@{demod.Lock?.CfoHz:F2}Hz (tx K{settings.ConstraintLength})\n");
        decoded.Count.Should().BeGreaterThan(0,
            "the corpse must at least acquire for the dump to mean anything");
    }
}
