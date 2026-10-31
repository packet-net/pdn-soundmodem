using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The built-in mailcast receiver is gone (issue #588): a configuration that still has a
/// <c>mailcast</c> section starts, says once where the receiver is, and behaves as it would
/// without the section. A Flex station keeps its usual slice receive filter.
/// </summary>
public sealed class MailcastRetiredTests : IDisposable
{
    private const string FilterPrefix = "flex: setting the slice receive filter to ";

    private readonly ScratchDirectory _dir = new("pdnsm-mailcast-retired");

    public void Dispose() => _dir.Dispose();

    private const string OldSection =
        """
        "mailcast": {
          "bbs": { "type": "linBpq", "host": "127.0.0.1", "port": 8011, "login": "Q0CAST", "password": "secret" },
          "sources": ["GB7RDG"], "dialKHz": 7052.3, "retune": true, "stateDirectory": "/var/lib/pdn-soundmodem/mailcast",
          "hooks": { "after": { "command": "/bin/true", "args": ["x"], "timeoutSeconds": 5 } }
        }
        """;

    [Fact]
    public void An_Old_Mailcast_Section_Loads_Without_Complaint()
    {
        string path = Path.Combine(_dir.FullName, "old.json");
        File.WriteAllText(path, "{ \"device\": \"null\", \"modems\": [ { \"mode\": \"afsk1200\" } ], " + OldSection + " }");

        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);

        config.Should().NotBeNull(error);
        config!.Mailcast.Should().NotBeNull("the section is kept only so the start-up line can be said");
    }

    [Fact]
    public void A_Mailcast_Section_Of_Any_Shape_Still_Loads()
    {
        string path = Path.Combine(_dir.FullName, "odd.json");
        File.WriteAllText(path, """{ "device": "null", "modems": [ { "mode": "afsk1200" } ], "mailcast": { "dialKHz": "nonsense", "hooks": 5 } }""");

        DaemonConfig.TryLoad(path, out string error).Should().NotBeNull(error);
    }

    [Fact]
    public void The_Notice_Is_One_Plain_Ascii_Line_Naming_The_Receiver_And_The_Guide()
    {
        MailcastRetired.Line.Should().NotContain("\n");
        MailcastRetired.Line.All(c => c is >= ' ' and <= '~').Should().BeTrue();
        MailcastRetired.Line.Should().Contain("pdn-mailcast-receiver").And.Contain(MailcastRetired.GuideUrl);
        MailcastRetired.Line.Should().NotContain("secret");
    }

    [Fact]
    public void The_Daemon_Says_It_Once_At_Start_Up_With_The_Section_And_Not_Without()
    {
        string with = Path.Combine(_dir.FullName, "with.json");
        string without = Path.Combine(_dir.FullName, "without.json");
        // An unknown mode stops the daemon right after the notice and before anything is opened,
        // so the run is short and needs no sound card.
        File.WriteAllText(with, "{ \"modems\": [ { \"mode\": \"no-such-mode\" } ], " + OldSection + " }");
        File.WriteAllText(without, """{ "modems": [ { "mode": "no-such-mode" } ] }""");

        (_, string withOut, string withErr) = UsageTests.RunDaemon("--config", with);
        (_, string withoutOut, string withoutErr) = UsageTests.RunDaemon("--config", without);

        string[] said = (withOut + withErr).Split('\n').Where(l => l.StartsWith("mailcast:", StringComparison.Ordinal)).ToArray();
        said.Should().ContainSingle().Which.Should().Be(MailcastRetired.Line);
        (withoutOut + withoutErr).Should().NotContain("mailcast:");
        (withOut + withErr).Should().NotContain("secret");
    }

    [Fact]
    public async Task A_Flex_Station_Starts_With_The_Section_And_Opens_Its_Slice_Filter_As_Without_It()
    {
        (string withLine, string withNotice) = await FilterLineAsync(OldSection + ",");
        (string withoutLine, string withoutNotice) = await FilterLineAsync("");

        // Without the section the channel runs at 12 kHz; with it, as before the removal, at 48 kHz,
        // where the band planner's edges differ. Either way the filter covers the modems and
        // nothing else: no mailcast band is added to it.
        withoutLine.Should().Be("flex: setting the slice receive filter to 300-3150 Hz (all modems)");
        withLine.Should().Be("flex: setting the slice receive filter to 350-3100 Hz (all modems)");
        withNotice.Should().Be(MailcastRetired.Line);
        withoutNotice.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Leftover_Section_Still_Gives_The_48_kHz_Channel_Other_Settings_Were_Written_For()
    {
        // publish.audioRate 24000 divides 48000 but not 12000: before the removal the section made
        // the channel 48 kHz, and this must go on starting.
        string with = Path.Combine(_dir.FullName, "rate-with.json");
        string rest = "\"device\": \"flex:mock\", \"publish\": { \"audioRate\": 24000, \"callsign\": \"G8ABC\", \"token\": \"" + new string('a', 40) + "\", \"url\": \"ws://127.0.0.1:9/uplink\" }, ";
        File.WriteAllText(with, "{ " + rest + OldSection + ", \"waterfall\": { \"port\": " + FreePorts.Next()
            + " }, \"modems\": [ { \"mode\": \"afsk1200\", \"port\": " + FreePorts.Next() + " } ] }");
        string withLine = await StartedLineAsync(with);
        withLine.Should().StartWith(FilterPrefix, "it started");
    }

    [Fact]
    public void A_Leftover_Sections_Secrets_Are_Never_Served_Whatever_The_Key_Case()
    {
        string redacted = ConfigApi.Redact(
            """{"Mailcast": {"BBS": {"Password": "pick-one", "login": "Q0CAST"}, "Hooks": {"After": {"command": "/bin/x", "ARGS": ["s3cret"]}, "before": {"command": "/bin/y", "args": ["h1dden"]}}}}""");

        redacted.Should().NotContain("pick-one").And.NotContain("s3cret").And.NotContain("h1dden");
        redacted.Should().Contain("(set, not shown)").And.Contain("Q0CAST").And.Contain("/bin/x");
        ConfigApi.Redact("""{"mailcast": {"bbs": {"password": "pick-one"}}}""").Should().NotContain("pick-one");
    }

    /// <summary>
    /// Runs the daemon on a mock Flex with <paramref name="extra"/> in its config and returns the
    /// slice filter line it prints (and the mailcast notice, if any), then stops it.
    /// </summary>
    private async Task<(string Line, string Notice)> FilterLineAsync(string extra)
    {
        string path = Path.Combine(_dir.FullName, $"flex-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            "{ \"device\": \"flex:mock\", " + extra
            + $" \"waterfall\": {{ \"port\": {FreePorts.Next()} }},"
            + $" \"modems\": [ {{ \"mode\": \"afsk1200\", \"port\": {FreePorts.Next()} }} ] }}");
        return await RunUntilFilterLineAsync(path);
    }

    private async Task<string> StartedLineAsync(string path) => (await RunUntilFilterLineAsync(path)).Line;

    private static async Task<(string Line, string Notice)> RunUntilFilterLineAsync(string path)
    {

        string dll = Path.Combine(AppContext.BaseDirectory, "pdn-soundmodem.dll");
        var info = new ProcessStartInfo(UsageTests.DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(path);

        var noticeLines = new StringBuilder();
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = info };
        void Line(string? line)
        {
            if (line is null)
            {
                return;
            }

            if (line.StartsWith("mailcast:", StringComparison.Ordinal))
            {
                lock (noticeLines)
                {
                    noticeLines.Append(line);
                }
            }

            if (line.StartsWith(FilterPrefix, StringComparison.Ordinal))
            {
                found.TrySetResult(line);
            }
        }

        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            string line = await found.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // The notice is printed well before the filter line, so it has been seen by now.
            lock (noticeLines)
            {
                return (line, noticeLines.ToString());
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
