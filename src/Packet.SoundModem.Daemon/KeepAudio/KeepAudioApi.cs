using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// <c>/api/keepaudio</c>: keep the receive audio from one time to another, with a label, and say
/// what has been kept.
/// </summary>
/// <remarks>
/// The request handling without the HTTP, as <see cref="TxLeaseApi"/> does it: <see cref="ConfigApi"/>
/// reads the body and writes the answer, and a test can reach everything in between.
/// </remarks>
internal static class KeepAudioApi
{
    /// <summary>What a wrong method or a malformed request is told.</summary>
    internal const string Usage =
        "POST {\"from\": \"2026-10-22T15:58:00Z\", \"to\": \"2026-10-22T16:01:30Z\", \"label\": \"slot 1600\"} "
        + "to keep the receive audio between those UTC times (at most 10 minutes, starting at most "
        + "5 minutes ago and ending at most 15 minutes ahead); GET to see what is held and kept";

    /// <summary>Answers one request.</summary>
    internal static (int Status, JsonObject Answer) Handle(KeepAudioRecorder recorder, string method, string body)
    {
        if (method == "GET")
        {
            return (200, Describe(recorder));
        }

        if (method != "POST")
        {
            return (405, Error(Usage));
        }

        JsonNode? asked;
        try
        {
            asked = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body);
        }
        catch (JsonException bad)
        {
            return (400, Error($"the body is not JSON: {bad.Message}"));
        }

        if (asked is not JsonObject request)
        {
            return (400, Error(Usage));
        }

        foreach (KeyValuePair<string, JsonNode?> field in request)
        {
            if (field.Key is not ("from" or "to" or "label"))
            {
                return (400, Error(
                    $"there is no field \"{TxTestRunner.Printable(field.Key)}\"; it takes \"from\", \"to\" and \"label\""));
            }
        }

        string? label;
        string? fromText;
        string? toText;
        try
        {
            label = request["label"]?.GetValue<string>();
            fromText = request["from"]?.GetValue<string>();
            toText = request["to"]?.GetValue<string>();
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            return (400, Error($"\"from\", \"to\" and \"label\" are strings: {wrongType.Message}"));
        }

        if (fromText is null || toText is null)
        {
            return (400, Error(Usage));
        }

        if (!TryTime(fromText, out DateTimeOffset from))
        {
            return (400, Error($"\"from\" is not a time: write it like 2026-10-22T15:58:00Z"));
        }

        if (!TryTime(toText, out DateTimeOffset to))
        {
            return (400, Error($"\"to\" is not a time: write it like 2026-10-22T16:01:30Z"));
        }

        (KeptWindow? window, int status, string? refusal) = recorder.Keep(label, from, to);
        if (window is null)
        {
            return (status, Error(refusal ?? Usage));
        }

        return (200, new JsonObject
        {
            ["id"] = window.Id,
            ["label"] = window.Label,
            ["from"] = TxLeaseApi.Utc(window.From),
            ["to"] = TxLeaseApi.Utc(window.To),
            ["clamped"] = window.Note is not null,
            ["note"] = window.Note,
            ["readyAfter"] = TxLeaseApi.Utc(window.To + KeepAudioRecorder.SettleAfterEnd),
            ["directory"] = Path.Combine(recorder.Directory, window.Id),
        });
    }

    /// <summary>What is held in memory, what is waiting and what has been kept.</summary>
    internal static JsonObject Describe(KeepAudioRecorder recorder)
    {
        (DateTimeOffset? heldFrom, DateTimeOffset? heldTo, IReadOnlyList<KeptWindow> pending, IReadOnlyList<string> kept) =
            recorder.Status();
        var waiting = new JsonArray();
        foreach (KeptWindow w in pending)
        {
            waiting.Add(new JsonObject
            {
                ["id"] = w.Id,
                ["from"] = TxLeaseApi.Utc(w.From),
                ["to"] = TxLeaseApi.Utc(w.To),
                ["readyAfter"] = TxLeaseApi.Utc(w.To + KeepAudioRecorder.SettleAfterEnd),
            });
        }

        var done = new JsonArray();
        foreach (string id in kept)
        {
            done.Add(id);
        }

        return new JsonObject
        {
            ["heldFrom"] = heldFrom is { } f ? TxLeaseApi.Utc(f) : null,
            ["heldTo"] = heldTo is { } t ? TxLeaseApi.Utc(t) : null,
            ["sampleRate"] = recorder.SampleRate,
            ["directory"] = recorder.Directory,
            ["retentionDays"] = KeepAudioRecorder.Retention.TotalDays,
            ["pending"] = waiting,
            ["kept"] = done,
        };
    }

    /// <summary>An ISO 8601 time; one with no offset is taken as UTC.</summary>
    internal static bool TryTime(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);

    private static JsonObject Error(string why) => new() { ["refused"] = why };
}
