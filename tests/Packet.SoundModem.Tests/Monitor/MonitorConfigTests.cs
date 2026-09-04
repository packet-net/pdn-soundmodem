using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// The <c>monitor</c> section: what it has to say before this process can be a monitor, and what
/// it is told when it does not.
/// </summary>
/// <remarks>
/// Every failure here is an exit 2, which the shipped unit's <c>RestartPreventExitStatus=2</c>
/// turns into one readable line in the journal rather than a crash loop. So the messages are what
/// an admin acts on and are pinned like the rest of the loader's.
/// </remarks>
public class MonitorConfigTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new("pdnsm-monitor-config");

    public void Dispose() => _scratch.Dispose();

    /// <summary>A monitor configuration that loads, for a test to break one thing in.</summary>
    private const string Working = """
        {
          "monitor": {
            "directory": "https://instances.ubersdr.org/api/instances",
            "modems": [
              { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 }
            ]
          },
          "waterfall": { "port": 8099, "title": "UK packet monitor" },
          "bind": "127.0.0.1"
        }
        """;

    [Fact]
    public void A_Working_Monitor_Config_Loads()
    {
        DaemonConfig? config = Load(Working, out string error);

        error.Should().BeEmpty();
        config.Should().NotBeNull();
        config!.Monitor.Should().NotBeNull();
        config.Monitor!.Modems.Should().ContainSingle();
        config.Monitor.RefreshMinutes.Should().Be(5);
        config.Monitor.LingerSeconds.Should().Be(60);
        config.Warnings.Should().BeEmpty();

        // Forced, not defaulted: a picker is a page for strangers by definition, so an operator's
        // console dressing on it could only ever be wrong.
        config.Waterfall!.Public.Should().BeTrue();
    }

    [Fact]
    public void Both_Monitor_And_Device_Is_A_Configuration_Error()
    {
        DaemonConfig? config = Load(
            Working.Replace(
                "\"bind\": \"127.0.0.1\"",
                "\"bind\": \"127.0.0.1\", \"device\": \"ubersdr:m9psy-1.instance.ubersdr.org\""),
            out string error);

        config.Should().BeNull();
        error.Should().Contain("device").And.Contain("monitor")
            .And.Contain("ubersdr:m9psy-1.instance.ubersdr.org",
                "the message names what it found, not just the key");
        error.Should().Contain("Remove whichever one you did not mean.");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void A_Monitor_Without_A_Device_Is_Not_Refused_For_The_Default_One()
    {
        // "device" defaults to "default" whether or not anybody wrote it down, so the exclusion
        // has to read the file rather than the parsed object. Otherwise every monitor config ever
        // written would be refused for a value nobody typed.
        Load(Working, out string error);

        error.Should().BeEmpty();
    }

    [Fact]
    public void A_Monitor_With_No_Modems_Is_A_Configuration_Error()
    {
        DaemonConfig? config = Load(
            Working.Replace(
                """
                "modems": [
                      { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 }
                    ]
                """,
                "\"modems\": []"),
            out string error);

        config.Should().BeNull();
        error.Should().Contain("\"monitor\".\"modems\" is empty")
            .And.Contain("decode nothing")
            .And.Contain("afsk300-il2pc", "the message shows what an entry looks like");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void A_Monitor_Without_A_Waterfall_Is_A_Configuration_Error()
    {
        DaemonConfig? config = Load(
            Working.Replace("\"waterfall\": { \"port\": 8099, \"title\": \"UK packet monitor\" },", ""),
            out string error);

        config.Should().BeNull();
        error.Should().Contain("needs a \"waterfall\" section")
            .And.Contain("the page's viewers are what asks for a receiver");
    }

    [Fact]
    public void A_Monitor_Whose_Waterfall_Has_No_Port_Is_A_Configuration_Error()
    {
        // A station on a LAN may reasonably let the default stand. A monitor is a public site
        // served entirely on this one port, usually through a tunnel pointed at it, and coming up
        // on the single-station default because nobody wrote a number down is not something to do
        // quietly.
        DaemonConfig? config = Load(
            Working.Replace("\"waterfall\": { \"port\": 8099, \"title\": \"UK packet monitor\" },",
                            "\"waterfall\": { \"title\": \"UK packet monitor\" },"),
            out string error);

        config.Should().BeNull();
        error.Should().Contain("\"waterfall\" has no \"port\"")
            .And.Contain("8107", "the message names the default it would otherwise have taken")
            .And.Contain("not a decision anybody made");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void A_Modem_The_Monitor_Cannot_Build_Is_A_Configuration_Error()
    {
        // The mode-name and rate checks do not build anything, so a modem that cannot actually be
        // made used to get all the way through start-up and then fail once per station, once per
        // request, as a 404 with a stack trace behind it. Built once now, against a throwaway
        // channel, so it is one message here instead.
        var journal = new StationJournal("", _ => { }, Errors.Add);
        var channel = new Packet.SoundModem.Channel.SoundModemChannel(12000);

        bool built = StationFactory.TryAddModems(
            channel,
            [new ModemConfig { SubChannel = 0, Mode = "afsk1200", AcceptPlainIl2p = true }],
            12000, null, journal);

        built.Should().BeFalse();
        Errors.Should().Contain(e => e.Contains("does not run IL2P+CRC"));
    }

    [Fact]
    public void A_Directory_That_Is_Not_A_Url_Is_A_Configuration_Error()
    {
        foreach (string bad in (string[])["instances.ubersdr.org", "/api/instances", "ftp://x/y", ""])
        {
            DaemonConfig? config = Load(
                Working.Replace("https://instances.ubersdr.org/api/instances", bad),
                out string error);

            config.Should().BeNull("\"{0}\" is not an absolute http or https URL", bad);
            error.Should().Contain("\"monitor\".\"directory\"")
                .And.Contain("https://instances.ubersdr.org/api/instances",
                    "the message says what a good one looks like");
        }
    }

    [Fact]
    public void A_Negative_Refresh_Or_Linger_Is_A_Configuration_Error()
    {
        foreach ((string key, string words) in (ReadOnlySpan<(string, string)>)
            [("refreshMinutes", "minutes"), ("lingerSeconds", "seconds")])
        {
            DaemonConfig? config = Load(
                Working.Replace("\"modems\":", $"\"{key}\": -1, \"modems\":"), out string error);

            config.Should().BeNull();
            error.Should().Contain($"\"monitor\".\"{key}\" is -1").And.Contain(words);
        }
    }

    [Fact]
    public void An_Allow_Or_Deny_Entry_That_Is_Not_A_Hostname_Is_A_Configuration_Error()
    {
        // A deny list is how an operator's wish not to be listed is honoured, so an entry that
        // silently matches nothing is the worst possible outcome: somebody believes they have
        // been taken off a list they are still on.
        foreach (string list in (string[])["allow", "deny"])
        {
            foreach (string bad in (string[])
                     ["https://m9psy-1.instance.ubersdr.org/", "m9psy-1.instance.ubersdr.org:443",
                      "m9psy 1", "", "-leading.example.org"])
            {
                DaemonConfig? config = Load(
                    Working.Replace(
                        "\"modems\":", $"\"{list}\": [\"{bad}\"], \"modems\":"),
                    out string error);

                config.Should().BeNull("\"{0}\" is not a hostname", bad);
                error.Should().Contain($"\"monitor\".\"{list}\"")
                    .And.Contain("no scheme, no port, no path");
            }
        }

        DaemonConfig? good = Load(
            Working.Replace(
                "\"modems\":",
                "\"deny\": [\"m9psy-1.instance.ubersdr.org\", \"NA5B.com\"], \"modems\":"),
            out string noError);
        noError.Should().BeEmpty();
        good!.Monitor!.Deny.Should().HaveCount(2);
    }

    [Fact]
    public void An_Unknown_Key_In_The_Monitor_Section_Is_Reported()
    {
        DaemonConfig? config = Load(
            Working.Replace("\"modems\":", "\"refreshMins\": 5, \"modems\":"), out string error);

        error.Should().BeEmpty("an unknown key is ignored rather than refused");
        config!.Warnings.Should().ContainSingle()
            .Which.Should().Contain("monitor: \"refreshMins\"").And.Contain("IGNORED");
    }

    [Fact]
    public void A_Monitor_Frame_Log_Path_Is_A_Directory()
    {
        var journal = new StationJournal("", _ => { }, Errors.Add);

        // Missing: created, because the packaged service's state directory may be the first thing
        // this daemon ever writes to.
        string fresh = Path.Combine(_scratch.FullName, "logs");
        MonitorStartup.TryPrepareFrameLogDirectory(fresh, journal, out string directory)
            .Should().BeTrue();
        directory.Should().Be(fresh);
        Directory.Exists(fresh).Should().BeTrue();
        Errors.Should().BeEmpty();

        // Already a directory: used as it stands.
        MonitorStartup.TryPrepareFrameLogDirectory(fresh, journal, out _).Should().BeTrue();

        // A file: refused in words, not left to fail as a SQLite error from inside whichever
        // receiver a visitor happened to pick first.
        string file = Path.Combine(_scratch.FullName, "frames.db");
        File.WriteAllText(file, "not a directory");
        MonitorStartup.TryPrepareFrameLogDirectory(file, journal, out _).Should().BeFalse();
        Errors.Should().ContainSingle().Which.Should()
            .Contain("which is a file")
            .And.Contain("DIRECTORY here")
            .And.Contain("frames-<slug>.db");

        // And a single-station path pasted into a monitor config, which does not exist yet:
        // creating a directory called frames.db would obey the letter of it and leave somebody
        // working out later why their log was not where they put it.
        Errors.Clear();
        string pasted = Path.Combine(_scratch.FullName, "elsewhere", "frames.db");
        MonitorStartup.TryPrepareFrameLogDirectory(pasted, journal, out _).Should().BeFalse();
        Errors.Should().ContainSingle().Which.Should().Contain("names a database file");
        Directory.Exists(pasted).Should().BeFalse();
    }

    // ------------------------------------------------------------------ monitor.uplinks

    /// <summary>The uplink table, as a working monitor config would carry it.</summary>
    private const string OneUplink = """
        "uplinks": [
          {
            "callsign": "GB7RDG-2",
            "slug": "gb7rdg-2",
            "tokenSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
          }
        ],
        """;

    [Fact]
    public void A_Monitor_With_Uplinks_Loads_And_One_Without_Accepts_None()
    {
        DaemonConfig? config = Load(WithUplinks(OneUplink), out string error);

        error.Should().BeEmpty();
        config!.Monitor!.Uplinks.Should().ContainSingle();
        config.Monitor.Uplinks[0].Callsign.Should().Be("GB7RDG-2");
        config.Monitor.Uplinks[0].Slug.Should().Be("gb7rdg-2");
        config.Warnings.Should().BeEmpty();

        // And the default, which is the monitor that is already deployed: no table, no endpoint,
        // nothing about it anywhere.
        Load(Working, out _)!.Monitor!.Uplinks.Should().BeEmpty();
    }

    [Fact]
    public void An_Uplink_Without_A_Callsign_Is_A_Configuration_Error()
    {
        Load(WithUplinks(OneUplink.Replace("\"GB7RDG-2\"", "\"\"")), out string error)
            .Should().BeNull();

        error.Should().Contain("callsign")
            .And.Contain("has no business being there",
                "a station on a public page that will not say who it is");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void An_Uplink_Slug_That_Is_Not_A_Path_Segment_Is_A_Configuration_Error()
    {
        Load(WithUplinks(OneUplink.Replace("\"gb7rdg-2\"", "\"GB7RDG 2/\"")), out string error)
            .Should().BeNull();

        error.Should().Contain("GB7RDG 2/").And.Contain("cannot be a path segment")
            .And.Contain("/r/gb7rdg-2/", "and it says what the callsign would have given");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void An_Uplink_Token_Hash_That_Is_Not_A_Hash_Is_A_Configuration_Error()
    {
        Load(WithUplinks(OneUplink.Replace(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            "hunter2")), out string error).Should().BeNull();

        error.Should().Contain("64 hex characters")
            .And.Contain("--uplink-token", "the message says how to make one")
            .And.Contain("never the token",
                "and why this site holds the hash rather than the token");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void Two_Uplinks_With_One_Slug_Is_A_Configuration_Error()
    {
        const string twice = """
            "uplinks": [
              { "callsign": "GB7RDG-2", "slug": "gb7rdg-2",
                "tokenSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" },
              { "callsign": "M0LTE-7", "slug": "gb7rdg-2",
                "tokenSha256": "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752" }
            ],
            """;

        Load(WithUplinks(twice), out string error).Should().BeNull();
        error.Should().Contain("gb7rdg-2").And.Contain("One page cannot be two stations");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void One_Token_For_Two_Stations_Is_A_Configuration_Error()
    {
        const string shared = """
            "uplinks": [
              { "callsign": "GB7RDG-2", "slug": "gb7rdg-2",
                "tokenSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" },
              { "callsign": "M0LTE-7", "slug": "m0lte-7",
                "tokenSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" }
            ],
            """;

        // A token names one station. The same one twice cannot say which of them is connecting,
        // and the one-connection-per-token rule would have them closing each other's sockets.
        Load(WithUplinks(shared), out string error).Should().BeNull();
        error.Should().Contain("another entry already has").And.Contain("a token of its own");
        ShouldGuideTheOperator(error);
    }

    [Fact]
    public void An_Unknown_Key_In_An_Uplink_Is_Reported()
    {
        DaemonConfig? config = Load(
            WithUplinks(OneUplink.Replace("\"slug\"", "\"slugg\"")), out string error);

        // Ignored silently by the deserialiser, which is exactly what makes it worth saying: a
        // station with a typo for a slug would be served under an empty path segment.
        error.Should().Contain("cannot be a path segment", "the missing slug is caught first");
        config.Should().BeNull();
    }

    /// <summary>The working monitor config with an uplink table dropped into it.</summary>
    private static string WithUplinks(string uplinks) =>
        Working.Replace("\"monitor\": {", "\"monitor\": {\n    " + uplinks);

    private List<string> Errors { get; } = [];

    private DaemonConfig? Load(string json, out string error)
    {
        string path = Path.Combine(_scratch.FullName, $"monitor-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return DaemonConfig.TryLoad(path, out error);
    }

    /// <summary>Every failure message must be actionable, not just accurate.</summary>
    private static void ShouldGuideTheOperator(string error)
    {
        error.Should().Contain("systemctl restart pdn-soundmodem",
            "the message must say how to apply the fix");
        error.Should().Contain("CONFIG.md", "the message must point at the reference");
        error.Should().NotContain("Exception", "a stack trace is not an explanation");
        error.Should().NotContain("   at ", "a stack trace is not an explanation");
    }
}
