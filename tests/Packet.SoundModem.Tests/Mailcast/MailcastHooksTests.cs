using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Packet.Mailcast;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// Programs for the hooks tests: each is /bin/sh running a script, so no test ever executes a
/// file it has just written (which a fork elsewhere in the test process can make fail with "text
/// file busy"). Each run appends one line to <see cref="CallsPath"/>: the hook, the slot, whether
/// "before" worked, the dial and the centre.
/// </summary>
internal sealed class HookScripts
{
    private readonly string _dir;

    internal HookScripts(string dir)
    {
        _dir = dir;
        CallsPath = Path.Combine(dir, "calls.txt");
    }

    internal string CallsPath { get; }

    /// <summary>The lines written so far.</summary>
    internal IReadOnlyList<string> Calls => File.Exists(CallsPath) ? File.ReadAllLines(CallsPath) : [];

    /// <summary>A hook that notes its run, prints a line and exits with <paramref name="exit"/>.</summary>
    internal HookCommand Hook(string name, int exit = 0, int timeoutSeconds = HookCommand.DefaultTimeoutSeconds)
    {
        string script = Path.Combine(_dir, name + ".sh");
        File.WriteAllText(script,
            $"echo \"$MAILCAST_HOOK $MAILCAST_SLOT_UTC ${{MAILCAST_BEFORE_OK:--}} $MAILCAST_DIAL_KHZ $MAILCAST_CENTRE_KHZ\" >> '{CallsPath}'\n"
            + "echo \"said $MAILCAST_HOOK\"\n"
            + $"exit {exit}\n");
        return new HookCommand { Command = "/bin/sh", Args = [script], TimeoutSeconds = timeoutSeconds };
    }
}

/// <summary>
/// <c>mailcast.hooks</c> in the configuration file: what loads, what is refused with a sentence
/// saying why, and that the commands' arguments are never served or logged.
/// </summary>
public sealed class MailcastHooksConfigTests : IDisposable
{
    private readonly ScratchDirectory _dir = new("pdnsm-mailcast-hooks-config");

    public void Dispose() => _dir.Dispose();

    private (DaemonConfig? Config, string Error) Load(string hooks)
    {
        string path = Path.Combine(_dir.FullName, "soundmodem.json");
        File.WriteAllText(path, $$$"""{"device": "null", "mailcast": {"bbs": {"password": "x"}, "hooks": {{{hooks}}}}}""");
        DaemonConfig? config = DaemonConfig.TryLoad(path, out string error);
        return (config, error);
    }

    [Fact]
    public void Both_Hooks_Load_With_A_30_S_Timeout_Unless_Set()
    {
        (DaemonConfig? config, string error) = Load("""
            {"before": {"command": "/bin/sh", "args": ["-c", "exit 0"], "timeoutSeconds": 45},
             "after": {"command": "/bin/true"}}
            """);

        error.Should().BeEmpty();
        MailcastHooksConfig hooks = config!.Mailcast!.Hooks!;
        hooks.Before!.Command.Should().Be("/bin/sh");
        hooks.Before.Args.Should().Equal("-c", "exit 0");
        hooks.Before.TimeoutSeconds.Should().Be(45);
        hooks.After!.Args.Should().BeEmpty();
        hooks.After.TimeoutSeconds.Should().Be(30);
        config.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Either_Hook_Can_Be_Left_Out()
    {
        (DaemonConfig? config, string error) = Load("""{"after": {"command": "/bin/true"}}""");

        error.Should().BeEmpty();
        config!.Mailcast!.Hooks!.Before.Should().BeNull();
    }

    [Fact]
    public void A_Misspelt_Setting_In_A_Hook_Is_Refused_Not_Ignored()
    {
        (DaemonConfig? config, string error) = Load("""{"before": {"command": "/bin/true", "timeout": 30}}""");

        config.Should().BeNull();
        error.Should().Contain("\"timeout\" is not a setting of a hook")
            .And.Contain("a hook has \"command\", \"args\" and \"timeoutSeconds\"");
    }

    [Fact]
    public void A_Misspelt_Hook_Is_Refused_Not_Ignored()
    {
        (DaemonConfig? config, string error) = Load("""{"befor": {"command": "/bin/true"}}""");

        config.Should().BeNull();
        error.Should().Contain("\"befor\" is not a setting of \"mailcast\".\"hooks\": it has \"before\" and \"after\"");
    }

    [Fact]
    public void Arguments_That_Are_Not_A_List_Of_Strings_Are_Refused_Saying_What_A_Hook_Looks_Like()
    {
        (DaemonConfig? config, string error) = Load("""{"before": {"command": "/bin/true", "args": "stop"}}""");

        config.Should().BeNull();
        error.Should().Contain("\"mailcast\".\"hooks\" cannot be read").And.Contain("\"timeoutSeconds\": 30");
    }

    [Theory]
    [InlineData("mailcast-hook", 30, "is not a full path")]
    [InlineData("/nonexistent/mailcast-hook", 30, "does not exist")]
    [InlineData("/bin/true", 0, "\"timeoutSeconds\" 0 must be from 1 to 300")]
    [InlineData("/bin/true", 301, "\"timeoutSeconds\" 301 must be from 1 to 300")]
    public void A_Hook_That_Could_Not_Run_Is_Refused_By_Name(string command, int timeout, string why)
    {
        (DaemonConfig? config, string error) = Load($$$"""{"after": {"command": "{{{command}}}", "timeoutSeconds": {{{timeout}}}}}""");

        config.Should().BeNull();
        error.Should().Contain("\"mailcast\".\"hooks\".\"after\": ").And.Contain(why);
    }

    [Fact]
    public void A_Program_The_Service_Cannot_Execute_Is_Refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "execute permission is a Unix question");
        string script = Path.Combine(_dir.FullName, "not-executable");
        File.WriteAllText(script, "exit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        (DaemonConfig? config, string error) = Load($$$"""{"before": {"command": "{{{script}}}"}}""");

        config.Should().BeNull();
        error.Should().Contain("\"mailcast\".\"hooks\".\"before\": ").And.Contain("is not executable by the user");
    }

    [Fact]
    public void The_Api_Refuses_A_Hook_That_Could_Not_Run_Before_It_Restarts_Onto_It()
    {
        string json = """
            {"device": "null", "dialFrequency": 7052000,
             "mailcast": {"bbs": {"password": "x"}, "hooks": {"before": {"command": "/nonexistent/mailcast-hook"}}}}
            """;

        string? why = ConfigApi.Validate(json, Path.Combine(_dir.FullName, "soundmodem.json"));

        why.Should().Contain("\"mailcast\".\"hooks\".\"before\": \"command\" /nonexistent/mailcast-hook does not exist");
        ConfigApi.Validate(json.Replace("/nonexistent/mailcast-hook", "/bin/true", StringComparison.Ordinal), Path.Combine(_dir.FullName, "soundmodem.json"))
            .Should().BeNull();
    }

    [Fact]
    public void The_Configuration_Api_Never_Serves_A_Hooks_Arguments()
    {
        string redacted = ConfigApi.Redact("""
            {"Mailcast": {"Hooks": {"Before": {"command": "/usr/local/bin/hook", "Args": ["--password", "s3cret"]},
                                    "after": {"command": "/usr/local/bin/hook", "args": ["start", "s3cret"]}}}}
            """);

        redacted.Should().NotContain("s3cret");
        redacted.Should().Contain("/usr/local/bin/hook");
        redacted.Should().Contain("\"Args\": \"(set, not shown)\"").And.Contain("\"args\": \"(set, not shown)\"");
    }

    [Fact]
    public void The_Journal_Names_The_Program_But_Never_Its_Arguments()
    {
        var config = new MailcastConfig
        {
            Bbs = new MailcastBbsConfig { Password = "x" },
            Hooks = new MailcastHooksConfig
            {
                Before = new HookCommand { Command = "/usr/local/bin/hook", Args = ["--password", "s3cret"] },
                After = new HookCommand { Command = "/usr/local/bin/hook", Args = ["start"] },
            },
        };
        using var hooks = new MailcastHooks(config, _dir.FullName, TimeProvider.System, _ => { });

        hooks.DescribeConfig().Should().Be(
            "mailcast: hooks: \"before\" each slot runs /usr/local/bin/hook (with 2 arguments, not shown), started 30 s ahead so it is done in time; "
            + "\"after\" runs /usr/local/bin/hook (with 1 argument, not shown)");
    }
}

/// <summary>
/// The hooks on a station that hears the signal where it is, on a fake clock, with real (tiny)
/// processes: the window they run around, the note that keeps an "after" owed across a restart,
/// and who may run it.
/// </summary>
public sealed class MailcastHooksTests : IAsyncDisposable
{
    // 11:50 UTC on 5 October: the noon slot is in daylight at IO91lk.
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 11, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Slot = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ConcurrentQueue<string> _journal = new();
    private readonly ScratchDirectory _dir = new("pdnsm-mailcast-hooks");
    private readonly HookScripts _scripts;
    private readonly List<MailcastHooks> _hooks = [];
    private readonly CancellationTokenSource _stop = new();

    public MailcastHooksTests() => _scripts = new HookScripts(_dir.FullName);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (MailcastHooks hooks in _hooks)
        {
            hooks.Dispose();
        }

        _stop.Dispose();
        _dir.Dispose();
    }

    private string StateDir => Path.Combine(_dir.FullName, "mailcast");

    private MailcastHooks Hooks(HookCommand? before, HookCommand? after)
    {
        var config = new MailcastConfig
        {
            Bbs = new MailcastBbsConfig { Password = "x" },
            Hooks = new MailcastHooksConfig { Before = before, After = after },
        };
        var hooks = new MailcastHooks(config, StateDir, _time, _journal.Enqueue);
        _hooks.Add(hooks);
        return hooks;
    }

    private static Task Run(MailcastHooks hooks, CancellationToken cancellation) =>
        Task.Run(() => hooks.RunAsync(at => MailcastHooks.PassbandWindowAt(at, MailcastOnAir.DefaultTimetable), cancellation));

    /// <summary>
    /// Moves the fake clock on to <paramref name="until"/>, never past the loop's next timer until
    /// it has acted on that one and set the next. Bounded by counts of looks, not by a time.
    /// </summary>
    private async Task AdvanceTo(MailcastHooks hooks, DateTimeOffset until)
    {
        await Eventually(() => hooks.NextWake > DateTimeOffset.MinValue, "the loop has set its first timer");
        while (_time.GetUtcNow() < until)
        {
            DateTimeOffset due = hooks.NextWake;
            if (due <= _time.GetUtcNow())
            {
                await Eventually(() => hooks.NextWake != due, "the loop acted on its timer");
                continue;
            }

            _time.SetUtcNow(due < until ? due : until);
            if (due <= _time.GetUtcNow())
            {
                await Eventually(() => hooks.NextWake != due, "the loop acted on its timer");
            }
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        // Generous, as real processes are run: a busy runner can take a while to start one.
        for (int look = 0; look < 12000 && !condition(); look++)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue(what);
    }

    [Fact]
    public async Task On_A_Station_That_Hears_The_Signal_The_Hooks_Run_From_Two_Minutes_Before_Each_Slot_To_Twelve_After()
    {
        MailcastHooks hooks = Hooks(_scripts.Hook("before", timeoutSeconds: 30), _scripts.Hook("after"));
        Task loop = Run(hooks, _stop.Token);

        // "before" starts its 30 s timeout ahead of the 11:58 opening.
        await AdvanceTo(hooks, Slot - TimeSpan.FromSeconds(151));
        _scripts.Calls.Should().BeEmpty();
        await AdvanceTo(hooks, Slot - TimeSpan.FromSeconds(150));
        await Eventually(() => _scripts.Calls.Count == 1, "\"before\" ran at 11:57:30");
        _scripts.Calls[0].Should().Be("before 2026-10-05T12:00:00Z - 7052.0 7053.8");
        await Eventually(() => hooks.OwedSlot == Slot, "\"after\" is owed");
        File.Exists(hooks.NotePath).Should().BeTrue("a station killed now runs \"after\" at its next start");

        await AdvanceTo(hooks, Slot + TimeSpan.FromMinutes(12) - TimeSpan.FromSeconds(1));
        _scripts.Calls.Should().HaveCount(1);
        await AdvanceTo(hooks, Slot + TimeSpan.FromMinutes(12));
        await Eventually(() => _scripts.Calls.Count == 2 && !File.Exists(hooks.NotePath), "\"after\" ran at 12:12");
        _scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 1 7052.0 7053.8");
        hooks.OwedSlot.Should().BeNull();
        _journal.Should().Contain("mailcast: hooks: before: said before");
        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: \"after\" finished (exit 0)", StringComparison.Ordinal));
        _journal.Should().NotContain(line => line.Contains(_dir.FullName + Path.DirectorySeparatorChar + "before.sh", StringComparison.Ordinal),
            "the arguments are never logged");

        await _stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_Station_Stopped_During_A_Window_Runs_After_On_The_Way_Out()
    {
        MailcastHooks hooks = Hooks(_scripts.Hook("before"), _scripts.Hook("after"));
        Task loop = Run(hooks, _stop.Token);
        await AdvanceTo(hooks, Slot);
        await Eventually(() => hooks.OwedSlot == Slot, "\"before\" ran");

        await _stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(60));
        _scripts.Calls.Should().HaveCount(1, "the loop leaves \"after\" to the way out");
        await hooks.FinishAsync().WaitAsync(TimeSpan.FromSeconds(60));

        _scripts.Calls.Should().Equal("before 2026-10-05T12:00:00Z - 7052.0 7053.8", "after 2026-10-05T12:00:00Z 1 7052.0 7053.8");
        File.Exists(hooks.NotePath).Should().BeFalse();
        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: running \"after\" for the 12:00 UTC slot as the station stops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Station_Killed_During_A_Window_Runs_After_When_It_Next_Starts_And_Leaves_That_Slot_Alone()
    {
        MailcastHooks first = Hooks(_scripts.Hook("before"), _scripts.Hook("after"));
        using (var killed = new CancellationTokenSource())
        {
            Task loop = Run(first, killed.Token);
            await AdvanceTo(first, Slot);
            await Eventually(() => first.OwedSlot == Slot, "\"before\" ran");
            await killed.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(60));
        }

        // Killed: nothing ran "after", and the note is all that is left of it.
        File.Exists(first.NotePath).Should().BeTrue();
        _time.SetUtcNow(Slot + TimeSpan.FromMinutes(5));

        MailcastHooks second = Hooks(_scripts.Hook("before"), _scripts.Hook("after"));
        Task again = Run(second, _stop.Token);
        await Eventually(() => _scripts.Calls.Count == 2 && !File.Exists(second.NotePath), "\"after\" ran at the start");
        _scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 1 7052.0 7053.8");
        second.RecoveredSlot.Should().Be(Slot);
        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: the station stopped during the 12:00 UTC slot on 2026-10-05 last time", StringComparison.Ordinal));

        // The rest of the noon slot is left alone: the next "before" is for 13:00, at 12:57:30.
        await AdvanceTo(second, Slot + TimeSpan.FromMinutes(57));
        _scripts.Calls.Should().HaveCount(2);
        await AdvanceTo(second, Slot + TimeSpan.FromMinutes(57.5));
        await Eventually(() => _scripts.Calls.Count == 3, "\"before\" ran for 13:00");
        _scripts.Calls[2].Should().StartWith("before 2026-10-05T13:00:00Z");

        await _stop.CancelAsync();
        await again.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task An_After_That_Fails_Stays_Owed_Until_One_Works()
    {
        MailcastHooks first = Hooks(_scripts.Hook("before"), _scripts.Hook("after-fails", exit: 4));
        await first.BeforeAsync(Slot, this, "", CancellationToken.None);
        await first.AfterAsync(this, unowned: false);

        File.Exists(first.NotePath).Should().BeTrue("it did not exit with 0");
        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: WARNING - \"after\" for the 12:00 UTC slot exited with 4", StringComparison.Ordinal)
            && line.EndsWith("so it is run again when the station next starts", StringComparison.Ordinal));

        MailcastHooks second = Hooks(_scripts.Hook("before"), _scripts.Hook("after"));
        second.Recover();
        second.OwedSlot.Should().Be(Slot);
        await second.FinishAsync();
        File.Exists(second.NotePath).Should().BeFalse();
        _scripts.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_Failed_Before_Is_Said_And_After_Still_Runs_Told_So()
    {
        MailcastHooks hooks = Hooks(_scripts.Hook("before-fails", exit: 3), _scripts.Hook("after"));

        (await hooks.BeforeAsync(Slot, this, "; listening anyway", CancellationToken.None)).Should().Be(MailcastBeforeOutcome.Failed);
        await hooks.AfterAsync(this, unowned: false);

        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: WARNING - \"before\" for the 12:00 UTC slot exited with 3", StringComparison.Ordinal)
            && line.EndsWith("; listening anyway", StringComparison.Ordinal));
        _scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 0 7052.0 7053.8");
    }

    [Fact]
    public async Task Only_The_Windows_Owner_Runs_Its_After_And_Nobody_Else_Starts_One_Meanwhile()
    {
        MailcastHooks hooks = Hooks(_scripts.Hook("before"), _scripts.Hook("after"));
        object owner = new();
        object other = new();
        (await hooks.BeforeAsync(Slot, owner, "", CancellationToken.None)).Should().Be(MailcastBeforeOutcome.Ok);

        (await hooks.BeforeAsync(Slot, other, "", CancellationToken.None)).Should().Be(MailcastBeforeOutcome.Busy);
        (await hooks.BeforeAsync(Slot, owner, "", CancellationToken.None)).Should().Be(MailcastBeforeOutcome.Ok, "its own window is still open: not run again");
        await hooks.AfterAsync(other, unowned: true);
        _scripts.Calls.Should().HaveCount(1, "\"after\" is not the other's to run");

        await hooks.AfterAsync(owner, unowned: false);
        _scripts.Calls.Should().HaveCount(2);
        hooks.OwedSlot.Should().BeNull();
    }
}
