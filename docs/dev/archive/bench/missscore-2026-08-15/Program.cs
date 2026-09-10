using System.Text.Json;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

// missscore <misses-v2-dir> [--workers N]
//
// Runs every cut in the corpus through a fresh deployed-configuration bpsk300 bank
// (12 kHz, IL2P+CRC, 2150 Hz centre, 4 offset pairs, differential) and reports, per cut:
// decode outcome, whether the expected payload (where the manifest attaches one) was
// produced, and the quality flags of the best decode. One modem per cut - this is the
// isolated-decode instrument, the aspiration-test pipeline pointed at misses-v2.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: missscore <misses-v2-dir> [--workers N]");
    return 2;
}

string dir = args[0];
int workers = 4;
int w = Array.IndexOf(args, "--workers");
if (w >= 0 && w + 1 < args.Length)
{
    workers = int.Parse(args[w + 1]);
}

var manifest = JsonSerializer.Deserialize<List<Entry>>(
    File.ReadAllText(Path.Combine(dir, "manifest.json")),
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true })!;

var results = new Result[manifest.Count];
Parallel.For(0, manifest.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
{
    Entry entry = manifest[i];
    (float[] audio, int rate) = WavFile.ReadMono(Path.Combine(dir, entry.Wav));
    Array.Resize(ref audio, audio.Length + rate / 2); // flush tail

    var decodes = new List<(string Hex, FrameQuality Quality)>();
    var modem = new BpskMultiModem(rate, static _ => { }, crc: true,
        centreFrequency: 2150, baud: 300, offsetPairs: 4, offsetHz: null,
        detector: PskDetector.Differential);
    modem.FrameDecoded += (frame, quality) => decodes.Add((Convert.ToHexString(frame), quality));

    int block = rate / 10;
    for (int pos = 0; pos < audio.Length; pos += block)
    {
        modem.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
    }

    bool expectedCopied = entry.ExpectedHex is not null
        && decodes.Any(d => d.Hex == entry.ExpectedHex);
    var best = decodes
        .OrderByDescending(d => d.Quality.CrcValid == true ? 2 : d.Quality.PlainIl2p ? 0 : 1)
        .Select(d => d.Quality)
        .Cast<FrameQuality?>()
        .FirstOrDefault();
    int? chased = best?.ChasedBits;
    string? bestHex = decodes
        .OrderByDescending(d => d.Quality.CrcValid == true ? 2 : d.Quality.PlainIl2p ? 0 : 1)
        .Select(d => d.Hex)
        .FirstOrDefault();
    results[i] = new Result(
        entry.Wav, entry.Class, entry.ExpectedHex is not null, decodes.Count, expectedCopied,
        best?.CrcValid, best?.TrailerNearBits, best?.ErasedBytes, chased, bestHex);
});

Console.WriteLine("wav,class,has_expected,decodes,expected_copied,crc_valid,trailer_near,erased,chased,addresses,best_hex");
foreach (Result r in results)
{
    string addresses = "";
    if (r.BestHex is not null)
    {
        byte[] frame = Convert.FromHexString(r.BestHex);
        addresses = Ax25AddressParser.TryParse(frame, out string src, out string dst)
            ? $"{src}>{dst}" : "(no ax25 header)";
    }

    Console.WriteLine($"{r.Wav},{r.Cls},{(r.HasExpected ? 1 : 0)},{r.Decodes},"
        + $"{(r.ExpectedCopied ? 1 : 0)},{r.CrcValid},{r.TrailerNear},{r.Erased},{r.Chased},"
        + $"{addresses},{r.BestHex}");
}

int anyDecode = results.Count(r => r.Decodes > 0);
int withExpected = results.Count(r => r.HasExpected);
int copied = results.Count(r => r.ExpectedCopied);
int chasedFrames = results.Count(r => r.Chased > 0);
Console.Error.WriteLine(
    $"== {results.Length} cuts: {anyDecode} produced any decode; "
    + $"{copied}/{withExpected} expected-attached copied byte-exact; "
    + $"{chasedFrames} leaned on chase ==");
return 0;

internal sealed record Entry(string Wav, string Class, string? ExpectedHex)
{
    public string Cls => Class;
}

internal sealed record Result(
    string Wav, string Cls, bool HasExpected, int Decodes, bool ExpectedCopied,
    bool? CrcValid, int? TrailerNear, int? Erased, int? Chased, string? BestHex);
