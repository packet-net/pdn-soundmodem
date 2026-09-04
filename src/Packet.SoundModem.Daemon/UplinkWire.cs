using System.Globalization;
using System.Text.Json;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Reading what a station says about itself and about what it heard, at the point it enters this
/// process.
/// </summary>
/// <remarks>
/// <para>The normative wire format is section 4.2 of <c>docs/uplink-plan.md</c>; this is the
/// monitor's half of it and the field names below are the pinned ones. <b>Nothing a station sends
/// is forwarded.</b> Every message is parsed into typed fields here and the monitor re-serialises
/// its own, so there is no code path in which a station's bytes reach a browser unexamined and no
/// field it could add to a page's stream.</para>
/// <para><b>Length caps are enforced here, at the boundary, and over a cap is a refusal or a
/// dropped message rather than a truncation</b> - so nothing arrives on somebody's screen
/// half-said. The caps are uplink-plan 4.6's: callsign 16, operator 40, location 60, radio 60,
/// mode 24, a frame's why/il2p/hex 256, a status sentence 200, and a frame's own bytes 2048
/// decoded.</para>
/// </remarks>
internal static class UplinkWire
{
    private const int CallsignCap = 16;
    private const int OperatorCap = 40;
    private const int LocationCap = 60;
    private const int RadioCap = 60;
    private const int ModeCap = 24;
    private const int NoteCap = 256;
    private const int SiteCap = 200;

    /// <summary>
    /// Reads a station's <c>hello</c>, or says in one sentence why it will not be listed.
    /// </summary>
    /// <param name="root">The message.</param>
    /// <param name="entry">The configured station whose token opened this connection.</param>
    /// <param name="hello">What it said about itself, when this returns true.</param>
    /// <param name="why">The refusal, when it returns false. Written to be read by the operator
    /// of the station, whose console it will appear on.</param>
    internal static bool TryReadHello(
        JsonElement root, UplinkEntry entry, out UplinkHello? hello, out string? why)
    {
        hello = null;

        if (Int(root, "protocol") is not { } protocol)
        {
            why = "its hello carries no protocol version";
            return false;
        }

        if (protocol != UplinkServer.Protocol)
        {
            why = $"speaks uplink protocol {protocol} and this site speaks "
                + $"{UplinkServer.Protocol}";
            return false;
        }

        if (!TryCapped(root, "callsign", CallsignCap, out string? callsign)
            || callsign is not { Length: > 0 })
        {
            why = "its hello carries no callsign, or one longer than "
                + $"{CallsignCap} characters";
            return false;
        }

        // The callsign is bound to the token, and this is where that binding is enforced. It is
        // what stops one station claiming another's slug; the slug itself never comes off the
        // wire at all, so there is no way to ask for one.
        if (!string.Equals(callsign, entry.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            why = $"says it is {UberSdrDirectory.Ascii(callsign)} and this token was issued to "
                + $"{UberSdrDirectory.Ascii(entry.Callsign)}";
            return false;
        }

        if (Int(root, "audioRate") is not { } audioRate
            || audioRate is < UplinkServer.MinAudioRate or > UplinkServer.MaxAudioRate)
        {
            why = $"declares an audio rate outside {UplinkServer.MinAudioRate} to "
                + $"{UplinkServer.MaxAudioRate} Hz";
            return false;
        }

        // At most a second of audio in one message, so that the length check on every binary
        // message that follows is a small number rather than something a station could make
        // enormous by declaring it.
        if (Int(root, "blockSamples") is not { } blockSamples
            || blockSamples < 1 || blockSamples > audioRate)
        {
            why = $"declares an audio block of {Int(root, "blockSamples")?.ToString(
                CultureInfo.InvariantCulture) ?? "no"} samples, which cannot be right at "
                + $"{audioRate} Hz";
            return false;
        }

        if (!TryCapped(root, "operator", OperatorCap, out string? op)
            || !TryCapped(root, "location", LocationCap, out string? location)
            || !TryCapped(root, "radio", RadioCap, out string? radio)
            || !TryCapped(root, "site", SiteCap, out string? site))
        {
            why = "sent a longer operator, location, radio or site than this site accepts "
                + $"({OperatorCap}, {LocationCap}, {RadioCap} and {SiteCap} characters)";
            return false;
        }

        if (!TryReadBands(root, audioRate, out List<DeclaredBand>? bands, out string? bandProblem))
        {
            why = bandProblem;
            return false;
        }

        hello = new UplinkHello
        {
            Callsign = callsign,
            Operator = op,
            Location = location,
            Radio = radio,
            // The same check the directory's public_url goes through, at the same point: the
            // scheme is what matters and HTML escaping does not touch it, so a javascript: URL
            // would carry none of the four characters an escaper looks for and would run on this
            // site's origin in every visitor's session. Refused here rather than at each of the
            // places it is later rendered, because there is no bottom to the list of those.
            Site = UberSdrDirectory.HttpUrlOrNull(site),
            Daemon = Capped(root, "daemon", 40),
            AudioRate = audioRate,
            BlockSamples = blockSamples,
            DialHz = Double(root, "dialHz") is { } dial && dial >= 0 ? dial : 0,
            Sideband = Capped(root, "sideband", 3) is "lsb" ? "lsb" : "usb",
            Bands = bands!,
        };

        why = null;
        return true;
    }

    private static bool TryReadBands(
        JsonElement root, int audioRate, out List<DeclaredBand>? bands, out string? why)
    {
        bands = [];
        why = null;

        if (!root.TryGetProperty("bands", out JsonElement list))
        {
            return true;   // a station with nothing to draw is a station with an empty waterfall
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > UplinkServer.MaxBands)
        {
            why = $"sent no usable band list, or more than {UplinkServer.MaxBands} bands";
            bands = null;
            return false;
        }

        double nyquist = audioRate / 2.0;
        foreach (JsonElement band in list.EnumerateArray())
        {
            if (band.ValueKind != JsonValueKind.Object
                || Int(band, "sub") is not { } sub || sub is < 0 or > 15
                || Capped(band, "mode", ModeCap) is not { Length: > 0 } mode
                || Double(band, "lowHz") is not { } low
                || Double(band, "highHz") is not { } high
                || Double(band, "centreHz") is not { } centre
                || !double.IsFinite(low) || !double.IsFinite(high) || !double.IsFinite(centre)
                || low < 0 || high <= low || centre < low || centre > high)
            {
                why = "sent a band this site cannot draw";
                bands = null;
                return false;
            }

            // A band wider than the relayed audio is not a refusal: a 48 kHz station publishing
            // at 12 kHz has modems above the picture it is sending, which its own start-up told
            // it about, and drawing the part that fits is more use than refusing the lot.
            if (low >= nyquist)
            {
                continue;
            }

            double top = Math.Min(high, nyquist);
            bands.Add(new DeclaredBand(sub, mode, Math.Clamp(centre, low, top), top - low));
        }

        return true;
    }

    /// <summary>
    /// Reads a relayed frame, or returns null for one this site will not list.
    /// </summary>
    /// <remarks>
    /// Null is a dropped message rather than a closed connection: one frame that will not read is
    /// one row missing from a page, and hanging up over a field would cost the station its whole
    /// listing.
    /// </remarks>
    internal static RelayedFrame? ReadFrame(JsonElement root, DateTimeOffset now)
    {
        if (Int(root, "sub") is not { } sub || sub is < 0 or > 15
            || Capped(root, "mode", ModeCap) is not { Length: > 0 } mode)
        {
            return null;
        }

        byte[]? raw = null;
        if (root.TryGetProperty("raw", out JsonElement rawElement)
            && rawElement.ValueKind == JsonValueKind.String)
        {
            // Capped on the decoded length, which is the number that matters: base64 of 2 kB is
            // under 3 kB and the message cap would let a great deal more through.
            if (!rawElement.TryGetBytesFromBase64(out raw)
                || raw.Length > UplinkServer.MaxRawFrameBytes)
            {
                return null;
            }
        }

        return new RelayedFrame
        {
            SubChannel = sub,
            Mode = mode,
            From = Capped(root, "from", CallsignCap),
            To = Capped(root, "to", CallsignCap),
            LengthBytes = Math.Max(0, Int(root, "lenBytes") ?? raw?.Length ?? 0),
            SnrDb = Finite(root, "snrDb"),
            BurstLines = Int(root, "burstLines"),
            OffsetHz = Finite(root, "offsetHz"),
            CorrectedBytes = Int(root, "corrected"),
            CrcValid = Bool(root, "crc"),
            IdBeacon = Bool(root, "id") ?? false,
            Transmitted = Bool(root, "tx") ?? false,
            TransmitTrimHz = Finite(root, "txTrimHz"),
            Note = Capped(root, "why", NoteCap),
            HeaderType = Capped(root, "il2p", NoteCap),
            FrameHex = Capped(root, "hex", NoteCap),
            PlainIl2p = Bool(root, "plain") ?? false,
            MonitorOnly = Bool(root, "monitorOnly") ?? false,
            At = When(root, "at") ?? now,
            Raw = raw,
        };
    }

    /// <summary>
    /// A string property, or null where it is absent, is not a string, or is over the cap.
    /// </summary>
    /// <remarks>
    /// For the places where over-long is a dropped message. Where it has to be a refusal instead -
    /// the hello - use <see cref="TryCapped"/>, which tells the two apart.
    /// </remarks>
    internal static string? Capped(JsonElement root, string name, int cap) =>
        TryCapped(root, name, cap, out string? value) ? value : null;

    /// <summary>
    /// A string property, distinguishing "not there" (true, null) from "there and too long"
    /// (false).
    /// </summary>
    internal static bool TryCapped(JsonElement root, string name, int cap, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? text = element.GetString();
        if (text is null)
        {
            return true;
        }

        if (text.Length > cap)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out int value)
            ? value
            : null;

    private static double? Double(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out double value)
            ? value
            : null;

    /// <summary>A number that is a number: a NaN reaching the display would draw nothing at
    /// all, and a station is somebody else's software.</summary>
    private static double? Finite(JsonElement root, string name) =>
        Double(root, name) is { } value && double.IsFinite(value) ? value : null;

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
            ? element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static DateTimeOffset? When(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
        && element.TryGetDateTimeOffset(out DateTimeOffset value)
            ? value
            : null;
}

/// <summary>
/// What a station said about itself when it connected: who it is, what it is relaying, and what
/// to draw on its waterfall.
/// </summary>
/// <remarks>
/// Every string here is length-capped and every URL scheme-checked by the time this exists. The
/// slug is not among these fields and never will be: it comes from the monitor's own table.
/// </remarks>
internal sealed record UplinkHello
{
    /// <summary>Its callsign, which its token was issued against.</summary>
    public required string Callsign { get; init; }

    /// <summary>Whose station it is, where one was given.</summary>
    public string? Operator { get; init; }

    /// <summary>Where it is, where that was given.</summary>
    public string? Location { get; init; }

    /// <summary>What it is listening with, where that was given.</summary>
    public string? Radio { get; init; }

    /// <summary>Its own page, where one was given and is an absolute http or https URL.</summary>
    public string? Site { get; init; }

    /// <summary>The pdn-soundmodem version it is running, for the journal.</summary>
    public string? Daemon { get; init; }

    /// <summary>The rate it is relaying at, which is this station's channel rate here.</summary>
    public required int AudioRate { get; init; }

    /// <summary>Samples in one audio message, which is what every one is checked against.</summary>
    public required int BlockSamples { get; init; }

    /// <summary>Its dial, for the page's RF scale. 0 is "not set", as everywhere else.</summary>
    public double DialHz { get; init; }

    /// <summary>Which sideband it is on.</summary>
    public string Sideband { get; init; } = "usb";

    /// <summary>What its modems occupy, for the waterfall's overlay. It runs them, not us.</summary>
    public IReadOnlyList<DeclaredBand> Bands { get; init; } = [];
}
