using System.Text.Json;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Aspiration scoreboard of real off-air frames we do not yet copy. Each case is a BPSK300
/// IL2P+CRC frame the GB7RDG slot-3 NinoTNC decoded during the 2026-07-18/19 40 m benchmark that
/// our differential frequency-diversity bank missed — and that still fails to decode from its own
/// extracted audio, so it is genuinely hard rather than merely lost in the continuous 15-min
/// stream (see the honest-split note below). The corpus is 12 kHz mono snippets in
/// <c>samples/offair/misses-24h/</c>; <c>manifest.json</c> carries the expected AX.25 frame bytes.
/// </summary>
/// <remarks>
/// Provenance and methodology — including why only 37 of the day's 74 misses live here and where
/// the other 37 (frames that decode fine standalone but were dropped mid-stream) are tracked — are
/// in <c>samples/offair/misses-24h/README.md</c> and <c>docs/ninotnc-24h-continuous-losses.md</c>.
/// Category <c>Aspiration</c>: excluded from the blocking run, executed as a non-blocking scoreboard
/// (see <see cref="NinoTncAspirationTests"/>). When one starts copying, graduate it into
/// <see cref="NinoTncParityTests"/> and delete its manifest row — do not loosen the assertion to
/// make it pass.
/// </remarks>
[Trait("Category", "Aspiration")]
public class NinoTncMissCorpusAspirationTests
{
    private const int DspRate = 12000;

    public static IEnumerable<object[]> Misses()
    {
        foreach (MissCase c in LoadManifest())
        {
            yield return new object[] { c.Wav, c.Hex, $"{c.From}>{c.To} {c.Iso}" };
        }
    }

    [Theory]
    [MemberData(nameof(Misses))]
    public void Missed_Frame_Should_Copy(string wav, string expectedHex, string label)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("NINOTNC_ASPIRATION") != "1",
            "set NINOTNC_ASPIRATION=1 for the non-blocking aspiration scoreboard");

        float[] audio = Load(Path.Combine(CorpusDir(), wav));

        var frames = new List<byte[]>();
        var modem = new BpskMultiModem(DspRate, static _ => { }, crc: true,
            centreFrequency: 1500, baud: 300, offsetPairs: 4, offsetHz: null,
            detector: PskDetector.Differential);
        modem.FrameDecoded += (frame, _) => frames.Add(frame);

        int block = DspRate / 10;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            modem.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
        }

        frames.Should().Contain(f => Convert.ToHexString(f) == expectedHex,
            "the NinoTNC copied {0} off-air; our differential bank should too", label);
    }

    // Mirrors nino-compare's LoadDecimated for a channel-rate WAV: the snippets are already at the
    // 12 kHz DSP rate, so there is no decimation — just the half-second flush tail that drains the
    // final frame. Decoding a snippet here is byte-for-byte what `nino-compare decode` produced.
    private static float[] Load(string path)
    {
        var (samples, rate) = WavFile.ReadMono(path);
        if (rate != DspRate)
        {
            throw new InvalidOperationException($"corpus WAV {path} is {rate} Hz, expected {DspRate}");
        }

        Array.Resize(ref samples, samples.Length + DspRate / 2);
        return samples;
    }

    private static string CorpusDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "samples", "offair", "misses-24h");
    }

    private static IReadOnlyList<MissCase> LoadManifest()
    {
        string path = Path.Combine(CorpusDir(), "manifest.json");
        if (!File.Exists(path))
        {
            return Array.Empty<MissCase>();
        }

        return JsonSerializer.Deserialize<MissCase[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<MissCase>();
    }

    public sealed record MissCase(string Wav, string Hex, string From, string To, string Iso, string Cls);
}
