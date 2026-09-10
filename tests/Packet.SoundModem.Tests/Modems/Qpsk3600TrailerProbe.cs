using M0LTE.Dsp;
using M0LTE.Il2p;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Bench probe (set <c>TRAILER_PROBE=&lt;wav&gt;</c>): replays a capture through the qpsk3600
/// differential chain exactly as the catalogue builds it, and for every frame that comes out
/// compares the received wire bits with the wire the frame implies - header, payload blocks
/// and the trailing CRC alike - so the damage the deframer papers over with Reed-Solomon, and
/// the damage it cannot (the trailer), is laid out bit by bit against where the burst's audio
/// actually stopped.
/// </summary>
public class Qpsk3600TrailerProbe(ITestOutputHelper output)
{
    private const int Rate = 12000;
    private const int Baud = 1800;

    [Fact]
    public void Trailer_Damage_Against_Burst_End()
    {
        string? path = Environment.GetEnvironmentVariable("TRAILER_PROBE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var (raw, rate) = WavFile.ReadMono(path);
        float[] samples;
        if (rate == Rate)
        {
            samples = raw;
        }
        else
        {
            var decimator = new Decimator(rate, rate / Rate);
            samples = new float[decimator.MaxOutput(raw.Length)];
            int produced = decimator.Process(raw, samples);
            Array.Resize(ref samples, produced);
        }

        Array.Resize(ref samples, samples.Length + Rate);
        Run(samples, Path.GetFileName(path));
    }

    /// <summary>Synthetic calibration (set <c>TRAILER_PROBE_SYNTH=1</c>): one frame through our own
    /// modulator, its audio cut off at a range of points around the last symbol, so the
    /// off-air product-magnitude profile can be read as "the mute fell this many symbols
    /// before the end".</summary>
    [Fact]
    public void Synthetic_Burst_Cut_Ladder()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRAILER_PROBE_SYNTH")))
        {
            return;
        }

        var frame = new byte[15];
        new Random(7).NextBytes(frame);
        byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits: 400, Il2pFramer.PreambleStyle.Zeros);
        var modulator = new QpskModulator(Rate, Baud, 1650, 0.25);
        float[] audio = modulator.Modulate(bits, 0.5f);
        double sps = Rate / (double)Baud;
        // Our modulator's audio runs to one symbol past the last symbol's centre.
        double lastCentre = (bits.Length / 2 - 1) * sps;
        foreach (double cutSymbols in new[] { double.NaN, 8, 2, 1, 0.5, 0, -0.5, -1, -1.5, -2 })
        {
            int cut = double.IsNaN(cutSymbols) ? audio.Length : (int)Math.Round(lastCentre + (cutSymbols * sps));
            var samples = new float[Rate / 2 + audio.Length + Rate];
            Array.Copy(audio, 0, samples, Rate / 2, Math.Min(cut, audio.Length));
            string label = double.IsNaN(cutSymbols) ? "uncut" : $"cut at last symbol {cutSymbols:+0.0;-0.0} sym";
            output.WriteLine($"--- {label}: audio ends at sample {Rate / 2 + Math.Min(cut, audio.Length)}");
            Run(samples, label);
        }
    }

    /// <summary>BPSK300 leg (set <c>TRAILER_PROBE_BPSK=&lt;wav&gt;</c>): the same end-of-burst
    /// picture through the bpsk300 differential chain, so a fixed-symbol truncation and a
    /// fixed-time one can be told apart (3.3 ms per symbol here against 0.56 ms above).</summary>
    [Fact]
    public void Bpsk300_Trailer_Damage_Against_Burst_End()
    {
        string? path = Environment.GetEnvironmentVariable("TRAILER_PROBE_BPSK");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var (raw, rate) = WavFile.ReadMono(path);
        float[] samples = raw;
        if (rate != Rate)
        {
            var decimator = new Decimator(rate, rate / Rate);
            samples = new float[decimator.MaxOutput(raw.Length)];
            int produced = decimator.Process(raw, samples);
            Array.Resize(ref samples, produced);
        }

        Array.Resize(ref samples, samples.Length + Rate);
        Run(samples, Path.GetFileName(path), bpsk300: true);
    }

    private static string Row(byte[] header, int bit) =>
        string.Concat(header.Take(12).Select(b => ((b >> bit) & 1).ToString()));

    private void Run(float[] samples, string label, bool bpsk300 = false)
    {
        int baud = bpsk300 ? 300 : Baud;
        int bitsPerSymbol = bpsk300 ? 1 : 2;

        // The bit stream as the chain delivers it, with the input sample each dibit was
        // decided on, so a frame can be placed in the audio.
        var bits = new List<byte>();
        var bitSample = new List<long>();
        var symbolMargin = new List<float>();
        var symbolMagnitude = new List<float>();
        QpskDemodulator? demod = null;
        BpskDemodulator? bpskDemod = null;
        var frames = new List<(byte[] Frame, Il2pDecodeInfo Info, Il2pDelivery Delivery, int EndBit)>();
        var receiver = new Il2pReceiver(
            (frame, info, delivery) => frames.Add((frame, info, delivery, bits.Count)),
            crcMode: true);

        if (bpsk300)
        {
            bpskDemod = new BpskDemodulator(
                Rate, static _ => { }, 1500, 300, PskDetector.Differential,
                softBitSink: (bit, confidence, phase) =>
                {
                    if (phase != 0)
                    {
                        return;
                    }

                    bits.Add((byte)bit);
                    bitSample.Add(bpskDemod!.InputSamplePosition);
                    symbolMargin.Add(confidence);
                    receiver.PushBit(bit, confidence);
                });
            bpskDemod.SymbolPlotted = (re, im) => symbolMagnitude.Add(MathF.Abs(re));
        }
        else
        {
            demod = new QpskDemodulator(
                Rate, Baud, static (_, _) => { }, 1650, PskDetector.Differential,
                loopBandwidthHz: Baud * 0.03, rollOff: 0.25, decisionFeedback: false,
                softDibitSink: (first, second, confidence, phase) =>
                {
                    if (phase != 0)
                    {
                        return;
                    }

                    bits.Add((byte)first);
                    bitSample.Add(demod!.InputSamplePosition);
                    bits.Add((byte)second);
                    bitSample.Add(demod!.InputSamplePosition);
                    symbolMargin.Add(confidence);
                    receiver.PushBit(first, confidence);
                    receiver.PushBit(second, confidence);
                });
            demod.SymbolPlotted = (re, im) => symbolMagnitude.Add(MathF.Sqrt((re * re) + (im * im)));
        }

        // Busy falling edge resets, as QpskModem does.
        bool previousBusy = false;
        const int block = 240;
        for (int at = 0; at < samples.Length; at += block)
        {
            int n = Math.Min(block, samples.Length - at);
            bool busy;
            if (bpsk300)
            {
                bpskDemod!.Process(samples.AsSpan(at, n));
                busy = bpskDemod.CarrierDetect;
            }
            else
            {
                demod!.Process(samples.AsSpan(at, n));
                busy = demod.ChannelBusy;
            }

            if (previousBusy && !busy)
            {
                receiver.Reset();
            }

            previousBusy = busy;
        }

        output.WriteLine($"{label}: {samples.Length / (double)Rate:0.0} s, {frames.Count} frames, {bits.Count} bits");

        // Envelope of the band-passed input, 1 ms blocks, for the end-of-burst picture.
        var bandPass = new FirFilter(FilterDesign.BandPass(
            bpsk300 ? 1500 - 300 : 1650 - 1800, bpsk300 ? 1500 + 300 : 1650 + 1800, Rate, 256));
        var envelope = new float[samples.Length / 12 + 1];
        double acc = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float f = bandPass.Next(samples[i]);
            acc += f * f;
            if (i % 12 == 11)
            {
                envelope[i / 12] = (float)Math.Sqrt(acc / 12);
                acc = 0;
            }
        }

        int syncBits = 24;
        var syncPattern = Il2pFramer.FrameBits([], 0);
        var syncHits = new List<(int At, bool Inverted)>();
        for (int at = 0; at + 24 <= bits.Count; at++)
        {
            int direct = 0;
            for (int i = 0; i < 24; i++)
            {
                if (bits[at + i] == syncPattern[i]) { direct++; }
            }

            if (direct >= 23) { syncHits.Add((at, false)); }
            else if (direct <= 1) { syncHits.Add((at, true)); }
        }

        foreach (var (frame, info, delivery, endBit) in frames)
        {
            byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);
            byte[] expected = Il2pFramer.FrameBits(wire, 0);
            // The wire the station actually sent: our encoder's header choice when the
            // decoded header type agrees, else the type-0 layout (raw AX.25 as payload).
            int wireBytes = info.HeaderType == Il2pHeaderType.Type0
                ? Il2pCodec.HeaderWireLength + Il2pBlockLayout.Compute(frame.Length).WireLength + 4
                : wire.Length;
            bool bodyComparable = wireBytes == wire.Length;
            int wireBits = wireBytes * 8;
            int frameBits = syncBits + wireBits;
            // Place by the sync word: the nearest hit whose frame would end at or before the
            // delivery (the CRC reading delivers on the trailer's last bit, the plain one 32 bits on).
            var hit = syncHits.Where(h => h.At + frameBits <= endBit + 2 && h.At + frameBits >= endBit - 64)
                .OrderByDescending(h => h.At).DefaultIfEmpty((At: -1, Inverted: false)).First();
            if (hit.At < 0)
            {
                output.WriteLine($"  frame {frame.Length} B delivered at bit {endBit}: no sync word hit places it (hits: {string.Join(" ", syncHits.Select(h => h.At))})");
                continue;
            }

            int bestStart = hit.At;
            if (hit.Inverted)
            {
                expected = expected.Select(b => (byte)(b ^ 1)).ToArray();
            }

            int bestErrors = 0;
            for (int i = 0; i < syncBits; i++)
            {
                if (bits[bestStart + i] != expected[i]) { bestErrors++; }
            }

            int bodyErrors = 0;
            var trailerErrors = new List<int>();
            var bodyErrorPositions = new List<int>();
            int trailerAt = bestStart + syncBits + wireBits - 32;
            for (int i = 0; i < 32; i++)
            {
                int bitIndex = trailerAt + i;
                if (bitIndex < bits.Count && bits[bitIndex] != expected[expected.Length - 32 + i])
                {
                    trailerErrors.Add(i);
                }
            }

            if (bodyComparable)
            {
                for (int i = syncBits; i < expected.Length - 32; i++)
                {
                    int bitIndex = bestStart + i;
                    if (bitIndex < bits.Count && bits[bitIndex] != expected[i])
                    {
                        bodyErrors++;
                        bodyErrorPositions.Add(i - syncBits);
                    }
                }
            }
            else
            {
                bodyErrors = -1;
            }

            expected = new byte[frameBits]; // only its length is used below
            long lastBitSample = bitSample[Math.Min(bits.Count - 1, bestStart + expected.Length - 1)];
            long firstBitSample = bitSample[bestStart];
            // Symbol rate as our clock sees it: the frame's span in input samples over its symbols.
            double measuredBaud = (double)Rate * ((frameBits / bitsPerSymbol) - 1) / (lastBitSample - firstBitSample);
            double t0 = firstBitSample / (double)Rate;
    
            // Where the audio stops: last 1 ms block whose envelope is above half the burst's median.
            int e0 = (int)(firstBitSample / 12), e1 = (int)(lastBitSample / 12);
            var burst = envelope[e0..Math.Min(e1 + 1, envelope.Length)].ToArray();
            Array.Sort(burst);
            float median = burst.Length > 0 ? burst[burst.Length / 2] : 0;
            int stop = e1;
            while (stop + 1 < envelope.Length && envelope[stop + 1] > median * 0.5f)
            {
                stop++;
            }

            int start1 = e0;
            while (start1 - 1 >= 0 && envelope[start1 - 1] > median * 0.5f)
            {
                start1--;
            }

            double tailMs = (stop * 12 + 12 - lastBitSample) / (Rate / 1000.0);
            double leadMs = (firstBitSample - start1 * 12) / (Rate / 1000.0);
            // What follows the burst: envelope 5-50 ms after the audio stops, relative to the burst.
            float after = 0;
            int afterCount = 0;
            for (int k = stop + 5; k < Math.Min(stop + 50, envelope.Length); k++)
            {
                after += envelope[k];
                afterCount++;
            }

            after = afterCount > 0 ? after / afterCount : 0;

            string verdict = info.CrcValid == true ? "crc ok"
                : delivery.TrailerNearBits is int near ? $"trailer near {near}"
                : delivery.PlainIl2p ? "plain" : "?";
            output.WriteLine(
                $"  {t0,8:0.000}s {frame.Length,3} B {verdict,-16} fec {info.CorrectedSymbols}  last bit stamp {lastBitSample}  {info.HeaderType} {measuredBaud:0.0} Bd  "
                + $"sync errs {bestErrors}  body bit errs {(bodyErrors < 0 ? "n/a" : bodyErrors.ToString())}  "
                + $"trailer bit errs [{string.Join(",", trailerErrors)}]  "
                + $"lead {leadMs:0.0} ms  tail after last bit {tailMs:0.0} ms  after/median {after / Math.Max(median, 1e-9f):0.00}");
            if (bodyErrorPositions.Count > 0 && bodyErrorPositions.Count <= 40)
            {
                output.WriteLine($"      body errs at wire bits: {string.Join(",", bodyErrorPositions)} of {wireBits - 32}");
            }

            if (bestStart >= 0 && info.HeaderType == Il2pHeaderType.Type1)
            {
                // Received header wire against ours, and what each decodes to.
                var receivedHeader = new byte[Il2pCodec.HeaderWireLength];
                for (int i = 0; i < receivedHeader.Length * 8; i++)
                {
                    int bit = bits[bestStart + syncBits + i] ^ (hit.Inverted ? 1 : 0);
                    receivedHeader[i / 8] |= (byte)(bit << (7 - (i % 8)));
                }

                bool theirs = Il2pCodec.TryDecodeHeader(receivedHeader, out var theirType, out int theirCount, out int theirFec);
                bool ours = Il2pCodec.TryDecodeHeader(wire.AsSpan(0, Il2pCodec.HeaderWireLength), out var ourType, out int ourCount, out int ourFec);
                output.WriteLine($"      header wire theirs {Convert.ToHexString(receivedHeader)} -> {theirs} {theirType} payload {theirCount} fec {theirFec}");
                output.WriteLine($"      header wire ours   {Convert.ToHexString(wire.AsSpan(0, Il2pCodec.HeaderWireLength))} -> {ours} {ourType} payload {ourCount} fec {ourFec}");
                output.WriteLine($"      ax25 {Convert.ToHexString(frame.AsSpan(0, Math.Min(20, frame.Length)))}");
                var plainTheirs = receivedHeader.AsSpan(0, 13).ToArray();
                var plainOurs = wire.AsSpan(0, 13).ToArray();
                Il2pScrambler.Descramble(plainTheirs);
                Il2pScrambler.Descramble(plainOurs);
                output.WriteLine($"      plain theirs {Convert.ToHexString(plainTheirs)} row7 {Row(plainTheirs, 7)} row6 {Row(plainTheirs, 6)}");
                output.WriteLine($"      plain ours   {Convert.ToHexString(plainOurs)} row7 {Row(plainOurs, 7)} row6 {Row(plainOurs, 6)}");
            }

            // Envelope of the last 20 ms of the burst and 20 ms after, 1 ms per cell, relative to median.
            var cells = new List<string>();
            for (int k = e1 - 20; k <= e1 + 20 && k < envelope.Length; k++)
            {
                if (k < 0) { continue; }
                cells.Add((envelope[k] / Math.Max(median, 1e-9f)).ToString("0.0"));
            }

            output.WriteLine($"      env (1 ms cells, -20..+20 ms around last bit, /median): {string.Join(" ", cells)}");

            // Symbol decision margins (confidence) for the last 24 symbols of the frame and 8 after.
            int lastSymbol = (bestStart + expected.Length - 1) / bitsPerSymbol;
            var margins = new List<string>();
            for (int s = lastSymbol - 23; s <= lastSymbol + 8 && s < symbolMargin.Count; s++)
            {
                if (s < 0) { continue; }
                margins.Add(s == lastSymbol ? $"[{symbolMargin[s]:0.00}]" : symbolMargin[s].ToString("0.00"));
            }

            output.WriteLine($"      confidence last 24 symbols (+8 after): {string.Join(" ", margins)}");

            // Product magnitude over the same symbols, relative to the frame's median magnitude.
            int firstSymbol = bestStart / bitsPerSymbol;
            var mags = symbolMagnitude.Skip(firstSymbol).Take(lastSymbol - firstSymbol + 1).OrderBy(m => m).ToArray();
            float magMedian = mags.Length > 0 ? mags[mags.Length / 2] : 1;
            var magCells = new List<string>();
            for (int s = lastSymbol - 23; s <= lastSymbol + 16 && s < symbolMagnitude.Count; s++)
            {
                if (s < 0) { continue; }
                string cell = (symbolMagnitude[s] / magMedian).ToString("0.00");
                magCells.Add(s == lastSymbol ? $"[{cell}]" : cell);
            }

            output.WriteLine($"      |product| last 24 symbols (+16 after) /median: {string.Join(" ", magCells)}");

            // Raw input envelope, no filter, 0.5 ms cells, -20..+20 ms around the last bit's stamp.
            var rawCells = new List<string>();
            double rawRef = 0;
            int refCount = 0;
            for (long k = firstBitSample; k < lastBitSample; k++)
            {
                rawRef += samples[k] * samples[k];
                refCount++;
            }

            rawRef = Math.Sqrt(rawRef / Math.Max(1, refCount));
            int rawCell = bpsk300 ? 36 : 6; // 3 ms cells at 300 Bd, 0.5 ms at 1800 Bd
            for (long c = lastBitSample - (40 * rawCell); c <= lastBitSample + (40 * rawCell); c += rawCell)
            {
                double e = 0;
                for (long k = c; k < c + rawCell && k < samples.Length; k++)
                {
                    if (k >= 0) { e += samples[k] * samples[k]; }
                }

                rawCells.Add((Math.Sqrt(e / rawCell) / Math.Max(rawRef, 1e-9)).ToString("0.0"));
            }

            // Where the raw audio actually mutes: the first cell after -20 ms that falls below
            // 0.15 of the burst rms and is followed by another such cell. Reported relative to
            // the last bit's stamp; the stamp trails the true last symbol by the chain delay.
            double? muteMs = null;
            for (int i = 0; i + 1 < rawCells.Count; i++)
            {
                if (double.Parse(rawCells[i]) < 0.15 && double.Parse(rawCells[i + 1]) < 0.15)
                {
                    muteMs = (i - 40) * rawCell / 12.0;
                    break;
                }
            }

            output.WriteLine($"      raw env ({rawCell / 12.0:0.0} ms cells, -40..+40 cells around last bit stamp, /rms): {string.Join(" ", rawCells)}");
            output.WriteLine($"      audio mutes {(muteMs is null ? "beyond +20 ms" : $"{muteMs:+0.0;-0.0} ms")} vs last bit stamp");
            if (Environment.GetEnvironmentVariable("TRAILER_PROBE_DUMP") is { Length: > 0 } dumpDir)
            {
                Directory.CreateDirectory(dumpDir);
                long from = Math.Max(0, lastBitSample - Rate / 10);
                long to = Math.Min(samples.Length, lastBitSample + Rate / 10);
                var seg = samples[(int)from..(int)to];
                File.WriteAllLines(Path.Combine(dumpDir, $"burst-{t0:0.000}.txt"), seg.Select(v => v.ToString("R")));
            }
        }
    }
}
