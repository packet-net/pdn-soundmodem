using System.Diagnostics;
using Packet.SoundModem.Pocsag;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// Cross-validation against multimon-ng, the de-facto POCSAG receiver: our transmission,
/// rendered at multimon's native 22050 Hz and piped in as raw samples, must decode
/// exactly — address, function bits and text, at all three standard rates. Skips when
/// multimon-ng is not installed (<c>apt-get install multimon-ng</c>).
/// </summary>
/// <remarks>
/// Polarity: multimon-ng expects the spec convention ('0' bit = high frequency = positive
/// discriminator sample) — our <see cref="PocsagPolarity.Normal"/> decodes with no flags,
/// and the 1.3.0 build shipped by Debian/Ubuntu has no polarity auto-detection, so an
/// <see cref="PocsagPolarity.Inverted"/> transmission needs its <c>-i</c> switch: both
/// facts are asserted below. Trailing content padding (zero bits for alphanumeric, 0xC
/// spaces for numeric — the DAPNET convention) appears in multimon's output as
/// <c>&lt;NUL&gt;</c> tokens / spaces and is stripped before comparing.
/// </remarks>
public class PocsagMultimonTests
{
    internal static bool MultimonMissing { get; } = !File.Exists("/usr/bin/multimon-ng")
        && Environment.GetEnvironmentVariable("PATH")!
            .Split(':').All(dir => !File.Exists(Path.Combine(dir, "multimon-ng")));

    /// <summary>Decodes 22050 Hz audio with multimon-ng and returns its output lines,
    /// trailing-padding tokens stripped — shared with the paging endpoint tests.</summary>
    internal static string[] RunMultimonRaw(float[] audio, string demod, params string[] extraArgs) =>
        [.. RunMultimon(audio, demod, extraArgs).Select(Strip)];

    private static string[] RunMultimon(float[] audio, string demod, params string[] extraArgs)
    {
        // multimon-ng raw input: mono signed 16-bit machine-endian at 22050 Hz.
        string raw = Path.Combine(Path.GetTempPath(), $"pocsag-mm-{Guid.NewGuid():N}.raw");
        try
        {
            var bytes = new byte[audio.Length * 2];
            for (int i = 0; i < audio.Length; i++)
            {
                short value = (short)Math.Round(Math.Clamp(audio[i], -1f, 1f) * 32767f);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), value);
            }

            File.WriteAllBytes(raw, bytes);

            var info = new ProcessStartInfo("multimon-ng")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "-q", "-c", "-t", "raw", "-a", demod }.Concat(extraArgs).Append(raw))
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            string stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        finally
        {
            File.Delete(raw);
        }
    }

    private static string Strip(string line)
    {
        while (line.EndsWith("<NUL>", StringComparison.Ordinal))
        {
            line = line[..^5];
        }

        return line.TrimEnd();
    }

    [SkippableFact]
    public void Multimon_Decodes_Every_Page_Of_A_Mixed_1200_Bd_Transmission()
    {
        Skip.If(MultimonMissing, "multimon-ng is not installed");

        var messages = new List<PocsagMessage>
        {
            PocsagMessage.Alphanumeric(133703, "Hello DAPNET interop"),
            PocsagMessage.Numeric(8, "0123456789-U.[]"),
            PocsagMessage.Alphanumeric(2007287, "Frame seven, function two", function: 2),
            PocsagMessage.Numeric(2097151, "999 111"),
            PocsagMessage.Tone(21, function: 1),
        };
        float[] audio = new PocsagEncoder(22050).Modulate(messages);

        string[] lines = RunMultimon(audio, "POCSAG1200").Select(Strip).ToArray();

        lines.Should().HaveCount(5, "every page must decode, exactly once");
        lines[0].Should().Be("POCSAG1200: Address:  133703  Function: 3  Alpha:   Hello DAPNET interop");
        lines[1].Should().Be("POCSAG1200: Address:       8  Function: 0  Numeric: 0123456789-U.[]");
        lines[2].Should().Be("POCSAG1200: Address: 2007287  Function: 2  Alpha:   Frame seven, function two");
        lines[3].Should().Be("POCSAG1200: Address: 2097151  Function: 0  Numeric: 999 111");
        lines[4].Should().Be("POCSAG1200: Address:      21  Function: 1");
    }

    [SkippableTheory]
    [InlineData(512, "POCSAG512")]
    [InlineData(2400, "POCSAG2400")]
    public void Multimon_Decodes_The_Other_Standard_Rates(int baud, string demod)
    {
        Skip.If(MultimonMissing, "multimon-ng is not installed");

        var messages = new List<PocsagMessage>
        {
            PocsagMessage.Alphanumeric(133703, $"pocsag{baud} leg"),
            PocsagMessage.Numeric(42, "8675309"),
        };
        float[] audio = new PocsagEncoder(22050, baud).Modulate(messages);

        string[] lines = RunMultimon(audio, demod).Select(Strip).ToArray();

        lines.Should().HaveCount(2);
        lines[0].Should().EndWith($"Function: 3  Alpha:   pocsag{baud} leg")
            .And.Contain("133703");
        lines[1].Should().EndWith("Function: 0  Numeric: 8675309")
            .And.Contain("42");
    }

    [SkippableFact]
    public void Normal_Polarity_Is_What_Multimon_Expects_And_Inverted_Needs_Its_Invert_Switch()
    {
        Skip.If(MultimonMissing, "multimon-ng is not installed");

        var messages = new List<PocsagMessage> { PocsagMessage.Alphanumeric(1234, "polarity proof") };
        float[] normal = new PocsagEncoder(22050).Modulate(messages);
        float[] inverted = new PocsagEncoder(22050, polarity: PocsagPolarity.Inverted).Modulate(messages);

        RunMultimon(normal, "POCSAG1200").Should().HaveCount(1, "normal polarity needs no flags");
        RunMultimon(inverted, "POCSAG1200").Should().BeEmpty(
            "multimon-ng 1.3.0 does not auto-detect polarity");
        RunMultimon(inverted, "POCSAG1200", "-i").Select(Strip).Should().ContainSingle()
            .Which.Should().EndWith("Alpha:   polarity proof");
    }
}
