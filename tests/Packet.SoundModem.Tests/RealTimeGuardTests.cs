using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// Source-text guard for the clock epic (#860, slice #873): production code and tests may not read
/// or wait on real time except where an allow-list entry says why. An entry whose file is gone, or
/// whose file no longer uses the token, fails the test, so the list can only shrink.
/// </summary>
/// <remarks>
/// <para>
/// <c>src</c> forbids: wall-clock reads, <c>Stopwatch</c>, <c>Environment.TickCount</c>,
/// <c>Thread.Sleep</c>, timers and <c>Task.Delay</c> made without a <c>TimeProvider</c>,
/// <c>CancelAfter</c> and timed <c>CancellationTokenSource</c> without one, and timed waits and
/// joins (<c>WaitAsync</c>, <c>Monitor.Wait</c>, <c>WaitOne</c>, <c>Wait</c>, <c>Join</c>, socket
/// timeouts, <c>SpinUntil</c>).
/// </para>
/// <para>
/// <c>tests</c> forbids the first four plus <c>Task.Delay</c> without a provider. The other timed
/// waits (<c>WaitAsync</c>, <c>Monitor.Wait</c>, <c>Join</c>, timed <c>CancellationTokenSource</c>
/// and so on) are not banned in tests yet: 90 or more uses, mostly hang guards, which join the
/// rule once #871 and #872 have moved the real-pacing tests.
/// </para>
/// </remarks>
public class RealTimeGuardTests
{
    private static AllowEntry Allow(string file, string token, string reason) => new(file, token, reason);

    private static readonly AllowEntry[] SrcAllowed =
    [
        Allow("src/Packet.SoundModem.Daemon/HardwareProbes.cs", "CancelAfter",
            "700 ms TCP-connect probe and serial listen window against real hardware"),
        Allow("src/Packet.SoundModem.Daemon/IqPipe/IqFeeds.cs", "Join(timeout)",
            "bounded joins of a real stderr thread and a 50 ms handshake with a child process"),
        Allow("src/Packet.SoundModem.Daemon/IqPipe/IqFeeds.cs", "Wait(timeout)",
            "bounded joins of a real stderr thread and a 50 ms handshake with a child process"),
        Allow("src/Packet.SoundModem.Daemon/Program.cs", "Join(timeout)",
            "bounded join of the listener thread on shutdown"),
        Allow("src/Packet.SoundModem.Daemon/Pacer.cs", "Environment.TickCount",
            "the real pacer for a file or pipe replayed as a capture device; the device clock is the point"),
        Allow("src/Packet.SoundModem.Daemon/Pacer.cs", "Thread.Sleep",
            "the real pacer for a file or pipe replayed as a capture device; the device clock is the point"),
        Allow("src/Packet.SoundModem.Daemon/ProspectorWorker.cs", "Join(timeout)",
            "bounded join of a real worker thread on shutdown"),
        Allow("src/Packet.SoundModem.Daemon/ShutdownDeadline.cs", "Join(timeout)",
            "bounded join of the last-unkey thread on a failure path"),
        Allow("src/Packet.SoundModem.Daemon/TxLeaseTuning.cs", "Thread.Sleep",
            "default pause is a real sleep; tests replace it"),
        Allow("src/Packet.SoundModem.Daemon/UplinkAudioInput.cs", "Wait(timeout)",
            "audio-thread pull wake-up on a real signal"),
        Allow("src/Packet.SoundModem.Windows/WasapiAudioInput.cs", "Join(timeout)",
            "wake-ups on the device's own event and bounded joins of the capture thread"),
        Allow("src/Packet.SoundModem.Windows/WasapiAudioInput.cs", "Monitor.Wait",
            "wake-ups on the device's own event and bounded joins of the capture thread"),
        Allow("src/Packet.SoundModem.Windows/WasapiAudioInput.cs", "WaitOne",
            "wake-ups on the device's own event and bounded joins of the capture thread"),
        Allow("src/Packet.SoundModem.Windows/WasapiAudioOutput.cs", "Join(timeout)",
            "wake-ups on the device's own event and bounded joins of the render thread"),
        Allow("src/Packet.SoundModem.Windows/WasapiAudioOutput.cs", "Monitor.Wait",
            "wake-ups on the device's own event and bounded joins of the render thread"),
        Allow("src/Packet.SoundModem.Windows/WasapiAudioOutput.cs", "WaitOne",
            "wake-ups on the device's own event and bounded joins of the render thread"),
        Allow("src/Packet.SoundModem/Audio/AlsaPcm.cs", "Thread.Sleep",
            "ALSA pause primitive; the sound card is the clock"),
        Allow("src/Packet.SoundModem/FlexRadio/BoundedFlexPtt.cs", "Wait(timeout)",
            "default poll of a real PTT call; tests inject their own wait"),
        Allow("src/Packet.SoundModem/FlexRadio/FlexDevice.cs", "WaitAsync",
            "bounded teardown wait on a real radio session"),
        Allow("src/Packet.SoundModem/Iq/IqReceiver.cs", "Join(timeout)",
            "bounded join of the receive thread on close"),
        Allow("src/Packet.SoundModem/Iq/IqReceiverChannel.cs", "Monitor.Wait",
            "read wake-up on a real signal from the receive thread"),
        Allow("src/Packet.SoundModem/Iq/SoundCardIqSource.cs", "Wait(timeout)",
            "bounded wait for the sound-card thread to go idle on close"),
        Allow("src/Packet.SoundModem/OpenWebRx/OpenWebRxAudioInput.cs", "Monitor.Wait",
            "wake-up for the stop flag and a bounded pump join"),
        Allow("src/Packet.SoundModem/OpenWebRx/OpenWebRxAudioInput.cs", "Wait(timeout)",
            "wake-up for the stop flag and a bounded pump join"),
        Allow("src/Packet.SoundModem/Rig/RigctldConnection.cs", "socket timeout",
            "the network stream's own read and write timeouts"),
        Allow("src/Packet.SoundModem/RtlTcp/RtlTcpIqSource.cs", "socket timeout",
            "the socket's own receive timeout"),
        Allow("src/Packet.SoundModem/SdrConnect/SdrConnectAudioInput.cs", "Monitor.Wait",
            "wake-up on a real signal from the pump"),
        Allow("src/Packet.SoundModem/SdrConnect/SdrConnectClient.cs", "Wait(timeout)",
            "bounded join of the supervisor task on dispose"),
        Allow("src/Packet.SoundModem/SdrConnect/SdrConnectIqSource.cs", "Monitor.Wait",
            "wake-ups for the stop flag"),
        Allow("src/Packet.SoundModem/Survey/BurstCaptureWriter.cs", "Wait(timeout)",
            "bounded join of the writer task at dispose"),
        Allow("src/Packet.SoundModem/UberSdr/OnDemandUberSdrInput.cs", "Monitor.Wait",
            "wake-up for the stop flag"),
        Allow("src/Packet.SoundModem/UberSdr/UberSdrAudioInput.cs", "Monitor.Wait",
            "wake-up for the stop flag and a bounded pump join"),
        Allow("src/Packet.SoundModem/UberSdr/UberSdrAudioInput.cs", "Wait(timeout)",
            "wake-up for the stop flag and a bounded pump join"),
        Allow("src/Packet.SoundModem/Waterfall/UplinkClient.cs", "Task.Delay",
            "goodbye safety net is deliberately real: a fake clock would never fire it"),
    ];

    private static readonly AllowEntry[] TestsAllowed =
    [
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopArdopcfLiveSessionTests.cs", "Stopwatch",
            "live session against a real ardopcf process on a real audio loopback; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopArdopcfLiveSessionTests.cs", "Thread.Sleep",
            "live session against a real ardopcf process on a real audio loopback; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopHostLiveTests.cs", "Stopwatch",
            "live session against a real ardopcf process on a real audio loopback; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopHostLiveTests.cs", "Thread.Sleep",
            "live session against a real ardopcf process on a real audio loopback; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopHostLiveTests.cs", "wall-clock",
            "live session against a real ardopcf process on a real audio loopback; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopSharedChannelSessionTests.cs", "Stopwatch",
            "paces or waits on real audio; removed by the real-audio-pacing design (#872)"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopSharedChannelSessionTests.cs", "Thread.Sleep",
            "paces or waits on real audio; removed by the real-audio-pacing design (#872)"),
        Allow("tests/Packet.SoundModem.Tests/Channel/ReceiveLevelSweepProbe.cs", "Stopwatch",
            "manual probe (RX_LEVELS=1); the elapsed marker in its report is for the person running it by hand"),
        Allow("tests/Packet.SoundModem.Tests/Eventually.cs", "Stopwatch",
            "the shared polling helper with a real guard; to move to a fake-clock helper under #860"),
        Allow("tests/Packet.SoundModem.Tests/Eventually.cs", "Thread.Sleep",
            "the shared polling helper with a real guard; to move to a fake-clock helper under #860"),
        Allow("tests/Packet.SoundModem.Tests/Iq/IqReceiverCpuTests.cs", "Stopwatch",
            "manual bench (IQ_BENCH=1) that prints measured speed for the person running it; nothing asserts on it"),
        Allow("tests/Packet.SoundModem.Tests/Modems/FrequencyShiftedModemTests.cs", "wall-clock",
            "timestamps the evidence file lines of a gate run by hand (MS110D_SHIFT_GATE_OUT); the time is the record"),
        Allow("tests/Packet.SoundModem.Tests/OpenWebRx/OpenWebRxLiveStreamTests.cs", "wall-clock",
            "live stream from a real OpenWebRX server for a real 12 s; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/UberSdr/UberSdrLiveStreamTests.cs", "Stopwatch",
            "live stream from a real UberSDR receiver for a real duration; the other party runs on the real clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Waterfall/WaterfallPageTests.cs", "Thread.Sleep",
            "feeds audio at the pace a real node browser page plays it: real node process running the page on real time, over a real socket; the page's own audio player and probe are the clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Eventually.cs", "Task.Delay",
            "the polling helper's own pause between checks; to move to a fake-clock helper under #860"),
        Allow("tests/Packet.SoundModem.Tests/Waterfall/WaterfallPageTests.cs", "Task.Delay",
            "winds the server clock while a real node process running the page on real time, over a real socket; the page's own audio player and probe are the clock, so this stays real"),
        Allow("tests/Packet.SoundModem.Tests/Ardop/ArdopSharedChannelSessionTests.cs", "Task.Delay",
            "real audio block pacing and a real 3 s wait; removed by #872"),
    ];

    [Fact]
    public void Production_Code_Uses_Real_Time_Only_Where_The_Allow_List_Says_Why()
    {
        Lines(RealTimeGuard.Check(Read("src"), GuardRoot.Src, SrcAllowed)).Should().BeEmpty(
            "production code takes its time from the injected TimeProvider; allow-list entries are for "
            + "device-clock pacing and bounded joins of real threads, and a stale entry must be deleted");
    }

    [Fact]
    public void Tests_Use_Real_Time_Only_Where_The_Allow_List_Says_Why()
    {
        Lines(RealTimeGuard.Check(Read("tests"), GuardRoot.Tests, TestsAllowed)).Should().BeEmpty(
            "tests drive a FakeTimeProvider and never assert on elapsed real time; allow-list entries "
            + "name the ticket that will remove them, and a stale entry must be deleted");
    }

    [Fact]
    public void Every_Allow_List_Entry_Has_A_Reason_And_Is_Listed_Once()
    {
        AllowEntry[] all = [.. SrcAllowed, .. TestsAllowed];

        all.Where(e => string.IsNullOrWhiteSpace(e.Reason)).Should().BeEmpty("every entry says why");
        all.GroupBy(e => (Root: e.File, e.Token)).Where(g => g.Count() > 1).Should().BeEmpty("one entry per file and token");
    }

    // ---- the checker itself, on small in-memory sources ----

    private static IReadOnlyList<string> CheckSrc(string source, params AllowEntry[] allow) =>
        RealTimeGuard.Check(new Dictionary<string, string> { ["src/A.cs"] = source }, GuardRoot.Src, allow);

    private static IReadOnlyList<string> CheckTests(string source, params AllowEntry[] allow) =>
        RealTimeGuard.Check(new Dictionary<string, string> { ["tests/A.cs"] = source }, GuardRoot.Tests, allow);

    [Theory]
    [InlineData("var t = DateTime.UtcNow;", "wall-clock")]
    [InlineData("var t = DateTimeOffset.Now;", "wall-clock")]
    [InlineData("var s = Stopwatch.StartNew();", "Stopwatch")]
    [InlineData("var t = Environment.TickCount64;", "Environment.TickCount")]
    [InlineData("Thread.Sleep(5);", "Thread.Sleep")]
    public void A_Forbidden_Token_Is_Caught_In_Both_Roots_With_File_Line_And_Advice(string code, string token)
    {
        string source = "class A\n{\n    void M()\n    {\n        " + code + "\n    }\n}\n";

        CheckSrc(source).Should().Equal($"src/A.cs:5: {token}: use the injected TimeProvider");
        CheckTests(source).Should().Equal($"tests/A.cs:5: {token}: use the injected TimeProvider");
    }

    [Theory]
    [InlineData("var t = new PeriodicTimer(period);", "PeriodicTimer")]
    [InlineData("source.CancelAfter(5000);", "CancelAfter")]
    [InlineData("var s = new CancellationTokenSource(1000);", "CancellationTokenSource(span)")]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(5));", "WaitAsync")]
    [InlineData("Monitor.Wait(gate, 100);", "Monitor.Wait")]
    [InlineData("handle.WaitOne(200);", "WaitOne")]
    [InlineData("task.Wait(TimeSpan.FromSeconds(2));", "Wait(timeout)")]
    [InlineData("thread.Join(TimeSpan.FromSeconds(2));", "Join(timeout)")]
    [InlineData("stream.ReadTimeout = 500;", "socket timeout")]
    public void A_Timer_Or_Wait_Without_A_Provider_Is_Caught_In_Src_Only(string code, string token)
    {
        string source = "class A { void M() { " + code + " } }";

        CheckSrc(source).Should().ContainSingle().Which.Should().Contain($": {token}: ");
        CheckTests(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("await Task.Delay(50);")]
    [InlineData("await Task.Delay(50, token);")]
    [InlineData("await Task.Delay(50, TimeProvider.System);")]
    public void A_Delay_Without_A_Provider_Is_Caught_In_Both_Roots(string code)
    {
        string source = "class A { void M() { " + code + " } }";

        CheckSrc(source).Should().ContainSingle().Which.Should().Contain(": Task.Delay: ");
        CheckTests(source).Should().ContainSingle().Which.Should().Contain(": Task.Delay: ");
    }

    [Fact]
    public void A_Raw_String_Is_Skipped_And_Does_Not_Hide_The_Code_After_It()
    {
        string source = "class A { string a = $$\"\"\"\n  Thread.Sleep(1) {{DateTime.UtcNow}} \"quoted\"\n  \"\"\"; void M() { Thread.Sleep(2); } }";

        CheckSrc(source).Should().Equal("src/A.cs:3: Thread.Sleep: use the injected TimeProvider");
    }

    [Theory]
    [InlineData("await Task.Delay(50, time, token);")]
    [InlineData("await Task.Delay(Timeout.Infinite, token);")]
    [InlineData("var s = new CancellationTokenSource(TimeSpan.FromSeconds(1), _time);")]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(5), _time);")]
    [InlineData("await gate.WaitAsync(token);")]
    [InlineData("await gate.WaitAsync();")]
    [InlineData("var s = new CancellationTokenSource(); s.Cancel();")]
    [InlineData("var s = new CancellationTokenSource(span, clock); s.CancelAfter(span);")]
    [InlineData("s.CancelAfter(Timeout.InfiniteTimeSpan);")]
    [InlineData("var t = new PeriodicTimer(period, time);")]
    [InlineData("task.Wait(token);")]
    [InlineData("handle.WaitOne(0);")]
    [InlineData("var j = string.Join(\",\", items) + string.Join(sep, items) + Path.Join(a, b);")]
    [InlineData("var j = String.Join(Environment.NewLine, items);")]
    public void The_Provider_Forms_And_Non_Time_Uses_Pass(string code)
    {
        CheckSrc("class A { void M() { " + code + " } }").Should().BeEmpty();
    }

    [Fact]
    public void A_Token_In_A_Comment_Or_A_String_Is_Not_Caught()
    {
        string[] lines =
        [
            "class A",
            "{",
            "    // Thread.Sleep(5) would be wrong here; so would DateTime.UtcNow",
            "    /* Stopwatch.StartNew()",
            "       Environment.TickCount64 */",
            "    /// <summary>Uses <c>Task.Delay(1)</c> never.</summary>",
            "    string a = \"Thread.Sleep(5) and DateTime.UtcNow\";",
            "    string b = @\"Stopwatch \"\"Environment.TickCount\"\"\";",
            "    char c = '\"';",
            "    string d = $\"{{Thread.Sleep(1)}} text\";",
            "    string e = \"\"\"Stopwatch\"\"\";",
            "}",
        ];
        string source = string.Join("\n", lines);

        CheckSrc(source).Should().BeEmpty();
        CheckTests(source).Should().BeEmpty();
    }

    [Fact]
    public void Code_In_An_Interpolation_Hole_Is_Still_Code()
    {
        CheckSrc("class A { string s = $\"at {DateTime.UtcNow:O}\"; }").Should().ContainSingle()
            .Which.Should().Contain(": wall-clock: ");
    }

    [Fact]
    public void Line_Numbers_Survive_Comments_And_Multiline_Strings()
    {
        const string source = "/* a\nb */\nstring s = @\"x\ny\";\nThread.Sleep(1);\n";

        CheckSrc(source).Should().Equal("src/A.cs:5: Thread.Sleep: use the injected TimeProvider");
    }

    [Fact]
    public void An_Allowed_Entry_Passes_And_Only_For_Its_File_And_Token()
    {
        const string source = "class A { void M() { Thread.Sleep(1); var s = Stopwatch.StartNew(); } }";

        CheckSrc(source, Allow("src/A.cs", "Thread.Sleep", "paces to the device")).Should().ContainSingle()
            .Which.Should().Contain(": Stopwatch: ");
        CheckSrc(source, Allow("src/B.cs", "Thread.Sleep", "other file"), Allow("src/A.cs", "Stopwatch", "x"))
            .Should().Contain(l => l.Contains("src/A.cs:1: Thread.Sleep") )
            .And.Contain(l => l.StartsWith("stale allow-list entry src/B.cs Thread.Sleep"));
    }

    [Fact]
    public void An_Entry_For_A_File_That_Is_Gone_Fails_And_Names_The_Entry()
    {
        CheckSrc("class A { }", Allow("src/Gone.cs", "Thread.Sleep", "was needed once")).Should().Equal(
            "stale allow-list entry src/Gone.cs Thread.Sleep: the file no longer exists; delete the entry");
    }

    [Fact]
    public void An_Entry_For_A_Token_The_File_No_Longer_Uses_Fails_And_Names_The_Entry()
    {
        CheckSrc("class A { void M() { } }", Allow("src/A.cs", "Stopwatch", "was needed once")).Should().Equal(
            "stale allow-list entry src/A.cs Stopwatch: the file no longer uses it; delete the entry");
    }

    [Fact]
    public void A_Token_Only_In_A_Comment_Leaves_Its_Entry_Stale()
    {
        CheckSrc("class A { /* Thread.Sleep(1) */ }", Allow("src/A.cs", "Thread.Sleep", "x")).Should().ContainSingle()
            .Which.Should().StartWith("stale allow-list entry src/A.cs Thread.Sleep");
    }

    /// <summary>One problem per line, so the failure message lists every offender, not just the first.</summary>
    private static string Lines(IReadOnlyList<string> problems) =>
        problems.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, problems);

    // ---- reading the tree ----

    private static Dictionary<string, string> Read(string root)
    {
        string repo = FindRepoRoot();
        char sep = Path.DirectorySeparatorChar;
        var files = new Dictionary<string, string>();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(repo, root), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repo, file);
            string name = Path.GetFileName(file);

            if (relative.Contains($"{sep}bin{sep}") || relative.Contains($"{sep}obj{sep}")
                || name.EndsWith(".g.cs", StringComparison.Ordinal)
                || name.EndsWith(".Designer.cs", StringComparison.Ordinal)
                || name.EndsWith(".generated.cs", StringComparison.Ordinal)
                || name.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal))
            {
                continue;
            }

            files[relative.Replace(sep, '/')] = File.ReadAllText(file);
        }

        return files;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
