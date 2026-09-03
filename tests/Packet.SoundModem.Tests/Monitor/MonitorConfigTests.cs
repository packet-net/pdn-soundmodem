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
