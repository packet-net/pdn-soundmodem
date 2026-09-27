using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Packet.SoundModem.Linux;

/// <summary>A card's two PipeWire nodes: where to capture from and play to through PipeWire.</summary>
/// <param name="Source">The capture node's <c>node.name</c>, if PipeWire has one for the card.</param>
/// <param name="Sink">The playback node's <c>node.name</c>, if PipeWire has one.</param>
public sealed record PipeWireNodes(string? Source, string? Sink)
{
    /// <summary>The ALSA device string that captures from <see cref="Source"/> through PipeWire's
    /// ALSA plugin (the <c>pipewire-alsa</c> package), or null without a source.</summary>
    public string? CapturePcm => Source is null ? null : PipeWire.Pcm(Source);

    /// <summary>The ALSA device string that plays to <see cref="Sink"/> through PipeWire.</summary>
    public string? PlaybackPcm => Sink is null ? null : PipeWire.Pcm(Sink);
}

/// <summary>
/// The way round a card the desktop's sound server will not let go of: go through PipeWire
/// rather than around it.
/// </summary>
/// <remarks>
/// <para>The first choice is always the card direct (<c>plughw:</c>), with the sound server told
/// to leave radio interfaces alone (the WirePlumber rule the packages ship): nothing between the
/// modem and the converter, and the 0 dB rules applied where the gain is. But a desktop that has
/// the card open, with no rule installed, answers the direct open with EBUSY, and a station that
/// then refuses to start is worse than one that runs through PipeWire and says so.</para>
/// <para>Through PipeWire the card still runs at its own rate and the hardware mixer is still the
/// one the level rules act on; what is added is PipeWire's own stream volume, which it holds at
/// 100% (0 dB) unless someone moves it in the desktop's sound settings.</para>
/// </remarks>
public static class PipeWire
{
    /// <summary>How long <c>pw-dump</c> may take before the answer is "no PipeWire".</summary>
    private static readonly TimeSpan DumpTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// PipeWire's nodes for card <paramref name="cardNumber"/>, or null when PipeWire is not
    /// running here (or <c>pw-dump</c> is not installed).
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static PipeWireNodes? NodesFor(int cardNumber) =>
        Dump() is { } json ? Parse(json, cardNumber) : null;

    /// <summary>The ALSA device string for a PipeWire node: <c>pipewire:NODE=name</c>.</summary>
    public static string Pcm(string node) => $"pipewire:NODE={node}";

    /// <summary>
    /// The nodes in <c>pw-dump</c>'s JSON whose ALSA card is <paramref name="cardNumber"/>: an
    /// <c>Audio/Source</c> and an <c>Audio/Sink</c>. PipeWire spells the card as <c>alsa.card</c>
    /// on some versions and <c>api.alsa.pcm.card</c> on others, as a string or a number.
    /// </summary>
    internal static PipeWireNodes Parse(string json, int cardNumber)
    {
        string? source = null;
        string? sink = null;
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new PipeWireNodes(null, null);
        }

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement type) || type.GetString() != "PipeWire:Interface:Node"
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if ((Card(props, "alsa.card") ?? Card(props, "api.alsa.pcm.card")) != cardNumber
                || !props.TryGetProperty("node.name", out JsonElement name) || name.GetString() is not { } node)
            {
                continue;
            }

            string? mediaClass = props.TryGetProperty("media.class", out JsonElement c) ? c.GetString() : null;
            if (mediaClass == "Audio/Source")
            {
                source ??= node;
            }
            else if (mediaClass == "Audio/Sink")
            {
                sink ??= node;
            }
        }

        return new PipeWireNodes(source, sink);
    }

    private static int? Card(JsonElement props, string key)
    {
        if (!props.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) => number,
            _ => null,
        };
    }

    [SupportedOSPlatform("linux")]
    private static string? Dump()
    {
        try
        {
            var start = new ProcessStartInfo("pw-dump")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(DumpTimeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not installed, which is the answer "no PipeWire here".
            return null;
        }
    }
}
