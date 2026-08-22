using M0LTE.Il2p;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The issue #336 rig: the sim ladder's own burst (its frame, its AWGN calibration, its seeds,
/// its 100 ms blocks) through a <see cref="C4fskModem"/> with the bench seams open, read back
/// to what the receiver looked like when the sync word arrived. Shared by the
/// <see cref="C4fskTxDelayProbe"/> bench report and the <see cref="C4fskTxDelayTests"/>
/// regression cover.
/// </summary>
internal static class C4fskTxDelayRig
{
    internal const int Rate = 48000;
    internal const int FrameBytes = 60;
    internal const int FrontSymbols = 64;

    private static readonly int[] DibitToLevel = [2, 3, 1, 0];

    /// <summary>One burst's reading.</summary>
    /// <param name="Decoded">Whether the sent frame came back.</param>
    /// <param name="SyncSymbol">Phase 0's symbol index of the first wire symbol, or -1 if its
    /// stream never carried the sync word (to the deframer's one-bit tolerance).</param>
    /// <param name="FrozenAtSync">Whether phase 0's equalizer was frozen at that symbol.</param>
    /// <param name="HalfAtStart">The envelope half-swing 20 symbols into the burst.</param>
    /// <param name="HalfAtSync">The envelope half-swing at the sync.</param>
    /// <param name="CleanHalf">The half-swing the noiseless burst leaves the tracker at (1 when
    /// not measured).</param>
    /// <param name="SyncNormalised">Mean |slicer input| over the sync's last 12 symbols, all of
    /// them outer; 1.0 is an envelope that is exactly right.</param>
    /// <param name="SyncEqualized">The same after the equalizer.</param>
    /// <param name="TapDeviation">Euclidean distance of phase 0's taps from identity at the
    /// sync.</param>
    /// <param name="Taps">Phase 0's taps at the sync.</param>
    /// <param name="FrontOuter">Mean |slicer input| of the first 64 wire symbols sent as outer
    /// levels.</param>
    /// <param name="FrontInner">The same for the inner levels.</param>
    /// <param name="FrontErrors">Phase 0's symbol error rate over the first 64 wire symbols.</param>
    /// <param name="FrontDemotions">How many of those errors read an outer symbol as inner.</param>
    /// <param name="WholeErrors">Phase 0's symbol error rate over the whole wire.</param>
    /// <param name="ErrorsByQuarter">Phase 0's symbol errors in each quarter of the wire.</param>
    /// <param name="HalfTrajectory">The envelope half-swing against the clean burst's at the
    /// sync and 100, 200 and 300 symbols after it.</param>
    /// <param name="PhaseErrors">Every timing phase's symbol errors over the whole wire, scored
    /// from phase 0's sync position (the phases decide in lockstep).</param>
    /// <param name="TapsLate">Phase 0's taps 200 symbols into the wire.</param>
    internal sealed record Burst(
        bool Decoded, int SyncSymbol, bool FrozenAtSync, float HalfAtStart, float HalfAtSync,
        float CleanHalf, double SyncNormalised, double SyncEqualized, double TapDeviation,
        float[] Taps, double FrontOuter, double FrontInner, double FrontErrors, int FrontDemotions,
        double WholeErrors, int[] ErrorsByQuarter, double[] HalfTrajectory, int[] PhaseErrors,
        float[] TapsLate);

    internal static Burst Run(string mode, double snr, int txd, int seed, bool cleanReference = true)
    {
        byte[] frame = Frame(FrameBytes, seed);
        byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);
        int[] expected = ExpectedLevels(wire);

        float[] clean = Trim(Make(mode, _ => { }).Modulate(frame, txd));
        float cleanHalf = cleanReference ? CleanHalf(mode, clean) : 1f;

        var channel = new WattersonChannel(Rate, seed + 3_000_000);
        float[] rx = channel.Apply(
            clean, snr, noiseBandwidthHz: 3000, leadInSamples: Rate / 2, leadOutSamples: Rate * 6 / 5);

        bool decoded = false;
        var rows = new List<C4fskModem.Decision>();
        var tapRows = new List<float[]>();
        var bits = new List<int>();
        var phaseBits = new List<int>[C4fskModem.TimingPhaseCount];
        for (int phase = 0; phase < phaseBits.Length; phase++)
        {
            phaseBits[phase] = [];
        }
        C4fskModem modem = Make(mode, f => decoded |= f.AsSpan().SequenceEqual(frame));
        modem.DecisionObserver = d =>
        {
            if (d.Phase != 0)
            {
                return;
            }

            rows.Add(d);
            var taps = new float[C4fskModem.EqualizerLength];
            modem.CopyEqualizerTaps(0, taps);
            tapRows.Add(taps);
        };
        modem.PhaseDibitObserver = (phase, first, second) =>
        {
            phaseBits[phase].Add(first);
            phaseBits[phase].Add(second);
            if (phase == 0)
            {
                bits.Add(first);
                bits.Add(second);
            }
        };

        int block = Rate / 10;
        for (int pos = 0; pos < rx.Length; pos += block)
        {
            modem.Process(rx.AsSpan(pos, Math.Min(block, rx.Length - pos)));
        }

        int syncBit = SyncBitIndex(bits, wire.Length);
        if (syncBit < 0)
        {
            return new Burst(decoded, -1, false, 0, 0, cleanHalf, 0, 0, 0, [], 0, 0, 0, 0, 0, [], [], [], []);
        }

        // Bits and decision rows are both one entry per phase-0 symbol, in lockstep.
        int syncSymbol = syncBit / 2;
        int start = Math.Min(20, syncSymbol);
        C4fskModem.Decision atSync = rows[syncSymbol];
        C4fskModem.Decision atStart = rows[start];
        double syncNorm = 0;
        double syncEq = 0;
        for (int s = syncSymbol - 12; s < syncSymbol; s++)
        {
            syncNorm += Math.Abs(rows[s].Normalised);
            syncEq += Math.Abs(rows[s].Equalized);
        }

        var trajectory = new double[4];
        for (int q = 0; q < trajectory.Length; q++)
        {
            C4fskModem.Decision at = rows[Math.Min(rows.Count - 1, syncSymbol + (100 * q))];
            trajectory[q] = (at.PeakHigh - at.PeakLow) * 0.5 / cleanHalf;
        }

        var phaseErrors = new int[phaseBits.Length];
        for (int phase = 0; phase < phaseBits.Length; phase++)
        {
            for (int k = 0; k < expected.Length && syncBit + (2 * k) + 1 < phaseBits[phase].Count; k++)
            {
                int dibit = (phaseBits[phase][syncBit + (2 * k)] << 1) | phaseBits[phase][syncBit + (2 * k) + 1];
                phaseErrors[phase] += DibitToLevel[dibit] != expected[k] ? 1 : 0;
            }
        }

        float[] tapsAtSync = tapRows[syncSymbol];
        double deviation = 0;
        for (int t = 0; t < tapsAtSync.Length; t++)
        {
            double identity = t == tapsAtSync.Length / 2 ? 1 : 0;
            deviation += (tapsAtSync[t] - identity) * (tapsAtSync[t] - identity);
        }

        double frontOuter = 0;
        double frontInner = 0;
        int outerCount = 0;
        int innerCount = 0;
        int frontErrors = 0;
        int frontDemotions = 0;
        int wholeErrors = 0;
        var byQuarter = new int[4];
        for (int k = 0; k < expected.Length && syncSymbol + k < rows.Count; k++)
        {
            // The equalizer decides the centre of its five-symbol history, so the decision in
            // row k is of the input that arrived two rows earlier.
            C4fskModem.Decision d = rows[syncSymbol + k];
            C4fskModem.Decision input = rows[syncSymbol + k - (C4fskModem.EqualizerLength / 2)];
            bool wrong = d.Level != expected[k];
            wholeErrors += wrong ? 1 : 0;
            byQuarter[Math.Min(3, 4 * k / expected.Length)] += wrong ? 1 : 0;
            if (k >= FrontSymbols)
            {
                continue;
            }

            frontErrors += wrong ? 1 : 0;
            if (expected[k] is 0 or 3)
            {
                frontDemotions += wrong && d.Level is 1 or 2 ? 1 : 0;
                frontOuter += Math.Abs(input.Normalised);
                outerCount++;
            }
            else
            {
                frontInner += Math.Abs(input.Normalised);
                innerCount++;
            }
        }

        return new Burst(
            decoded, syncSymbol, atSync.Frozen,
            (atStart.PeakHigh - atStart.PeakLow) * 0.5f, (atSync.PeakHigh - atSync.PeakLow) * 0.5f,
            cleanHalf, syncNorm / 12, syncEq / 12, Math.Sqrt(deviation), tapsAtSync,
            outerCount == 0 ? 0 : frontOuter / outerCount, innerCount == 0 ? 0 : frontInner / innerCount,
            (double)frontErrors / FrontSymbols, frontDemotions, (double)wholeErrors / expected.Length,
            byQuarter, trajectory, phaseErrors, tapRows[Math.Min(tapRows.Count - 1, syncSymbol + 200)]);
    }

    /// <summary>Phase 0's rows from 20 symbols before the sync word to 12 after it, one line
    /// each, with the envelope against the clean reference.</summary>
    internal static IEnumerable<string> RowsAroundSync(string mode, double snr, int txd, int seed)
    {
        byte[] frame = Frame(FrameBytes, seed);
        byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);
        float[] clean = Trim(Make(mode, _ => { }).Modulate(frame, txd));
        float cleanHalf = CleanHalf(mode, clean);
        var channel = new WattersonChannel(Rate, seed + 3_000_000);
        float[] rx = channel.Apply(
            clean, snr, noiseBandwidthHz: 3000, leadInSamples: Rate / 2, leadOutSamples: Rate * 6 / 5);
        var rows = new List<C4fskModem.Decision>();
        var bits = new List<int>();
        C4fskModem modem = Make(mode, _ => { });
        modem.DecisionObserver = d =>
        {
            if (d.Phase == 0)
            {
                rows.Add(d);
            }
        };
        modem.PhaseDibitObserver = (phase, first, second) =>
        {
            if (phase == 0)
            {
                bits.Add(first);
                bits.Add(second);
            }
        };
        modem.Process(rx);
        int syncSymbol = SyncBitIndex(bits, wire.Length) / 2;
        for (int s = syncSymbol - 20; s < syncSymbol + 12; s++)
        {
            C4fskModem.Decision d = rows[s];
            yield return $"row {s - syncSymbol,4}: in {d.Normalised,7:+0.000;-0.000} eq {d.Equalized,7:+0.000;-0.000} "
                + $"level {d.Level} frozen {(d.Frozen ? 1 : 0)} half/clean {(d.PeakHigh - d.PeakLow) * 0.5 / cleanHalf:0.000}";
        }
    }

    /// <summary>The envelope half-swing a noiseless copy of the burst settles the tracker at by
    /// the end of its run-in: the reference the noisy runs' envelopes are compared with.</summary>
    private static float CleanHalf(string mode, float[] clean)
    {
        float half = 0;
        var bits = new List<int>();
        C4fskModem modem = Make(mode, _ => { });
        modem.DecisionObserver = d =>
        {
            if (d.Phase == 0)
            {
                half = (d.PeakHigh - d.PeakLow) * 0.5f;
            }
        };
        var audio = new float[(Rate / 2) + clean.Length + Rate];
        clean.CopyTo(audio, Rate / 2);
        modem.Process(audio);
        return half;
    }

    private static C4fskModem Make(string mode, Action<byte[]> received) =>
        mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(Rate, received)
            : C4fskModem.C4fsk19200(Rate, received);

    /// <summary>The sim rig's frame: an AX.25-looking UI header then a seeded random body.</summary>
    private static byte[] Frame(int bytes, int seed)
    {
        byte[] frame = new byte[bytes];
        ReadOnlySpan<byte> header =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0,
        ];
        header[..Math.Min(header.Length, bytes)].CopyTo(frame);
        if (bytes > header.Length)
        {
            new Random(seed).NextBytes(frame.AsSpan(header.Length));
        }

        return frame;
    }

    private static int[] ExpectedLevels(byte[] wire)
    {
        var levels = new int[wire.Length * 4];
        for (int i = 0; i < wire.Length; i++)
        {
            for (int k = 0; k < 4; k++)
            {
                int dibit = (wire[i] >> (6 - (2 * k))) & 3;
                levels[(i * 4) + k] = DibitToLevel[dibit];
            }
        }

        return levels;
    }

    private static float[] Trim(float[] audio)
    {
        int start = 0;
        while (start < audio.Length && audio[start] == 0f)
        {
            start++;
        }

        int end = audio.Length;
        while (end > start && audio[end - 1] == 0f)
        {
            end--;
        }

        return audio[start..end];
    }

    /// <summary>Where the wire's first bit sits in phase 0's decided stream, hunting the sync
    /// word with the deframer's own one-bit tolerance; -1 if it never arrived.</summary>
    private static int SyncBitIndex(List<int> decided, int wireBytes)
    {
        const int syncWord = 0x57DF7F;
        int hunt = 0;
        for (int i = 0; i < decided.Count; i++)
        {
            hunt = ((hunt << 1) | decided[i]) & 0xFFFFFF;
            if (System.Numerics.BitOperations.PopCount((uint)(hunt ^ syncWord)) <= 1
                && i + 1 + (8 * wireBytes) <= decided.Count)
            {
                return i + 1;
            }
        }

        return -1;
    }
}
