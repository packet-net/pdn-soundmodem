using System.Collections.Concurrent;
using Packet.Mailcast;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// The hooks on a station that retunes its rig for the signal: "before" is done before the rig is
/// retuned and the transmitter held, a failed one means no retune, and "after" runs only once the
/// rig is back and the station free to transmit, at the window's end, on the way out, and after
/// a restart in the middle of a window.
/// </summary>
public sealed partial class MailcastRetuneTests
{
    /// <summary>Where the rig was, and whether the station was held, as each hook started.</summary>
    private readonly ConcurrentQueue<(long DialHz, bool Held)> _atHookStart = new();

    private HookScripts? _scripts;

    private HookScripts Scripts => _scripts ??= new HookScripts(_dir.FullName);

    private string HooksDir => Path.Combine(_dir.FullName, "mailcast");

    private MailcastHooks Hooks(RigControl rig, HookCommand? before, HookCommand? after)
    {
        var config = new MailcastConfig
        {
            Bbs = new MailcastBbsConfig { Password = "x" },
            Retune = true,
            Hooks = new MailcastHooksConfig { Before = before, After = after },
        };
        var hooks = new MailcastHooks(config, HooksDir, _time, _journal.Enqueue);
        hooks.Runner.Started += _ => _atHookStart.Enqueue((_fake.DialHz, rig.HoldsTransmitter));
        return hooks;
    }

    private (MailcastRetuner Retuner, Task Running) RetunerWith(RigControl rig, MailcastHooks hooks, CancellationToken cancellation)
    {
        var config = new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "x" }, Retune = true, Hooks = new MailcastHooksConfig() };
        var retuner = new MailcastRetuner(rig, config, null, () => MailcastOnAir.DefaultTimetable, _time, _journal.Enqueue, hooks);
        _retuners.Add(retuner);
        Task running = Task.Run(() => retuner.RunAsync(cancellation));
        _running.Add(running);
        return (retuner, running);
    }

    private static async Task Patiently(Func<bool> condition, string what)
    {
        // Generous, as real processes are run: a busy runner can take a while to start one.
        for (int look = 0; look < 12000 && !condition(); look++)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue(what);
    }

    private int JournalIndex(Func<string, bool> match) => _journal.ToList().FindIndex(line => match(line));

    [Fact]
    public async Task Before_Is_Done_Before_The_Rig_Is_Retuned_And_After_Runs_Once_It_Is_Back()
    {
        RigControl rig = await StartedRig();
        MailcastHooks hooks = Hooks(rig, Scripts.Hook("before", timeoutSeconds: 30), Scripts.Hook("after"));
        (MailcastRetuner retuner, _) = RetunerWith(rig, hooks, _stop.Token);

        await Patiently(() => retuner.State.Contains("the \"before\" command runs at 11:58:30 UTC", StringComparison.Ordinal), "waiting to run \"before\"");
        await AdvanceTo(Slot - TimeSpan.FromSeconds(90));
        await Patiently(() => Scripts.Calls.Count == 1 && hooks.OwedSlot == Slot, "\"before\" ran at 11:58:30, its timeout ahead of the window");
        _fake.DialHz.Should().Be(7_049_450, "not retuned until the window opens");

        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Patiently(() => retuner.Listening && _fake.DialHz == 7_052_000, "retuned at 11:59");
        _atHookStart.First().Should().Be((7_049_450L, false), "\"before\" ran with the rig on the station's own dial and nothing held");
        JournalIndex(l => l.StartsWith("mailcast: hooks: \"before\" finished (exit 0)", StringComparison.Ordinal))
            .Should().BeLessThan(JournalIndex(l => l.StartsWith("mailcast: rig on 7.052 MHz", StringComparison.Ordinal)));
        File.Exists(hooks.NotePath).Should().BeTrue();

        await AdvanceTo(Slot + TimeSpan.FromMinutes(12));
        await Patiently(() => Scripts.Calls.Count == 2 && !File.Exists(hooks.NotePath), "\"after\" ran at 12:12");
        Scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 1 7052.0 7053.8");
        _atHookStart.Last().Should().Be((7_049_450L, false), "\"after\" ran with the rig back and the station free to transmit");
        await Patiently(() => retuner.State.StartsWith("waiting for the 13:00 UTC slot", StringComparison.Ordinal), "waiting for the next");
    }

    [Fact]
    public async Task A_Failed_Before_Means_No_Retune_And_Nothing_Held_For_That_Slot_But_After_Still_Runs()
    {
        RigControl rig = await StartedRig();
        MailcastHooks hooks = Hooks(rig, Scripts.Hook("before-fails", exit: 3), Scripts.Hook("after"));
        (MailcastRetuner retuner, _) = RetunerWith(rig, hooks, _stop.Token);

        await AdvanceTo(Slot + TimeSpan.FromMinutes(5));
        await Patiently(() => retuner.State.StartsWith("not retuning for the 12:00 UTC slot: the \"before\" command failed", StringComparison.Ordinal), "not retuning");
        _fake.Sets.Should().NotContain("F 7052000");
        rig.HoldsTransmitter.Should().BeFalse();
        retuner.Listening.Should().BeFalse();
        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: WARNING - \"before\" for the 12:00 UTC slot exited with 3", StringComparison.Ordinal)
            && line.EndsWith("so the rig is not retuned and nothing is held for that slot, as whatever it was to stop may still be transmitting", StringComparison.Ordinal));

        await AdvanceTo(Slot + TimeSpan.FromMinutes(12));
        await Patiently(() => Scripts.Calls.Count == 2, "\"after\" ran at 12:12");
        Scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 0 7052.0 7053.8");
        _fake.Sets.Should().NotContain("F 7052000");
    }

    [Fact]
    public async Task A_Station_Stopped_Mid_Window_Puts_The_Rig_Back_Before_It_Runs_After()
    {
        RigControl rig = await StartedRig();
        MailcastHooks hooks = Hooks(rig, Scripts.Hook("before"), Scripts.Hook("after"));
        using var stop = new CancellationTokenSource();
        (MailcastRetuner retuner, Task running) = RetunerWith(rig, hooks, stop.Token);
        await AdvanceTo(Slot - TimeSpan.FromMinutes(1));
        await Patiently(() => retuner.Listening && _fake.DialHz == 7_052_000, "the window is open");

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(60));

        Scripts.Calls.Should().HaveCount(2);
        Scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 1 7052.0 7053.8");
        _atHookStart.Last().Should().Be((7_049_450L, false), "the rig was put back first");
        File.Exists(hooks.NotePath).Should().BeFalse();
        _journal.Should().Contain(line => line.StartsWith("mailcast: hooks: running \"after\" for the 12:00 UTC slot as the station stops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Station_Restarted_Mid_Window_After_Its_Before_Puts_The_Rig_Back_Runs_After_And_Leaves_That_Slot_Alone()
    {
        // The last run started "before" for noon, retuned, and was killed at 12:04.
        using (var lastRun = new MailcastHooks(
            new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "x" }, Hooks = new MailcastHooksConfig { Before = Scripts.Hook("before") } },
            HooksDir, _time, _ => { }))
        {
            (await lastRun.BeforeAsync(Slot, this, "", CancellationToken.None)).Should().Be(MailcastBeforeOutcome.Ok);
        }

        RigRestoreFile.Write(RestorePath, _fake.Endpoint, Plan);
        _fake.DialHz = 7_052_000;
        _fake.Mode = "USB";
        _fake.PassbandHz = 3000;
        _time.SetUtcNow(Slot + TimeSpan.FromMinutes(5));

        RigControl rig = await StartedRig();
        MailcastHooks hooks = Hooks(rig, Scripts.Hook("before"), Scripts.Hook("after"));
        (MailcastRetuner retuner, _) = RetunerWith(rig, hooks, _stop.Token);

        await Patiently(() => Scripts.Calls.Count == 2 && !File.Exists(hooks.NotePath), "\"after\" ran at the start");
        Scripts.Calls[1].Should().Be("after 2026-10-05T12:00:00Z 1 7052.0 7053.8");
        _atHookStart.Single().Should().Be((7_049_450L, false), "the rig was put back first");
        await Patiently(() => retuner.State.StartsWith("waiting for the 13:00 UTC slot", StringComparison.Ordinal), "the rest of the noon slot is left alone");
        _fake.Sets.Should().NotContain("F 7052000");
        rig.HoldsTransmitter.Should().BeFalse();
    }

    [Fact]
    public async Task After_Waits_While_The_Rig_Is_Owed_Its_Restore()
    {
        RigControl rig = await StartedRig();
        MailcastHooks hooks = Hooks(rig, Scripts.Hook("before"), Scripts.Hook("after"));
        (MailcastRetuner retuner, _) = RetunerWith(rig, hooks, _stop.Token);
        await AdvanceTo(Slot + TimeSpan.FromMinutes(11));
        await Patiently(() => retuner.Listening, "the window is open");

        // The rig will not go back at 12:12.
        _fake.RefusesFrequency = true;
        await AdvanceTo(Slot + TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(30));
        await Patiently(() => retuner.State.StartsWith("waiting for the rig to be put back", StringComparison.Ordinal), "\"after\" waits");
        rig.RestorePending.Should().BeTrue();
        Scripts.Calls.Should().HaveCount(1, "\"after\" has not run with the rig still on the mailcast dial");

        _fake.RefusesFrequency = false;
        await AdvanceTo(_time.GetUtcNow() + TimeSpan.FromMinutes(1));
        await Patiently(() => Scripts.Calls.Count == 2, "\"after\" ran once the rig was back");
        _atHookStart.Last().Should().Be((7_049_450L, false));
        _journal.Should().ContainSingle(line => line == "mailcast: hooks: waiting for the rig to be put back before running \"after\"");
    }

    [Fact]
    public async Task An_Owed_After_Waits_While_Someone_Elses_Window_Has_The_Rig()
    {
        // A note from a run killed mid-window, and the API holding the rig when this one starts.
        using (var lastRun = new MailcastHooks(
            new MailcastConfig { Bbs = new MailcastBbsConfig { Password = "x" }, Hooks = new MailcastHooksConfig { Before = Scripts.Hook("before") } },
            HooksDir, _time, _ => { }))
        {
            (await lastRun.BeforeAsync(Slot, this, "", CancellationToken.None)).Should().Be(MailcastBeforeOutcome.Ok);
        }

        _time.SetUtcNow(Slot + TimeSpan.FromMinutes(5));
        RigControl rig = await StartedRig();
        rig.Tune(new RigTuning(7_074_000, "USB", 0), TimeSpan.FromMinutes(4), "the API").Granted.Should().BeTrue();
        rig.Snapshot().RestoreOwed.Should().BeNull("the snapshot hides a restore behind an open window, which is why it is not what is asked");
        MailcastHooks hooks = Hooks(rig, Scripts.Hook("before"), Scripts.Hook("after"));
        (MailcastRetuner retuner, _) = RetunerWith(rig, hooks, _stop.Token);

        await Patiently(() => retuner.State.StartsWith("waiting for the rig to be put back", StringComparison.Ordinal), "\"after\" waits");
        await AdvanceTo(_time.GetUtcNow() + TimeSpan.FromMinutes(1));
        Scripts.Calls.Should().HaveCount(1, "\"after\" has not run with the rig on the API's frequency");

        rig.Release("the API").Should().BeTrue();
        await AdvanceTo(_time.GetUtcNow() + MailcastRetuner.RetryEvery);
        await Patiently(() => Scripts.Calls.Count == 2, "\"after\" ran once the API let go");
        _atHookStart.Single().Should().Be((7_049_450L, false));
    }
}
