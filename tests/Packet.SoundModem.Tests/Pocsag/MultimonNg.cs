using System.Diagnostics;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// Cross-validation runner for multimon-ng, the de-facto POCSAG receiver: renders audio at
/// multimon's native 22050 Hz, pipes it in as raw samples, and returns the decoded lines
/// (trailing padding tokens stripped). Skips when multimon-ng is not installed
/// (<c>apt-get install multimon-ng</c>). The full multimon cross-validation of the codec
/// lives in the M0LTE.Pocsag package; this runner stays for the paging-endpoint tests.
/// </summary>
internal static class MultimonNg
{
    public static bool MultimonMissing { get; } = !File.Exists("/usr/bin/multimon-ng")
        && Environment.GetEnvironmentVariable("PATH")!
            .Split(':').All(dir => !File.Exists(Path.Combine(dir, "multimon-ng")));

    /// <summary>Decodes 22050 Hz audio with multimon-ng, trailing-padding tokens stripped.</summary>
    public static string[] RunMultimonRaw(float[] audio, string demod, params string[] extraArgs) =>
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
}
