using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The configuration page in a real browser, against a real daemon, checked against the real
/// config file on disk.
/// </summary>
/// <remarks>
/// <para>ConfigPageTests runs the page's form model and the API without a browser. What only this
/// can catch is everything between them: a field that does not fire its handler, a button wired
/// to the wrong call, the login, the wait across the restart, a page that saves and then says it
/// did not. Each test drives the page the way an operator would and then reads the file.</para>
/// <para><b>Written not to flake.</b> These run on shared, loaded runners, so:</para>
/// <list type="bullet">
/// <item>Nothing sleeps for a guessed time. Every wait is for a condition: the daemon answering,
/// a field showing a value, the page saying the station is back. Playwright's assertions retry
/// until they hold or their timeout runs out, and the timeouts are minutes, not seconds, because a
/// slow runner is a reason to wait longer, never a reason to fail.</item>
/// <item>The restart is detected the way the page detects it, by a different process answering,
/// not by timing; and the test then counts the daemon's starts itself rather than trusting the
/// page.</item>
/// <item>Every test has its own daemon, config file, state directory, ports (from
/// <see cref="FreePorts"/>, outside the range anything else on the box is handed) and browser
/// context, so no test can see another's leftovers, and none depends on order.</item>
/// <item>Elements are found by id, not by wording or position.</item>
/// <item>A failure leaves the daemon's log, the config file and a screenshot in its scratch
/// directory and prints the log, so a failure on CI can be read rather than re-run.</item>
/// </list>
/// <para>The browser is installed by CI before the suite (scripts/install-playwright-browser.cs).
/// Where none is installed the tests skip and say how to install one, except on CI, where a
/// missing browser is a broken job and fails.</para>
/// </remarks>
[Trait("Category", "Browser")]
public sealed class ConfigPageBrowserTests : IClassFixture<ConfigPageBrowserTests.Browser>, IAsyncLifetime
{
    /// <summary>How long a restart may take on a loaded runner before it is a failure. The page
    /// itself gives up at two minutes, so this is a little over that.</summary>
    private const float RestartMs = 150_000;

    /// <summary>The wait for anything else: a field, a message, a page load.</summary>
    private const float StepMs = 30_000;

    private const string Key = "the-station-key";

    private readonly Browser _browser;
    private readonly ScratchDirectory _scratch = new("config-page-browser-tests");
    private Station? _station;
    private IBrowserContext? _context;

    public ConfigPageBrowserTests(Browser browser) => _browser = browser;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Skips the test where there is no browser (never on CI; see the fixture).</summary>
    private void RequireBrowser() =>
        Assert.SkipWhen(_browser.Unavailable is not null, _browser.Unavailable ?? "");

    public async ValueTask DisposeAsync()
    {
        bool failed = TestContext.Current.TestState?.Result is not TestResult.Passed and not TestResult.Skipped;
        if (failed && _context is not null)
        {
            foreach (IPage page in _context.Pages)
            {
                try
                {
                    await page.ScreenshotAsync(new() { Path = Path.Combine(_scratch.FullName, "page.png"), FullPage = true });
                }
                catch (PlaywrightException)
                {
                    // The page went with whatever failed; the log below still says what happened.
                }
            }
        }

        // Each stage runs whatever the one before it did: a browser that crashed (the likeliest
        // failure) must not leave a daemon running or the scratch directory behind.
        try
        {
            if (_context is not null)
            {
                await _context.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
            // Closed already, with the browser.
        }
        finally
        {
            try
            {
                if (_station is not null)
                {
                    await _station.DisposeAsync();
                    if (failed)
                    {
                        File.WriteAllText(Path.Combine(_scratch.FullName, "daemon.log"), _station.Log);
                        TestContext.Current.TestOutputHelper?.WriteLine(
                            $"daemon log ({_scratch.FullName}):\n{_station.Log}");
                    }
                }
            }
            finally
            {
                _scratch.Dispose();
            }
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task Saving_From_The_Page_Writes_The_File_And_Keeps_What_The_Page_Never_Showed()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, Key);
        IPage page = await NewPageAsync();

        // In through the station page's own link, as an operator would.
        await page.GotoAsync(_station.Url(""));
        await Assertions.Expect(page.Locator("#configLink")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await page.Locator("#configLink").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(_station.Url("config"), new() { Timeout = StepMs });

        // A wrong key is refused and says so; the right one opens the form.
        await Assertions.Expect(page.Locator("#login")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await page.Locator("#key").FillAsync("not-the-key");
        await page.Locator("#login button").ClickAsync();
        await Assertions.Expect(page.Locator("#login-error")).ToContainTextAsync("not this station's key", new() { Timeout = StepMs });
        await page.Locator("#key").FillAsync(Key);
        await page.Locator("#login button").ClickAsync();
        await Assertions.Expect(page.Locator("#form-view")).ToBeVisibleAsync(new() { Timeout = StepMs });

        // The secret is not on the page in any form.
        (await page.ContentAsync()).Should().NotContain(Key);
        await Assertions.Expect(page.Locator("#f-api_key")).ToHaveValueAsync("", new() { Timeout = StepMs });

        await OpenSectionAsync(page, "modems");
        await OpenSectionAsync(page, "waterfall");
        await ReplaceAsync(page, "#f-modems_0__frequency", "1600");
        await ReplaceAsync(page, "#f-waterfall_title", "after");
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("2 changes", new() { Timeout = StepMs });

        await SaveAsync(page);

        JsonNode file = _station.ReadConfig();
        file["modems"]![0]!["frequency"]!.GetValue<double>().Should().Be(1600);
        file["waterfall"]!["title"]!.GetValue<string>().Should().Be("after");
        file["api"]!["key"]!.GetValue<string>().Should().Be(Key, "the page never had the key, and saving must not lose it");
        file["notASettingYet"]!.GetValue<int>().Should().Be(7, "a key this version does not know is kept");
        file["modems"]![0]!["mode"]!.GetValue<string>().Should().Be("bpsk300", "what was not edited is as it was");
        _station.Starts.Should().Be(2, "one save, one restart");
        File.Exists(_station.PendingPath).Should().BeFalse("a save writes the file, not a one-run change");

        // A fresh load reads the restarted station, so what it shows came from the daemon and not
        // from what was typed. (The tab keeps the key for the session, so no login this time.)
        await ReloadFormAsync(page);
        await OpenSectionAsync(page, "waterfall");
        await Assertions.Expect(page.Locator("#f-waterfall_title")).ToHaveValueAsync("after", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#source")).ToHaveTextAsync(_station.ConfigPath, new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task Trying_A_Change_Runs_It_Once_And_Leaves_The_File_Alone()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        string before = File.ReadAllText(_station.ConfigPath);
        IPage page = await OpenConfigAsync();

        await OpenSectionAsync(page, "waterfall");
        await ReplaceAsync(page, "#f-waterfall_title", "just this once");
        await page.Locator("#try").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync(
            "The station is back", new() { Timeout = RestartMs });

        await _station.WaitForStartsAsync(2);
        File.ReadAllText(_station.ConfigPath).Should().Be(before, "a one-run change never touches the file");
        _station.Starts.Should().Be(2);
        File.Exists(_station.PendingPath).Should().BeFalse("the restart took the one-run change and removed it");

        // Read afresh from the restarted station: it is running the change, and says it is.
        await ReloadFormAsync(page);
        await OpenSectionAsync(page, "waterfall");
        await Assertions.Expect(page.Locator("#source")).ToContainTextAsync("one-run", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#f-waterfall_title")).ToHaveValueAsync("just this once", new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Configuration_The_Station_Would_Refuse_Changes_Nothing()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        string before = File.ReadAllText(_station.ConfigPath);
        IPage page = await OpenConfigAsync();

        // The 2026-08-15 outage: a trailing space in a mode name.
        await OpenSectionAsync(page, "modems");
        await ReplaceAsync(page, "#f-modems_0__mode", "bpsk300 ");
        await page.Locator("#check").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync("unknown mode", new() { Timeout = StepMs });

        // The save has to reach the station and be declined there: its own journal line says so.
        // (The page's message would read the same as the check's, so it cannot prove the save
        // was sent at all.)
        page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        await page.Locator("#save").ClickAsync();
        await _station.WaitForLogAsync("api: configuration declined, the station is unchanged");
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync("unknown mode", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#save")).ToBeEnabledAsync(new() { Timeout = StepMs });

        File.ReadAllText(_station.ConfigPath).Should().Be(before);
        File.Exists(_station.PendingPath).Should().BeFalse();
        _station.Starts.Should().Be(1, "a refused configuration never restarts the station");

        // And text that is not a number never reaches the station at all.
        await ReplaceAsync(page, "#f-modems_0__mode", "bpsk300");
        await ReplaceAsync(page, "#f-kissPort", "eighty");
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("needs fixing", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#save")).ToBeDisabledAsync(new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Section_Added_On_The_Form_And_An_Edit_Pasted_As_Json_Are_Both_Saved()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        string frameLog = Path.Combine(_scratch.FullName, "frames.db");
        IPage page = await OpenConfigAsync();

        // A section the file does not have, added with its button.
        await OpenSectionAsync(page, "frameLog");
        await page.Locator("details.section[data-path='frameLog'] button.act").First.ClickAsync();
        await ReplaceAsync(page, "#f-frameLog_path", frameLog);

        // The JSON tab shows it, and text pasted there (comments and all) is what gets saved.
        await page.Locator("#tab-json").ClickAsync();
        await Assertions.Expect(page.Locator("#json")).ToHaveValueAsync(
            new System.Text.RegularExpressions.Regex("frameLog"), new() { Timeout = StepMs });
        JsonNode edited = JsonNode.Parse(await page.Locator("#json").InputValueAsync())!;
        edited["waterfall"]!["title"] = "pasted";
        await page.Locator("#json").FillAsync("// pasted from a notebook\n" + edited.ToJsonString() + "\n");
        await page.Locator("#tab-form").ClickAsync();
        await OpenSectionAsync(page, "waterfall");
        await Assertions.Expect(page.Locator("#f-waterfall_title")).ToHaveValueAsync("pasted", new() { Timeout = StepMs });

        await SaveAsync(page);

        JsonNode file = _station.ReadConfig();
        file["frameLog"]!["path"]!.GetValue<string>().Should().Be(frameLog);
        file["waterfall"]!["title"]!.GetValue<string>().Should().Be("pasted");
        _station.Starts.Should().Be(2);
    }

    // ---------------------------------------------------------------- steps

    private async Task<IPage> NewPageAsync()
    {
        _context = await _browser.Instance!.NewContextAsync();
        _context.SetDefaultTimeout(StepMs);
        _context.SetDefaultNavigationTimeout(StepMs);
        return await _context.NewPageAsync();
    }

    /// <summary>The configuration page of a station with no key, form showing.</summary>
    private async Task<IPage> OpenConfigAsync()
    {
        IPage page = await NewPageAsync();
        await page.GotoAsync(_station!.Url("config"));
        await Assertions.Expect(page.Locator("#form-view")).ToBeVisibleAsync(new() { Timeout = StepMs });
        return page;
    }

    /// <summary>Loads the page again and waits for its form, with no load error showing.</summary>
    private static async Task ReloadFormAsync(IPage page)
    {
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("#form-view")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#loading")).ToBeHiddenAsync(new() { Timeout = StepMs });
    }

    private static async Task OpenSectionAsync(IPage page, string name)
    {
        ILocator section = page.Locator($"details.section[data-path='{name}']");
        if (await section.GetAttributeAsync("open") is null)
        {
            await section.Locator("summary").ClickAsync();
        }

        await Assertions.Expect(section).ToHaveAttributeAsync("open", "", new() { Timeout = StepMs });
    }

    /// <summary>Types a field's new text and waits until the page has taken it.</summary>
    private static async Task ReplaceAsync(IPage page, string selector, string text)
    {
        ILocator field = page.Locator(selector);
        await field.FillAsync(text);
        await Assertions.Expect(field).ToHaveValueAsync(text, new() { Timeout = StepMs });
    }

    /// <summary>Save to file, agree to the question, and wait for the restarted station.</summary>
    private async Task SaveAsync(IPage page)
    {
        page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        await page.Locator("#save").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync(
            "The station is back", new() { Timeout = RestartMs });
        await _station!.WaitForStartsAsync(2);
    }

    // ---------------------------------------------------------------- the browser

    /// <summary>One headless Chromium for the class; each test has a context of its own.</summary>
    public sealed class Browser : IAsyncLifetime
    {
        public IPlaywright? Playwright { get; private set; }

        public IBrowser? Instance { get; private set; }

        /// <summary>Why the tests cannot run here, or null when they can.</summary>
        public string? Unavailable { get; private set; }

        public async ValueTask InitializeAsync()
        {
            try
            {
                Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                // Libraries CI fetched for a runner that lacks some (scripts/browser-libs.sh), for
                // the browser alone: the daemon and the rest of the suite never see them.
                var launch = new BrowserTypeLaunchOptions { Headless = true };
                if (Environment.GetEnvironmentVariable("PDN_BROWSER_LIBRARY_PATH") is { Length: > 0 } libs)
                {
                    // Env replaces the browser's whole environment, so it is this process's
                    // environment with the one variable added, not that variable alone.
                    var env = new Dictionary<string, string>();
                    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    {
                        env[(string)entry.Key] = (string?)entry.Value ?? "";
                    }

                    string? existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                    env["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? libs : $"{libs}:{existing}";
                    launch.Env = env;
                }

                Instance = await Playwright.Chromium.LaunchAsync(launch);
            }
            catch (PlaywrightException e)
            {
                const string how = "install one with: dotnet run scripts/install-playwright-browser.cs";
                if (Environment.GetEnvironmentVariable("CI") == "true")
                {
                    throw new InvalidOperationException(
                        "CI must run the browser tests, and Chromium would not start. The workflow's "
                        + "install steps should have put it in $PLAYWRIGHT_BROWSERS_PATH and any libraries "
                        + $"it lacks in $PDN_BROWSER_LIBRARY_PATH. Playwright said:\n{e.Message}", e);
                }

                Unavailable = $"no browser for Playwright here ({e.Message.Split('\n')[0]}); {how}";
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Instance is not null)
            {
                await Instance.CloseAsync();
            }

            Playwright?.Dispose();
        }
    }

    // ---------------------------------------------------------------- the station

    /// <summary>
    /// The real daemon on a config file of its own, kept running across the restarts the page
    /// asks for, as systemd's <c>Restart=on-failure</c> does.
    /// </summary>
    private sealed class Station : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly int _port;
        private readonly StringBuilder _log = new();
        private readonly CancellationTokenSource _stop = new();
        private Process? _process;
        private int _starts;
        private int _lastExit = int.MinValue;
        private Task _supervising = Task.CompletedTask;

        private Station(string dir, int port, string configPath)
        {
            _dir = dir;
            _port = port;
            ConfigPath = configPath;
        }

        public string ConfigPath { get; }

        public string StateDirectory => Path.Combine(_dir, "state");

        /// <summary>Where the daemon leaves a one-run change: its state directory, as the shipped
        /// unit's <c>$STATE_DIRECTORY</c> makes it.</summary>
        public string PendingPath => Path.Combine(StateDirectory, "pending-config.json");

        /// <summary>How many times the daemon has been started, the first included.</summary>
        public int Starts => Volatile.Read(ref _starts);

        public string Log
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        public string Url(string relative) => $"http://127.0.0.1:{_port}/{relative}";

        public JsonNode ReadConfig() => JsonNode.Parse(File.ReadAllText(ConfigPath), null,
            new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

        public static async Task<Station> StartAsync(string dir, string? key)
        {
            int port = FreePorts.Next();
            int kiss = FreePorts.Next();
            string configPath = Path.Combine(dir, "soundmodem.json");
            string api = key is null ? "" : $",\n  \"api\": {{ \"key\": \"{key}\" }}";
            // A pipe that loops back to itself, not ALSA's "null": the daemon makes the FIFO and
            // paces it to the clock, and nothing needs libasound, which a bare CI runner may not
            // have (proxmox0's did not, 2026-11-06, and every start aborted).
            File.WriteAllText(configPath, $$"""
                {
                  // the station's own file, comments and all, as an operator keeps it
                  "device": "pipe:{{Path.Combine(dir, "air")}},{{Path.Combine(dir, "air")}}",
                  "bind": "127.0.0.1",
                  "kissPort": {{kiss}},
                  "waterfall": { "port": {{port}}, "title": "before" },
                  "modems": [ { "subChannel": 0, "mode": "bpsk300", "frequency": 1500 } ],
                  "notASettingYet": 7{{api}}
                }
                """);

            var station = new Station(dir, port, configPath);
            Directory.CreateDirectory(station.StateDirectory);
            station._supervising = station.SuperviseAsync();
            try
            {
                await station.WaitForStartsAsync(1);
            }
            catch
            {
                // The test never gets the station to dispose, so it is stopped here: otherwise
                // the supervisor would go on restarting it after the test had failed.
                await station.DisposeAsync();
                throw;
            }

            return station;
        }

        /// <summary>
        /// Waits until the daemon has been started <paramref name="count"/> times and the latest
        /// one answers. Fails, with the log, if it stops for good instead.
        /// </summary>
        public async Task WaitForStartsAsync(int count)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                if (_supervising.IsCompleted)
                {
                    throw new InvalidOperationException(
                        $"the daemon stopped for good (exit {_lastExit}) instead of coming back. "
                        + $"Its last lines:\n{Tail()}\nThe whole log:\n{Log}");
                }

                if (Starts >= count)
                {
                    try
                    {
                        using HttpResponseMessage answer = await client.GetAsync(new Uri(Url("config")));
                        if (answer.StatusCode == HttpStatusCode.OK)
                        {
                            return;
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                    {
                        // Not listening yet: start-up, or the old process going away.
                    }
                }

                await Task.Delay(200);
            }

            throw new TimeoutException(
                $"the daemon was not answering after {count} start(s). Its last lines:\n{Tail()}\nThe whole log:\n{Log}");
        }

        /// <summary>The last lines of the log, first in a failure message: CI's summary of a
        /// failure shows its first few lines and no more.</summary>
        private string Tail() => string.Join('\n', Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(8));

        /// <summary>Waits for a line in the daemon's output; fails, with the log, if it never comes.</summary>
        public async Task WaitForLogAsync(string text)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            while (!Log.Contains(text, StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"the daemon never said \"{text}\":\n{Log}");
                }

                await Task.Delay(100);
            }
        }

        /// <summary>Runs the daemon, and runs it again whenever it exits 1, the restart it asks
        /// systemd for after an applied change. Any other exit ends it.</summary>
        private async Task SuperviseAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var info = new ProcessStartInfo(UsageTests.DotnetHost())
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pdn-soundmodem.dll"));
                info.ArgumentList.Add("--config");
                info.ArgumentList.Add(ConfigPath);
                info.Environment["STATE_DIRECTORY"] = StateDirectory;
                // As under systemd, so the daemon does not warn that nothing will restart it.
                info.Environment["INVOCATION_ID"] = "config-page-browser-tests";

                var process = new Process { StartInfo = info };
                process.OutputDataReceived += (_, e) => Append(e.Data);
                process.ErrorDataReceived += (_, e) => Append(e.Data);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
                Interlocked.Increment(ref _starts);
                Append($"--- start {Starts}, pid {process.Id}");

                try
                {
                    await process.WaitForExitAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stopped, possibly after DisposeAsync looked at _process and before this one
                    // was put there, so this daemon is killed here as well as there.
                    Kill(process);
                    return;
                }

                _lastExit = process.ExitCode;
                Append($"--- exit {process.ExitCode}");
                if (_lastExit != 1)
                {
                    return;
                }

                // systemd's RestartSec, short: long enough for the old listener to be gone.
                await Task.Delay(500);
            }
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_log)
            {
                _log.AppendLine(line);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_process is { } running)
            {
                Kill(running);
            }

            try
            {
                await _supervising.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception e)
            {
                // Never thrown from here: this runs while some other failure is being reported,
                // and that one, with the daemon's log, is the one worth reading.
                Append($"--- supervisor: {e.GetType().Name}: {e.Message}");
            }

            _process?.Dispose();
            _stop.Dispose();
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(30_000);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException
                                        or System.ComponentModel.Win32Exception)
            {
                // Already gone, or going.
            }
        }
    }
}
