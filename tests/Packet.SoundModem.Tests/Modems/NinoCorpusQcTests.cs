using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Env-gated QC pass over a NinoTNC capture corpus (the studybox bench,
/// docs/ninotnc-loop.md): every WAV decodes through its paired catalog mode, expecting
/// the number of frames the capture driver transmitted. Not a gate — an instrument for
/// corpus quality control. <c>NINO_CORPUS_DIR</c> points at the corpus,
/// <c>NINO_CORPUS_FRAMES</c> (default 3) the expected per-file frame count.
/// </summary>
public class NinoCorpusQcTests
{
    // DIP → catalog mode, the docs/ninotnc-loop.md §Results pairing (C4FSK rows added
    // when those modes landed with the FM work).
    private static readonly Dictionary<string, string> DipToMode = new()
    {
        ["0000"] = "fsk9600",
        ["0001"] = "c4fsk19200",
        ["0010"] = "fsk9600-il2p",
        ["0011"] = "c4fsk9600",
        ["0100"] = "fsk4800-il2p",
        ["0101"] = "qpsk3600",
        ["0110"] = "afsk1200",
        ["0111"] = "afsk1200-il2p",
        ["1000"] = "bpsk300",
        ["1001"] = "qpsk600",
        ["1010"] = "bpsk1200",
        ["1011"] = "qpsk2400",
        ["1100"] = "afsk300",
        ["1101"] = "afsk300-il2p",
        ["1110"] = "afsk300-il2pc",
    };

    [Fact]
    public void Corpus_Files_Decode_Through_Their_Paired_Modes()
    {
        string? dir = Environment.GetEnvironmentVariable("NINO_CORPUS_DIR");
        Assert.SkipWhen(dir is null, "set NINO_CORPUS_DIR to run the corpus QC");
        int expected = int.TryParse(
            Environment.GetEnvironmentVariable("NINO_CORPUS_FRAMES"), out int e) ? e : 3;

        // MTP does not relay test stdout — the per-file verdicts go to a report file
        // (the MS110D_MASK_LOG pattern).
        using StreamWriter? report =
            Environment.GetEnvironmentVariable("NINO_CORPUS_REPORT") is { } rp
                ? new StreamWriter(rp) : null;

        var failures = new List<string>();
        foreach (string path in Directory.GetFiles(dir!, "*.wav").OrderBy(p => p))
        {
            string name = Path.GetFileName(path);
            string dip = name.Split('-')[0];
            if (!DipToMode.TryGetValue(dip, out string? mode))
            {
                failures.Add($"{name}: no catalog pairing for DIP {dip}");
                continue;
            }

            int rate = ModemCatalog.DspRateFor(mode);
            var (raw, wavRate) = WavFile.ReadMono(path);
            float[] samples;
            int produced;
            if (wavRate == rate)
            {
                samples = raw;
                produced = raw.Length;
            }
            else
            {
                var decimator = new Decimator(wavRate, wavRate / rate);
                samples = new float[decimator.MaxOutput(raw.Length)];
                produced = decimator.Process(raw, samples);
            }

            var padded = new float[produced + rate];
            Array.Copy(samples, padded, produced);

            var frames = new List<byte[]>();
            ModemOptions options = Environment.GetEnvironmentVariable("NINO_CORPUS_DETECTOR") switch
            {
                "differential" => new ModemOptions(Detector: PskDetector.Differential),
                "coherent" => new ModemOptions(Detector: PskDetector.Coherent),
                _ => default,
            };
            IModem modem = ModemCatalog.Create(mode, rate, frames.Add, options);
            modem.Process(padded);

            string verdict = frames.Count == expected ? "OK " : "QC-FAIL";
            string sizes = string.Join(",", frames.Select(f => f.Length));
            report?.WriteLine($"{verdict} {name,-42} {mode,-16} frames {frames.Count}/{expected} sizes [{sizes}]");
            if (frames.Count != expected)
            {
                failures.Add($"{name}: {mode} decoded {frames.Count}/{expected}");
            }
        }

        report?.Flush();

        failures.Should().BeEmpty("every corpus capture must decode its expected frame count");
    }
}
