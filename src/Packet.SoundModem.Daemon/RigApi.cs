using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// <c>/api/rig</c> and <c>/api/rig/tune</c>: the rig's state, and a tuning window that is always
/// put back.
/// </summary>
/// <remarks>
/// The request handling without the HTTP, as <see cref="TxLeaseApi"/> is: <see cref="ConfigApi"/>
/// reads the body and writes the answer. Every window opened here belongs to one owner,
/// <see cref="Owner"/>, so a second POST renews or retunes the same window, and a window opened
/// by something inside the station is refused rather than taken over.
/// </remarks>
internal static class RigApi
{
    /// <summary>The owner every window opened over the API is held by.</summary>
    internal const string Owner = "the API";

    /// <summary>How long a window lasts when the request does not say.</summary>
    internal const double DefaultSeconds = 60;

    /// <summary>What a malformed request is told about the shape of a good one.</summary>
    internal const string Usage =
        "POST {\"dialHz\": 7052000, \"mode\": \"USB\", \"seconds\": 60} to tune the rig there for that "
        + "long (at most 300 s at a time, optionally with \"passbandHz\", 0 for the rig's normal width) "
        + "and put it back afterwards; POST again before it ends to renew it; {\"release\": true} to put "
        + "it back now. GET /api/rig to read the rig";

    /// <summary>Answers one request.</summary>
    /// <param name="rig">The station's rig.</param>
    /// <param name="path"><c>/api/rig</c> or <c>/api/rig/tune</c>.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="body">The request body, possibly empty.</param>
    internal static (int Status, JsonObject Answer) Handle(
        RigControl rig, string path, string method, string body)
    {
        if (path == "/api/rig")
        {
            return method == "GET"
                ? (200, Describe(rig))
                : (405, Error("GET to read the rig; POST to /api/rig/tune to tune it for a while"));
        }

        if (method == "GET")
        {
            return (200, Describe(rig));
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

        bool release;
        double? dialHz;
        string? mode;
        double seconds;
        int passbandHz;
        try
        {
            release = asked?["release"]?.GetValue<bool>() == true;
            dialHz = asked?["dialHz"]?.GetValue<double>();
            mode = asked?["mode"]?.GetValue<string>();
            seconds = asked?["seconds"]?.GetValue<double>() ?? DefaultSeconds;
            passbandHz = asked?["passbandHz"]?.GetValue<int>() ?? 0;
        }
        catch (Exception wrongType) when (wrongType is InvalidOperationException or FormatException)
        {
            return (400, Error(
                "\"dialHz\" and \"seconds\" are numbers, \"passbandHz\" a whole number, \"mode\" a string "
                + $"and \"release\" true or false: {wrongType.Message}"));
        }

        if (release)
        {
            bool released = rig.Release(Owner);
            JsonObject answer = Describe(rig);
            answer["released"] = released;
            return (200, answer);
        }

        if (dialHz is not double dial || mode is null)
        {
            return (400, Error(Usage));
        }

        if (!double.IsFinite(dial) || dial != Math.Floor(dial))
        {
            return (400, Error("\"dialHz\" is the dial frequency in whole hertz, as 7052000"));
        }

        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return (400, Error("\"seconds\" must be more than 0"));
        }

        RigTuneResult result = rig.Tune(
            new RigTuning((long)dial, mode, passbandHz), TimeSpan.FromSeconds(seconds), Owner);
        switch (result.Outcome)
        {
            case RigTuneOutcome.Invalid:
                return (400, Error(result.Why!));

            case RigTuneOutcome.Refused:
            {
                JsonObject refused = Describe(rig);
                refused["refused"] = result.Why;
                return (409, refused);
            }

            case RigTuneOutcome.Failed:
            {
                JsonObject failed = Describe(rig);
                failed["failed"] = result.Why;
                return (500, failed);
            }

            default:
            {
                JsonObject tuned = Describe(rig);
                tuned["renewed"] = result.Outcome == RigTuneOutcome.Renewed;
                tuned["seconds"] = Math.Min(seconds, RigControl.MaxWindow.TotalSeconds);
                tuned["capped"] = result.Capped;
                return (200, tuned);
            }
        }
    }

    /// <summary>The rig as it stands.</summary>
    internal static JsonObject Describe(RigControl rig)
    {
        RigState state = rig.Snapshot();
        return new JsonObject
        {
            ["rigctld"] = state.Endpoint.ToString(),
            ["connected"] = state.Connected,
            ["dialHz"] = state.Tuning?.DialHz,
            ["mode"] = state.Tuning?.Mode,
            ["passbandHz"] = state.Tuning?.PassbandHz,
            ["keyed"] = state.Keyed,
            ["pttThroughRig"] = state.KeysThroughRig,
            ["transmitHeld"] = state.Window is not null || state.RestoreOwed is not null,
            ["window"] = state.Window is { } window
                ? new JsonObject
                {
                    ["owner"] = window.Owner,
                    ["dialHz"] = window.Tuning.DialHz,
                    ["mode"] = window.Tuning.Mode,
                    ["passbandHz"] = window.Tuning.PassbandHz,
                    ["expires"] = TxLeaseApi.Utc(window.Expires),
                    ["restoreTo"] = Tuning(window.RestoreTo),
                }
                : null,
            ["restoreOwed"] = state.RestoreOwed is { } owed ? Tuning(owed) : null,
            ["problem"] = state.LastProblem,
        };
    }

    private static JsonObject Tuning(RigTuning tuning) => new()
    {
        ["dialHz"] = tuning.DialHz,
        ["mode"] = tuning.Mode,
        ["passbandHz"] = tuning.PassbandHz,
    };

    private static JsonObject Error(string why) => new() { ["refused"] = why };
}
