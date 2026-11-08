using System.Text.Json.Nodes;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// What the configuration page calls each key, and where on the page it lives.
/// </summary>
/// <remarks>
/// <para><b>One entry per key, drift-tested.</b> The schema is read off the config classes; this
/// table is the part a person has to write: a plain label, a unit, an area and whether the key is
/// for specialists. <c>ConfigPageTests</c> fails when a key has no entry here and when an entry
/// names a key that is gone, so a new setting cannot reach the page unlabelled.</para>
/// <para><b>Keyed by class, not by path.</b> An entry is <c>ClassName.keyName</c> as the schema
/// names them. A class met in two places (<c>ModemConfig</c> under <c>modems</c> and under
/// <c>monitor.modems</c>) has one entry, and its area is the area of the top-level key it is
/// reached from, so only the <c>DaemonConfig</c> entries carry one.</para>
/// <para><b>Labels are what the key does, not what it is called.</b> Taken from
/// <c>docs/reference/config.md</c>, the reference the page also quotes for its help text. The key
/// name stays on the page as small print so the two can be matched up.</para>
/// <para><b>Shown when.</b> A section that cannot apply to this station is left off its page
/// until the file sets it: the Flex section on a station that is not on a Flex, the PTT pin on a
/// station that keys through a serial line. A rule is one of three plain tests, any of which is
/// enough: the <em>kind</em> of audio device (<see cref="DeviceKind"/>), another key being set, or
/// a sibling key having one of a few values. A key the file does set is always shown, whatever
/// the rule says, so nothing in a file is ever hidden from the person editing it.</para>
/// </remarks>
internal static class ConfigKeys
{
    /// <summary>The page's areas, in the order its menu lists them.</summary>
    internal static readonly (string Id, string Title)[] Areas =
    [
        ("radio", "Radio and audio"),
        ("keying", "Keying"),
        ("modes", "Modes and frequencies"),
        ("software", "Connect your software"),
        ("sharing", "Sharing and logging"),
        ("advanced", "Advanced"),
        // A monitor site's areas, shown in place of the station's when the file has a monitor
        // section. The first keeps the id "monitor" it has always had.
        ("monitor", "Receivers and directory"),
        ("monitor-modems", "Modems"),
        ("monitor-uplinks", "Private station uplinks"),
        ("monitor-site", "Site page"),
        ("monitor-logging", "Logging"),
    ];

    /// <summary>The kind a file with a <c>monitor</c> section is: a site, with no audio device.</summary>
    internal const string MonitorKind = "monitor";

    /// <summary>What the daemon knows about one kind of audio device, read from
    /// <see cref="Daemon.DeviceKinds"/> and not copied: whether it has a mixer, can transmit, takes a
    /// rig and may publish.</summary>
    internal sealed record KindFacts(string Name, bool HasMixer, bool Transmits, bool TakesRig, bool MayPublish);

    /// <summary>The kinds of audio device, as <c>device</c> spells them (the part before the colon;
    /// the sound card, which claims everything else, is <c>alsa</c>), each with the facts the rules
    /// need, and then the monitor, which has no device at all.</summary>
    internal static IReadOnlyList<KindFacts> Kinds { get; } =
    [
        .. Daemon.DeviceKinds.All.Select(k => new KindFacts(
            k.Name, k.HasMixer, k.TransmitTestRefusal is null,
            k.RigRefusal(k.Name) is null, k.PublishRefusal(k.Name) is null)),
        new KindFacts(MonitorKind, false, false, false, false),
    ];

    /// <summary>The names in <see cref="Kinds"/>, as the schema serves them.</summary>
    internal static string[] DeviceKinds => [.. Kinds.Select(k => k.Name)];

    /// <summary>When a key or section is worth drawing without the file having set it.</summary>
    /// <param name="DeviceKinds">Any of these kinds of audio device.</param>
    /// <param name="Set">Any of these keys (paths from the top of the file) is set.</param>
    /// <param name="SiblingKey">A key beside this one, whose value decides.</param>
    /// <param name="SiblingIn">The values of <paramref name="SiblingKey"/> that show this key.</param>
    /// <param name="SiblingDefault">What <paramref name="SiblingKey"/> is taken to be when it is not set.</param>
    internal sealed record ShownWhen(
        string[]? DeviceKinds = null, string[]? Set = null,
        string? SiblingKey = null, string[]? SiblingIn = null, string? SiblingDefault = null);

    /// <summary>One key's entry.</summary>
    /// <param name="Label">What the key does, in plain words and sentence case.</param>
    /// <param name="Unit">Hz, dB, s and so on, or null for a count, a name or a switch.</param>
    /// <param name="Area">Where it lives; only on a <c>DaemonConfig</c> key.</param>
    /// <param name="Advanced">Whether it sits behind "More" in its area.</param>
    /// <param name="When">When it is drawn if the file does not set it; null is always.</param>
    /// <param name="Item">What one entry of a list is called, singular.</param>
    /// <param name="ChoiceLabels">For a key that takes one of a few values, what each is called in
    /// a drop-down; the stored value is still the key's own.</param>
    internal sealed record Entry(
        string Label, string? Unit, string? Area, bool Advanced, ShownWhen? When, string? Item,
        (string Value, string Label)[]? ChoiceLabels = null);

    /// <summary>Every entry, by <c>ClassName.keyName</c>.</summary>
    internal static IReadOnlyDictionary<string, Entry> Table => Cached.Value;

    private static readonly Lazy<Dictionary<string, Entry>> Cached = new(Build);

    /// <summary>The entry for a key, or null when it has none (a test fails on that).</summary>
    internal static Entry? For(string type, string key) =>
        Table.TryGetValue($"{type}.{key}", out Entry? entry) ? entry : null;

    /// <summary>An entry as the schema serves it.</summary>
    internal static void AddTo(JsonObject key, Entry entry)
    {
        key["label"] = entry.Label;
        if (entry.Unit is not null)
        {
            key["unit"] = entry.Unit;
        }

        if (entry.Area is not null)
        {
            key["area"] = entry.Area;
        }

        if (entry.Advanced)
        {
            key["advanced"] = true;
        }

        if (entry.Item is not null)
        {
            key["item"] = entry.Item;
        }

        if (entry.ChoiceLabels is { } labels)
        {
            var map = new JsonObject();
            foreach ((string value, string label) in labels)
            {
                map[value] = label;
            }

            key["choiceLabels"] = map;
        }

        if (entry.When is { } when)
        {
            var rule = new JsonObject();
            if (when.DeviceKinds is not null)
            {
                rule["deviceKind"] = new JsonArray([.. when.DeviceKinds.Select(k => (JsonNode)k)]);
            }

            if (when.Set is not null)
            {
                rule["set"] = new JsonArray([.. when.Set.Select(k => (JsonNode)k)]);
            }

            if (when.SiblingKey is not null)
            {
                rule["sibling"] = new JsonObject
                {
                    ["key"] = when.SiblingKey,
                    ["in"] = new JsonArray([.. (when.SiblingIn ?? []).Select(k => (JsonNode)k)]),
                    ["default"] = when.SiblingDefault,
                };
            }

            key["when"] = rule;
        }
    }

    /// <summary>The areas as the schema serves them.</summary>
    internal static JsonArray AreasJson() =>
        [.. Areas.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["title"] = a.Title })];

    private static ShownWhen OnDevice(params string[] kinds) => new(DeviceKinds: kinds);

    /// <summary>The rule "on any kind of device the daemon says this about": a station's key that
    /// a monitor never reads is hidden on a monitor because no <see cref="Kinds"/> entry of its
    /// is among the kinds listed. Null, no rule, when every kind qualifies.</summary>
    private static ShownWhen? OnKinds(Func<KindFacts, bool> applies, params string[] also)
    {
        string[] kinds = [.. Kinds.Where(applies).Select(k => k.Name).Concat(also).Distinct()];
        return kinds.Length == Kinds.Count ? null : new ShownWhen(DeviceKinds: kinds);
    }

    /// <summary>As <see cref="OnKinds"/>, or the file setting one of <paramref name="set"/>.</summary>
    private static ShownWhen? OnKindsOrSet(Func<KindFacts, bool> applies, params string[] set)
    {
        ShownWhen? kinds = OnKinds(applies);
        return new ShownWhen(DeviceKinds: kinds?.DeviceKinds, Set: set);
    }

    /// <summary>A station key: shown on every kind of device but not on a monitor.</summary>
    private static readonly Func<KindFacts, bool> Station = k => k.Name != MonitorKind;

    private static readonly (string, string)[] Sidebands =
        [("usb", "Upper sideband (usb)"), ("lsb", "Lower sideband (lsb)"), ("fm", "FM")];

    private static Dictionary<string, Entry> Build()
    {
        var table = new Dictionary<string, Entry>(StringComparer.Ordinal);
        string type = "";

        void T(string name) => type = name;

        // K(key, label, unit, advanced, area, when, item): the arguments that matter by name.
        void K(string key, string label, string? unit = null, bool advanced = false,
               string? area = null, ShownWhen? when = null, string? item = null,
               (string, string)[]? choices = null) =>
            table.Add($"{type}.{key}", new Entry(label, unit, area, advanced, when, item, choices));

        // ------------------------------------------------------------ the file's top level
        T("DaemonConfig");
        K("device", "Audio device", area: "radio", when: OnKinds(Station));
        K("captureDevice", "Receive sound card", advanced: true, area: "radio", when: OnKinds(k => k.HasMixer));
        K("playbackDevice", "Transmit sound card", advanced: true, area: "radio", when: OnKinds(k => k.HasMixer));
        K("captureRate", "Sound card sample rate", "Hz", advanced: true, area: "radio", when: OnKinds(k => k.Name is "alsa" or "pipe"));
        K("kissPort", "KISS port", area: "software", when: OnKinds(Station));
        K("kissMaxFrameBytes", "Longest KISS frame", "bytes", advanced: true, area: "software", when: OnKinds(Station));
        K("bind", "Listen on address", area: "software");
        K("sideband", "Kind of radio", area: "radio", choices: Sidebands);
        K("dialFrequency", "Dial frequency", "Hz", area: "radio");
        K("modems", "Modems", area: "modes", item: "modem", when: OnKinds(Station));
        K("modemPlugins", "Extra modem assemblies", area: "advanced", item: "assembly");
        K("polyglot", "Polyglot KISS ports", advanced: true, area: "software", item: "port", when: OnKinds(Station));
        K("polyglotPort", "Polyglot KISS port, shorthand", advanced: true, area: "software", when: OnKinds(Station));
        K("ptt", "Push to talk", area: "keying", when: OnKinds(k => k.Transmits && k.TakesRig));
        K("rig", "Rig control through rigctld", area: "radio", when: OnKinds(k => k.TakesRig));
        K("mailcast", "Mailcast, retired", area: "advanced");
        K("carrierSense", "Carrier sense from the radio", area: "keying", when: OnKinds(k => k.Transmits));
        K("txTest", "Transmitter test", area: "advanced", when: OnKinds(k => k.Transmits));
        K("alsa", "Sound card mixer", area: "radio", when: OnKindsOrSet(k => k.HasMixer, "alsa"));
        K("paging", "Paging, POCSAG", area: "software", when: OnKinds(Station));
        K("ardop", "ARDOP, old style", advanced: true, area: "software", when: new ShownWhen(Set: ["ardop"]));
        K("s5066", "STANAG 5066 node", advanced: true, area: "software", when: OnKinds(k => k.Transmits));
        K("flex", "FlexRadio", area: "radio", when: OnDevice("flex"));
        K("ubersdr", "UberSDR receiver", area: "radio", when: OnDevice("ubersdr", MonitorKind));
        K("openwebrx", "OpenWebRX receiver", area: "radio", when: OnDevice("openwebrx"));
        K("iqCard", "IQ sound card", area: "radio", when: OnDevice("iqcard"));
        K("iqpipe", "IQ from a command", area: "radio", when: OnDevice("iqpipe"));
        K("rtltcp", "RTL-SDR through rtl_tcp", area: "radio", when: OnDevice("rtltcp"));
        K("sdrconnect", "SDRplay through SDRconnect", area: "radio", when: OnDevice("sdrconnect"));
        K("waterfall", "Station page", area: "sharing");
        K("monitor", "Monitor site", area: "monitor");
        K("publish", "Publish to a monitor site", area: "sharing", when: OnKinds(k => k.MayPublish));
        K("api", "Access key", area: "sharing");
        K("frameLog", "Log of heard frames", area: "sharing");
        K("survey", "Survey of unreadable bursts", area: "advanced", when: OnKinds(Station));
        K("metrics", "Metrics for Prometheus and Grafana", area: "sharing", when: OnKinds(Station));
        K("frequencyMatching", "Frequency matching", area: "advanced", when: OnKinds(Station));
        K("rawCapture", "Raw audio recording", area: "advanced", when: OnKinds(Station));
        K("deadFeed", "Dead audio watch", area: "advanced");
        K("idBeacons", "Hear NinoTNC idents", advanced: true, area: "modes");

        // ------------------------------------------------------------ modems
        T("ModemConfig");
        K("subChannel", "KISS sub-channel");
        K("mode", "Mode");
        K("frequency", "Audio frequency", "Hz");
        K("offsetPairs", "Decoder branches either side of the centre", advanced: true);
        K("offsetStepHz", "Gap between decoder branches", "Hz", advanced: true);
        K("acceptPlainIl2p", "Also pass IL2P frames with no CRC", advanced: true);
        K("port", "Own TCP port (on ARDOP, its command port)", advanced: true);
        K("rfFrequency", "Radio frequency", "Hz");
        K("transmitOnlyUnderLease", "Transmit only under a lease", advanced: true);
        K("bandwidth", "Bandwidth", "Hz", advanced: true);
        K("busyDetect", "Tell ARDOP when the channel is busy", advanced: true);
        K("identify", "Morse ident", advanced: true);
        K("maxBurstSeconds", "Longest MS110D burst", "s", advanced: true);
        K("burstGatherSeconds", "Wait to fill an MS110D burst", "s", advanced: true);
        K("txAmplitude", "MS110D transmit level", advanced: true);

        T("ModemPluginConfig");
        K("path", "Assembly file");

        T("PolyglotConfig");
        K("port", "KISS port");
        K("subChannels", "Modems it covers, by sub-channel");
        K("default", "Sub-channel for stations not heard lately");
        K("forgetAfterMinutes", "Remember a station for", "minutes", advanced: true);

        T("IdentifyConfig");
        K("callsign", "Callsign to send");
        K("intervalMinutes", "Send every", "minutes");
        K("wpm", "Sending speed", "wpm", advanced: true);
        K("toneHz", "Tone", "Hz", advanced: true);
        K("rfFrequency", "Radio frequency to send on", "Hz", advanced: true);
        K("includeMode", "Send the mode name after the callsign", advanced: true);
        K("amplitude", "Level", advanced: true);

        // ------------------------------------------------------------ keying and the rig
        T("PttConfig");
        K("type", "How the radio is keyed", choices:
        [("serial", "A serial port"), ("cm108", "The sound card's PTT pin (CM108)"), ("rigctld", "Hamlib's rigctld")]);
        K("device", "Device", when: new ShownWhen(SiblingKey: "type", SiblingIn: ["serial", "cm108"], SiblingDefault: "serial"));
        K("line", "Serial line", choices: [("rts", "RTS"), ("dtr", "DTR")], when: new ShownWhen(SiblingKey: "type", SiblingIn: ["serial"], SiblingDefault: "serial"));
        K("gpio", "CM108 pin", when: new ShownWhen(SiblingKey: "type", SiblingIn: ["cm108"], SiblingDefault: "serial"));

        T("RigConfig");
        K("rigctld", "Where rigctld listens");
        K("required", "Stop if rigctld does not answer", advanced: true);
        K("mode", "Rig mode", advanced: true);
        K("passbandHz", "Rig filter width", "Hz", advanced: true);

        T("CarrierSenseConfig");
        K("radio", "Radio driver");
        K("port", "Radio control port");
        K("baud", "Control port speed", "baud", advanced: true);
        K("busyAboveDbm", "Busy when the signal is above", "dBm");
        K("pollMilliseconds", "Signal reading interval", "ms", advanced: true);
        K("audioFallback", "Listen to the audio when there is no radio to ask", advanced: true);

        T("TxTestConfig");
        K("enabled", "Transmitter test available");
        K("seconds", "Test length", "s");
        K("maxSeconds", "Longest test allowed", "s");
        K("amplitude", "Test level");

        // ------------------------------------------------------------ the sound card
        T("AlsaConfig");
        K("mixer", "Mixer levels");

        T("AlsaMixerConfig");
        K("captureGainDb", "Capture gain", "dB");
        K("playbackDb", "Playback level", "dB");
        K("card", "Mixer card", advanced: true);
        K("stateFile", "File that remembers the levels", advanced: true);
        K("captureControls", "Names to look for the capture gain under", advanced: true);
        K("agcControls", "Names to look for the AGC switch under", advanced: true);
        K("micBoostControls", "Names to look for the mic boost under", advanced: true);
        K("playbackControls", "Names to look for the playback level under", advanced: true);

        // ------------------------------------------------------------ other services
        T("PagingConfig");
        K("port", "Paging port");
        K("baud", "Paging speed", "baud");
        K("invertPolarity", "Invert the transmit signal", advanced: true);

        T("ArdopConfig");
        K("port", "ARDOP command port");

        T("S5066Config");
        K("port", "SIS port");
        K("node", "Node settings, M0LTE.S5066 names");

        // ------------------------------------------------------------ radios and receivers
        T("FlexConfig");
        K("frequency", "Slice frequency", "MHz");
        K("antenna", "Antenna");
        K("mode", "Slice mode");
        K("daxChannel", "DAX channel");
        K("receiveOnly", "Receive only");
        K("txPowerWatts", "Transmit power", "W");
        K("transmitFilterHighHz", "Transmit filter upper edge", "Hz", advanced: true);
        K("stationName", "Name shown on the radio", advanced: true);
        K("arbitration", "Wait for a quiet radio before keying", advanced: true);

        T("UberSdrConfig");
        K("mode", "Stream type");
        K("password", "Password");
        K("ssbLowHz", "Filter lower edge", "Hz", advanced: true);
        K("ssbHighHz", "Filter upper edge", "Hz", advanced: true);
        K("startupGuardMs", "Audio dropped after connecting", "ms", advanced: true);
        K("gain", "Audio gain", "x", advanced: true);
        K("onDemand", "Connect only while the station page is open", when: OnDevice("ubersdr"));
        K("lingerSeconds", "Stay connected after the last viewer", "s", advanced: true, when: OnDevice("ubersdr"));

        T("OpenWebRxConfig");
        K("profile", "Receiver profile");
        K("ssbLowHz", "Filter lower edge", "Hz", advanced: true);
        K("ssbHighHz", "Filter upper edge", "Hz", advanced: true);
        K("fmHalfWidthHz", "FM filter, half width", "Hz", advanced: true);
        K("startupGuardMs", "Audio dropped after connecting", "ms", advanced: true);
        K("gain", "Audio gain", "x", advanced: true);

        T("IqCardConfig");
        K("centreFrequency", "Centre frequency", "Hz");
        K("centreOffsetHz", "Centre offset from the rig's dial", "Hz", advanced: true);
        K("channelOffsetHz", "Channel offset above the centre", "Hz", advanced: true);
        K("sampleRate", "Sample rate", "Hz");
        K("swapIq", "Swap I and Q");
        K("gainImbalanceDb", "Gain imbalance, Q against I", "dB", advanced: true);
        K("phaseImbalanceDegrees", "Phase error, Q against I", "degrees", advanced: true);
        K("ssbLowHz", "Filter lower edge", "Hz", advanced: true);
        K("ssbHighHz", "Filter upper edge", "Hz", advanced: true);
        K("gain", "Audio gain", "x", advanced: true);

        T("IqPipeConfig");
        K("command", "Command that makes the IQ");
        K("format", "Sample format", choices: [("cu8", "Unsigned 8-bit (rtl_sdr)"), ("cs8", "Signed 8-bit"), ("cs16", "Signed 16-bit"), ("cf32", "32-bit float")]);
        K("rate", "Sample rate", "samples/s");
        K("centreFrequency", "Centre frequency", "Hz");
        K("swapIq", "Swap I and Q");
        K("gain", "Audio gain", "x", advanced: true);

        T("RtlTcpConfig");
        K("readOnly", "Server takes no orders");
        K("centreFrequency", "Centre frequency", "Hz");
        K("sampleRate", "Sample rate", "samples/s");
        K("gainDb", "Tuner gain", "dB");
        K("ppm", "Frequency correction", "ppm", advanced: true);
        K("rtlAgc", "Use the dongle's own AGC", advanced: true);
        K("converterLoHz", "Converter oscillator", "Hz", advanced: true);
        K("swapIq", "Swap I and Q", advanced: true);
        K("lfoOffsetHz", "Converter oscillator, old name", "Hz", advanced: true);
        K("iqSwap", "Swap I and Q, old name", advanced: true);
        K("noDataSeconds", "Reconnect after silence of", "s", advanced: true);
        K("ssbLowHz", "Filter lower edge", "Hz", advanced: true);
        K("ssbHighHz", "Filter upper edge", "Hz", advanced: true);
        K("fmIfBandwidthHz", "FM filter width", "Hz", advanced: true);
        K("fmDeviationHz", "FM deviation at full scale", "Hz", advanced: true);
        K("gain", "Audio gain", "x", advanced: true);

        T("SdrConnectConfig");
        K("mode", "Stream type");
        K("sampleRate", "Sample rate", "samples/s");
        K("lnaState", "RF gain state");
        K("antenna", "Antenna");
        K("deviceSerial", "Radio serial number");
        K("deviceName", "Radio name");
        K("noDataSeconds", "Reconnect after silence of", "s", advanced: true);
        K("ssbLowHz", "Filter lower edge", "Hz", advanced: true);
        K("ssbHighHz", "Filter upper edge", "Hz", advanced: true);
        K("fmIfBandwidthHz", "FM filter width", "Hz", advanced: true);
        K("fmDeviationHz", "FM deviation at full scale", "Hz", advanced: true);
        K("gain", "Audio gain", "x", advanced: true);

        // ------------------------------------------------------------ sharing and logging
        T("WaterfallConfig");
        K("port", "Station page port");
        K("dialFrequencyHz", "Dial the page opens on", "Hz", advanced: true);
        K("sideband", "Sideband the page draws for", advanced: true, choices: Sidebands);
        K("linesPerSecond", "Waterfall speed", "lines/s", advanced: true);
        K("fftSize", "Spectrum detail", "points", advanced: true);
        K("public", "Public page for visitors", when: OnKinds(Station));
        K("enableAudioControls", "Level controls without a key", advanced: true, when: OnKinds(Station));
        K("title", "Page title");
        K("about", "About text for visitors");

        T("PublishConfig");
        K("url", "Monitor site address");
        K("token", "Access token from the site");
        K("callsign", "This station's callsign");
        K("operator", "Operator");
        K("location", "Location");
        K("radio", "Radio and antenna");
        K("site", "Your own web page");
        K("audioRate", "Published audio rate", "Hz", advanced: true);
        K("frames", "When to send heard frames", advanced: true, choices: [("always", "Always"), ("watched", "Only while someone is watching")]);

        T("ApiConfig");
        K("key", "Access key");

        T("FrameLogConfig");
        K("path", "Log file, or folder on a monitor");

        T("MetricsConfig");
        K("enabled", "Serve metrics");
        K("maxStations", "Most stations tracked", advanced: true);
        K("frameWindowSeconds", "Keep each frame in the feed for", "s", advanced: true);
        K("stationIdleHours", "Forget a quiet station after", "hours", advanced: true);

        // ------------------------------------------------------------ advanced
        T("SurveyConfig");
        K("path", "Folder for captures");
        K("maxBytes", "Space limit", "bytes");
        K("maxPerHour", "Most captures in an hour");
        K("cooldownSeconds", "Wait before capturing the same signal again", "s");
        K("marginSeconds", "Audio kept either side of a burst", "s");
        K("maxSeconds", "Longest burst still treated as a packet", "s");
        K("minPeakSnrDb", "Weakest burst kept", "dB");
        K("capture", "Kinds of burst to record");
        K("propose", "Suggest modems from the captures");
        K("proposeMinCaptures", "Captures needed for a suggestion");

        T("FrequencyMatchingConfig");
        K("enabled", "Match other stations' frequency");
        K("samples", "Frames remembered per station");
        K("maxAgeSeconds", "Oldest frame that counts", "s");
        K("minSamples", "Frames needed before acting");
        K("maxSpreadHz", "Largest spread that still counts as settled", "Hz");
        K("maxTrimHz", "Largest shift ever made", "Hz");
        K("damping", "Share of the offset applied to a station that has moved");
        K("chaseThresholdHz", "Station move that stops the correction", "Hz");
        K("chaseCooldownSeconds", "Leave a moved station alone for", "s");
        K("maxChases", "Moves before giving up on a station");

        T("RawCaptureConfig");
        K("path", "Folder for recordings");
        K("maxBytes", "Space limit", "bytes");
        K("chunkMinutes", "Length of each file", "minutes");

        T("DeadFeedConfig");
        K("silenceSeconds", "Silence before the feed counts as dead", "s");
        K("starvationSeconds", "No audio at all before the feed counts as stalled", "s");

        // ------------------------------------------------------------ monitor sites
        T("MonitorConfig");
        K("publicUrl", "Public address");
        K("directory", "Where the receiver list comes from");
        K("refreshMinutes", "Fetch the list again every", "minutes");
        K("lingerSeconds", "Keep a receiver connected after the last viewer", "s");
        K("allow", "Only these receivers");
        K("deny", "Never these receivers");
        K("modems", "Modems every receiver runs", item: "modem");
        K("uplinks", "Stations that may publish here", item: "station");

        T("UplinkConfig");
        K("callsign", "Callsign");
        K("slug", "Page name in the address, /r/NAME/");
        K("tokenSha256", "Token fingerprint, SHA-256");

        return table;
    }
}
