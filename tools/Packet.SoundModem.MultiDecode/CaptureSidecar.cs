using System.Text.Json;

namespace Packet.SoundModem.MultiDecode;

/// <summary>What a signal survey wrote down beside a capture, as far as this tool cares.</summary>
/// <param name="Path">The sidecar's own path, for saying where a number came from.</param>
/// <param name="CentreHz">The measured audio centre, or null if the file did not carry one.</param>
/// <param name="Verdict">Why the survey kept the capture, for the report line.</param>
/// <param name="WidthHz">The measured occupied width, same.</param>
/// <param name="Problem">Why the sidecar could not be used, when it could not be. A survey
/// capture whose sidecar is unreadable is worth a line rather than a silence: the tool's whole
/// failure mode is looking in the wrong place and saying nothing.</param>
internal sealed record Sidecar(
    string Path, double? CentreHz, string? Verdict, double? WidthHz, string? Problem);

/// <summary>
/// Reads the JSON sidecar the signal survey writes beside every capture
/// (<c>Packet.SoundModem.Survey.BurstCapture</c>).
/// </summary>
/// <remarks>
/// <para>
/// A survey capture is, by definition, a signal that was not on any modem's centre frequency -
/// that is what "unclaimed" means. Sweeping every mode at its catalogue centre over one is
/// therefore the one case this tool is guaranteed to get wrong, and it did: the 2026-08-24
/// capture in <c>samples/offair/</c> holds a plain-AX.25 <c>afsk300</c> beacon at 1120 Hz that
/// forty-six modes all missed, because <c>afsk300</c>'s catalogue centre is 1700.
/// </para>
/// <para>
/// The survey already measured where the signal was and wrote it down. Reading it costs nothing
/// and removes the commonest reason this tool comes back empty, so it is done by default rather
/// than behind a flag - <c>--centre</c> overrides it, for a capture from anything else.
/// </para>
/// <para>
/// Deliberately tolerant: any unexpected shape gives a <see cref="Sidecar.Problem"/> and the
/// sweep carries on at the catalogue centres. A file beside a WAV is not a contract.
/// </para>
/// </remarks>
internal static class CaptureSidecar
{
    /// <summary>Reads the sidecar beside <paramref name="wavPath"/>, or null if there is none.</summary>
    public static Sidecar? Beside(string wavPath)
    {
        string path = Path.ChangeExtension(wavPath, ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Sidecar(path, null, null, null, "not a JSON object");
            }

            double? centre = Number(document.RootElement, "audioCentreHz");
            return new Sidecar(
                path,
                centre,
                Text(document.RootElement, "verdict"),
                Number(document.RootElement, "widthHz"),
                centre is null ? "no audioCentreHz in it" : null);
        }
        catch (Exception error) when (error is JsonException or IOException
                                          or UnauthorizedAccessException)
        {
            return new Sidecar(path, null, null, null, error.Message);
        }
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement found)
        && found.ValueKind == JsonValueKind.Number
        && found.TryGetDouble(out double value)
            ? value
            : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;
}
