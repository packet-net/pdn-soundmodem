using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Measurement rig for issue #342: how far apart, in samples, do a diversity bank's branches
/// deliver copies of the same transmission, and how does the bank's carrier detect behave
/// around a burst. The numbers this prints are what size the banks' dedupe windows; the
/// windows' comments quote them. Run with <c>DEDUPE_SKEW_PROBE=1</c>; output to
/// <c>/tmp/dedupe-skew.txt</c>.
/// </summary>
public class BankDedupeSkewProbe
{
    private const int SampleRate = 12000;
    private const int Block = 10;   // feed granularity: 10 samples = 0.83 ms resolution

    private static readonly byte[] ShortFrame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static byte[] LongFrame()
    {
        var frame = new byte[200];
        ShortFrame.CopyTo(frame, 0);
        for (int i = ShortFrame.Length; i < frame.Length; i++)
        {
            frame[i] = (byte)i;
        }

        return frame;
    }

    private sealed record Arrival(string Branch, long Sample, bool MonitorOnly);

    private static List<Arrival> FeedInBlocks(
        float[] audio, IReadOnlyList<(string Name, IModem Modem)> branches)
    {
        var arrivals = new List<Arrival>();
        long position = 0;
        foreach (var (name, modem) in branches)
        {
            long at() => position;
            modem.FrameDecoded += (_, quality) =>
                arrivals.Add(new Arrival(name, at(), quality.MonitorOnly));
        }

        for (int i = 0; i < audio.Length; i += Block)
        {
            var slice = audio.AsSpan(i, Math.Min(Block, audio.Length - i));
            foreach (var (_, modem) in branches)
            {
                modem.Process(slice);
            }

            position += slice.Length;
        }

        return arrivals;
    }

    private static void Report(StreamWriter report, string label, List<Arrival> arrivals)
    {
        if (arrivals.Count == 0)
        {
            report.WriteLine($"{label}: NO DECODES");
            return;
        }

        var delivered = arrivals.Where(a => !a.MonitorOnly).ToList();
        var monitor = arrivals.Where(a => a.MonitorOnly).ToList();
        long all = arrivals.Max(a => a.Sample) - arrivals.Min(a => a.Sample);
        report.WriteLine(FormattableString.Invariant(
            $"{label}: {arrivals.Count} copies ({delivered.Count} delivered, {monitor.Count} monitor-only)"));
        if (delivered.Count > 0)
        {
            long spread = delivered.Max(a => a.Sample) - delivered.Min(a => a.Sample);
            report.WriteLine(FormattableString.Invariant(
                $"  delivered spread: {spread} samples = {spread * 1000.0 / SampleRate:F1} ms"));
        }

        report.WriteLine(FormattableString.Invariant(
            $"  all-copy spread:  {all} samples = {all * 1000.0 / SampleRate:F1} ms"));
        foreach (var group in arrivals.GroupBy(a => a.Sample).OrderBy(g => g.Key))
        {
            report.WriteLine(FormattableString.Invariant(
                $"    at {group.Key}: {string.Join(", ", group.Select(a => a.Branch + (a.MonitorOnly ? "(mon)" : "")))}"));
        }
    }

    [Fact]
    public void Measure_Branch_Delivery_Skew_Per_Bank()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("DEDUPE_SKEW_PROBE") is null, "bench probe only");
        using var report = new StreamWriter("/tmp/dedupe-skew.txt");

        foreach (byte[] frame in new[] { ShortFrame, LongFrame() })
        {
            report.WriteLine($"== frame length {frame.Length} bytes ==");

            // qpsk600 / qpsk2400: the catalogue's 4-pair banks, differential, plus the
            // ensemble's coherent twins (the widest configuration the catalogue can build).
            foreach (int baud in new[] { 300, 1200 })
            {
                double step = baud / 40.0;
                var branches = new List<(string, IModem)>();
                foreach (PskDetector detector in new[] { PskDetector.Differential, PskDetector.Coherent })
                {
                    for (int k = -4; k <= 4; k++)
                    {
                        QpskModem branch = baud == 300
                            ? QpskModem.Qpsk600(SampleRate, static _ => { }, crc: true,
                                detector: detector, carrierFrequency: 1500 + (k * step))
                            : QpskModem.Qpsk2400(SampleRate, static _ => { }, crc: true,
                                detector: detector, carrierFrequency: 1500 + (k * step));
                        branches.Add(($"{detector.ToString()[..3]}{(k >= 0 ? "+" : "")}{k * step}Hz", branch));
                    }
                }

                float[] audio = Pad(QpskMultiModem
                    .Qpsk600(SampleRate, static _ => { })
                    .Modulate(frame, 150));
                if (baud == 1200)
                {
                    audio = Pad(QpskMultiModem
                        .Qpsk2400(SampleRate, static _ => { })
                        .Modulate(frame, 150));
                }

                Report(report, $"qpsk{baud * 2} bank (9 offsets x 2 detectors)",
                    FeedInBlocks(audio, branches));
            }

            // bpsk300 / bpsk1200: same shape.
            foreach (int baud in new[] { 300, 1200 })
            {
                double step = baud / 40.0;
                var branches = new List<(string, IModem)>();
                foreach (PskDetector detector in new[] { PskDetector.Differential, PskDetector.Coherent })
                {
                    for (int k = -4; k <= 4; k++)
                    {
                        branches.Add(($"{detector.ToString()[..3]}{(k >= 0 ? "+" : "")}{k * step}Hz",
                            new BpskModem(SampleRate, static _ => { }, crc: true,
                                1500 + (k * step), baud, detector: detector)));
                    }
                }

                float[] audio = Pad(new BpskMultiModem(
                        SampleRate, static _ => { }, crc: true, 1500, baud)
                    .Modulate(frame, 150));
                Report(report, $"bpsk{baud} bank (9 offsets x 2 detectors)",
                    FeedInBlocks(audio, branches));
            }

            // afsk300-il2pc: the catalogue's 5-pair tight-filtered bank.
            {
                var branches = new List<(string, IModem)>();
                for (int k = -5; k <= 5; k++)
                {
                    branches.Add(($"{(k >= 0 ? "+" : "")}{k * 35}Hz",
                        new Afsk300Modem(SampleRate, static _ => { }, Afsk300Framing.Il2pCrc,
                            1700 + (k * 35), bandPassHalfWidth: 250, lowPassCutoff: 250)));
                }

                float[] audio = Pad(new Afsk300MultiModem(SampleRate, static _ => { })
                    .Modulate(frame, 240));
                Report(report, "afsk300-il2pc bank (11 branches)", FeedInBlocks(audio, branches));
            }

            report.WriteLine();
        }

        // afsk1200-multi: raw deframer chains, 7 offsets x 3 emphasis x timing phases,
        // exactly as Afsk1200MultiModem wires them (its branches have no per-branch dedupe,
        // so every phase copy reaches the bank deduper).
        {
            var arrivals = new List<Arrival>();
            long position = 0;
            int phases = AfskDemodulator.TimingPhaseCount;
            var demods = new List<AfskDemodulator>();
            var emphasisOrders = new List<int>();
            for (int e = 0; e < 3; e++)
            {
                for (int k = -3; k <= 3; k++)
                {
                    double offset = k * 30;
                    string name = $"{(k >= 0 ? "+" : "")}{offset}Hz+{e * 6}dB";
                    var deframers = new HdlcDeframer[phases];
                    var nrzi = new NrziDecoder[phases];
                    for (int phase = 0; phase < phases; phase++)
                    {
                        nrzi[phase] = new NrziDecoder();
                        string phaseName = $"{name}p{phase}";
                        deframers[phase] = new HdlcDeframer(_ =>
                            arrivals.Add(new Arrival(phaseName, position, MonitorOnly: false)));
                    }

                    demods.Add(new AfskDemodulator(
                        SampleRate, static _ => { }, 1700 + offset,
                        phaseBitSink: (level, phase) =>
                            deframers[phase].PushBit(nrzi[phase].Decode(level))));
                    emphasisOrders.Add(e);
                }
            }

            float[] audio = Pad(new Afsk1200MultiModem(SampleRate, static _ => { }, offsetPairs: 3)
                .Modulate(ShortFrame, 240));
            var previous1 = new float[demods.Count];
            var previous2 = new float[demods.Count];
            var scratch = new float[Block];
            for (int i = 0; i < audio.Length; i += Block)
            {
                var slice = audio.AsSpan(i, Math.Min(Block, audio.Length - i));
                for (int d = 0; d < demods.Count; d++)
                {
                    // Inline copy of Afsk1200MultiModem's private EmphasisFilter.
                    int order = emphasisOrders[d];
                    if (order == 0)
                    {
                        demods[d].Process(slice);
                        continue;
                    }

                    for (int s = 0; s < slice.Length; s++)
                    {
                        float x = slice[s];
                        float d1 = x - previous1[d];
                        previous1[d] = x;
                        if (order == 1)
                        {
                            scratch[s] = d1;
                        }
                        else
                        {
                            scratch[s] = d1 - previous2[d];
                            previous2[d] = d1;
                        }
                    }

                    demods[d].Process(scratch.AsSpan(0, slice.Length));
                }

                position += slice.Length;
            }

            Report(report, "afsk1200-multi (7 offsets x 3 emphasis x phases)", arrivals);
        }

        // DCD behaviour around a burst and a 600 ms-gap repeat, per catalogue bank mode:
        // when does bank-level CarrierDetect rise and fall, and is there a clean drop in a
        // realistic ARQ gap for an acquisition boundary to be seen.
        report.WriteLine();
        report.WriteLine("== bank DCD around one burst + 600 ms gap + repeat ==");
        foreach (string mode in new[]
        {
            "afsk1200-multi", "afsk300-il2pc", "bpsk300", "bpsk1200", "qpsk600", "qpsk2400", "qpsk3600",
        })
        {
            int rate = ModemCatalog.DspRateFor(mode);
            IModem modem = ModemCatalog.Create(mode, rate, static _ => { });
            float[] burst = modem.Modulate(ShortFrame, 150);
            int gap = rate * 6 / 10;
            int pad = rate / 2;
            var audio = new float[pad + burst.Length + gap + burst.Length + pad];
            burst.CopyTo(audio, pad);
            burst.CopyTo(audio, pad + burst.Length + gap);

            IModem receiver = ModemCatalog.Create(mode, rate, static _ => { });
            bool last = false;
            var edges = new List<string>();
            for (int i = 0; i < audio.Length; i += Block)
            {
                receiver.Process(audio.AsSpan(i, Math.Min(Block, audio.Length - i)));
                bool dcd = receiver.CarrierDetect;
                if (dcd != last)
                {
                    edges.Add(FormattableString.Invariant(
                        $"{(dcd ? "rise" : "fall")}@{i * 1000.0 / rate:F0}ms"));
                    last = dcd;
                }
            }

            string line = FormattableString.Invariant(
                $"{mode}: burst1 {pad * 1000.0 / rate:F0}..{(pad + burst.Length) * 1000.0 / rate:F0} ms, burst2 {(pad + burst.Length + gap) * 1000.0 / rate:F0}..{(pad + (2 * burst.Length) + gap) * 1000.0 / rate:F0} ms");
            report.WriteLine($"{line}; edges: {string.Join(" ", edges)}");
        }
    }

    private static float[] Pad(float[] audio)
    {
        int pad = SampleRate / 2;
        var padded = new float[audio.Length + (2 * pad)];
        audio.CopyTo(padded, pad);
        return padded;
    }
}
