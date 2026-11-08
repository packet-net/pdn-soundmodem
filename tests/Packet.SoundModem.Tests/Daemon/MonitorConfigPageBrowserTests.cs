using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Linux.Tests;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The settings page of a monitor site in a real browser, against the real daemon.
/// </summary>
/// <remarks>
/// Written like <see cref="ConfigPageBrowserTests"/>, not to flake: every wait is for a condition,
/// each test has its own daemon, ports and scratch directory, and elements are found by id. The
/// site's directory is an address nothing listens on, so it starts with no network: the picker
/// says it could not reach the directory, which is the right answer and not a failure.
/// </remarks>
[Trait("Category", "Browser")]
public sealed class MonitorConfigPageBrowserTests : IClassFixture<ConfigPageBrowserTests.Browser>, IAsyncLifetime
{
    private const float StepMs = 30_000;
    private const float RestartMs = 125_000;
    private const string Key = "the-site-key";

    private readonly ConfigPageBrowserTests.Browser _browser;
    private readonly ScratchDirectory _scratch = new("monitor-config-page-browser-tests");
    private Site? _site;
    private IBrowserContext? _context;

    public MonitorConfigPageBrowserTests(ConfigPageBrowserTests.Browser browser) => _browser = browser;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        bool failed = TestContext.Current.TestState?.Result is not TestResult.Passed and not TestResult.Skipped;
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
                if (_site is not null)
                {
                    await _site.DisposeAsync();
                    if (failed)
                    {
                        TestContext.Current.TestOutputHelper?.WriteLine($"daemon log:\n{_site.Log}");
                    }
                }
            }
            finally
            {
                _scratch.Dispose();
            }
        }
    }

    private void RequireBrowser() =>
        Assert.SkipWhen(_browser.Unavailable is not null, _browser.Unavailable ?? "");

    [Fact(Timeout = 600_000)]
    public async Task The_Monitor_Page_Loads_Behind_The_Key_And_A_Change_Runs_Once()
    {
        RequireBrowser();
        _site = await Site.StartAsync(_scratch.FullName, Key);

        // The journal says where the page is.
        await _site.WaitForLogAsync("settings page at");

        _context = await _browser.Instance!.NewContextAsync();
        _context.SetDefaultTimeout(StepMs);
        IPage page = await _context.NewPageAsync();
        await page.GotoAsync(_site.Url("config"));

        await Assertions.Expect(page.Locator("#login")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await page.Locator("#key").FillAsync(Key);
        await page.Locator("#login button").ClickAsync();

        // The site's overview, in the site's own words, and none of the station's areas.
        await Assertions.Expect(page.Locator("#site-about")).ToContainTextAsync("This is a monitor site", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#overview-monitor-modems")).ToContainTextAsync("7.050300 MHz", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#nav-radio")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("#nav-monitor-uplinks")).ToBeVisibleAsync();

        // The modems have cards, and the band strip is drawn from the plan the site would make.
        await page.Locator("#nav-monitor-modems").ClickAsync();
        await Assertions.Expect(page.Locator("#f-monitor_modems_0__rfFrequency")).ToHaveValueAsync("7.050300", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#strip-svg .blk[data-sub='0']")).ToBeVisibleAsync(new() { Timeout = StepMs });

        // A change to the page's title, tried for one run.
        await page.Locator("#nav-monitor-site").ClickAsync();
        ILocator title = page.Locator("#f-waterfall_title");
        await title.FillAsync("changed once");
        await Assertions.Expect(page.Locator("#try")).ToBeEnabledAsync(new() { Timeout = StepMs });
        string before = File.ReadAllText(_site.ConfigPath);
        await page.Locator("#try").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync("is back", new() { Timeout = RestartMs });

        await _site.WaitForStartsAsync(2);
        File.ReadAllText(_site.ConfigPath).Should().Be(before, "a one-run change never touches the file");

        // The restarted site is running it, and says so.
        await page.ReloadAsync();
        await page.Locator("#nav-monitor-site").ClickAsync();
        await Assertions.Expect(page.Locator("#f-waterfall_title")).ToHaveValueAsync("changed once", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#source")).ToContainTextAsync("one-run", new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Token_Is_Made_In_The_Page_Shown_Once_And_Only_Its_Hash_Is_Saved()
    {
        RequireBrowser();
        _site = await Site.StartAsync(_scratch.FullName, Key);
        IPage page = await OpenAsync();

        await page.Locator("#nav-monitor-uplinks").ClickAsync();
        await page.Locator("#add-f-monitor_uplinks").ClickAsync();
        await page.Locator("#f-monitor_uplinks_0__callsign").FillAsync("gb7rdg-2");
        await page.Locator("#f-monitor_uplinks_0__callsign").PressAsync("Tab");
        // The page address follows the callsign, in capitals as the command line makes it.
        await Assertions.Expect(page.Locator("#f-monitor_uplinks_0__slug")).ToHaveValueAsync("gb7rdg-2", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#f-monitor_uplinks_0__callsign")).ToHaveValueAsync("GB7RDG-2", new() { Timeout = StepMs });

        await page.Locator("#token-make-f-monitor_uplinks_0_").ClickAsync();
        ILocator shown = page.Locator("#newtoken-text-f-monitor_uplinks_0_");
        await Assertions.Expect(shown).ToBeVisibleAsync(new() { Timeout = StepMs });
        string token = (await shown.TextContentAsync())!;
        token.Should().MatchRegex("^pdnsm_[A-Za-z0-9_-]{43}$");
        await Assertions.Expect(page.Locator("#newtoken-f-monitor_uplinks_0_")).ToContainTextAsync("only time it is shown");

        // The field holds the fingerprint the daemon would compute.
        await Assertions.Expect(page.Locator("#f-monitor_uplinks_0__tokenSha256")).ToHaveValueAsync(
            UplinkToken.Hash(token), new() { Timeout = StepMs });

        await Assertions.Expect(page.Locator("#save")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await page.Locator("#save").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync("is back", new() { Timeout = RestartMs });
        await _site.WaitForStartsAsync(2);

        JsonNode entry = _site.ReadConfig()["monitor"]!["uplinks"]![0]!;
        entry["callsign"]!.GetValue<string>().Should().Be("GB7RDG-2");
        entry["slug"]!.GetValue<string>().Should().Be("gb7rdg-2");
        entry["tokenSha256"]!.GetValue<string>().Should().Be(UplinkToken.Hash(token));
        File.ReadAllText(_site.ConfigPath).Should().NotContain(token, "the site keeps only the hash");
        _site.Log.Should().NotContain(token);

        // Gone from the page once it has been applied and reloaded: it cannot be shown again.
        await page.ReloadAsync();
        await page.Locator("#nav-monitor-uplinks").ClickAsync();
        await Assertions.Expect(page.Locator("#f-monitor_uplinks_0__callsign")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#newtoken-f-monitor_uplinks_0_")).ToHaveCountAsync(0);
        (await page.ContentAsync()).Should().NotContain(token);
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Monitor_With_No_Key_Serves_No_Page_At_All()
    {
        RequireBrowser();
        _site = await Site.StartAsync(_scratch.FullName, key: null);
        await _site.WaitForLogAsync("no settings page");

        using var client = new HttpClient();
        foreach (string path in (string[])["config", "api/config", "api/config/schema"])
        {
            (await client.GetAsync(_site.Url(path))).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }

        (await client.GetAsync(_site.Url(""))).StatusCode.Should().Be(HttpStatusCode.OK, "the picker is unaffected");
    }

    private async Task<IPage> OpenAsync()
    {
        _context = await _browser.Instance!.NewContextAsync();
        _context.SetDefaultTimeout(StepMs);
        IPage page = await _context.NewPageAsync();
        await page.GotoAsync(_site!.Url("config"));
        await page.Locator("#key").FillAsync(Key);
        await page.Locator("#login button").ClickAsync();
        await Assertions.Expect(page.Locator("#site-about")).ToBeVisibleAsync(new() { Timeout = StepMs });
        return page;
    }

    /// <summary>The real daemon on a monitor file, restarted when it exits 1 as systemd would.</summary>
    private sealed class Site : IAsyncDisposable
    {
        private readonly int _port;
        private readonly StringBuilder _log = new();
        private readonly CancellationTokenSource _stop = new();
        private Process? _process;
        private int _starts;
        private int _lastExit = int.MinValue;
        private Task _supervising = Task.CompletedTask;

        private Site(string dir, int port, string configPath)
        {
            _port = port;
            Dir = dir;
            ConfigPath = configPath;
        }

        public string Dir { get; }

        public string ConfigPath { get; }

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

        public static async Task<Site> StartAsync(string dir, string? key)
        {
            int port = FreePorts.Next();
            string configPath = Path.Combine(dir, "soundmodem.json");
            string api = key is null ? "" : $"\"api\": {{ \"key\": \"{key}\" }},";
            File.WriteAllText(configPath, $$"""
                {
                  {{api}}
                  "bind": "127.0.0.1",
                  "waterfall": { "port": {{port}}, "title": "before" },
                  "monitor": {
                    "directory": "http://127.0.0.1:1/instances",
                    "refreshMinutes": 0,
                    "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ]
                  }
                }
                """);
            Directory.CreateDirectory(Path.Combine(dir, "state"));

            var site = new Site(dir, port, configPath);
            site._supervising = site.SuperviseAsync();
            try
            {
                await site.WaitForStartsAsync(1);
            }
            catch
            {
                await site.DisposeAsync();
                throw;
            }

            return site;
        }

        /// <summary>Waits until the daemon has started <paramref name="count"/> times and the
        /// latest answers on its port.</summary>
        public async Task WaitForStartsAsync(int count)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                if (_supervising.IsCompleted)
                {
                    throw new InvalidOperationException(
                        $"the daemon stopped for good (exit {_lastExit}). The log:\n{Log}");
                }

                if (Starts >= count)
                {
                    try
                    {
                        using HttpResponseMessage answer = await client.GetAsync(new Uri(Url("")));
                        if (answer.StatusCode == HttpStatusCode.OK)
                        {
                            return;
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                    {
                        // Not listening yet.
                    }
                }

                await Task.Delay(200);
            }

            throw new TimeoutException($"the daemon was not answering after {count} start(s):\n{Log}");
        }

        public async Task WaitForLogAsync(string text)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            while (!Log.Contains(text, StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"the daemon never said \"{text}\":\n{Log}");
                }

                await Task.Delay(100);
            }
        }

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
                info.Environment["STATE_DIRECTORY"] = Path.Combine(Dir, "state");
                info.Environment["INVOCATION_ID"] = "monitor-config-page-browser-tests";

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
                    Kill(process);
                    return;
                }

                _lastExit = process.ExitCode;
                Append($"--- exit {process.ExitCode}");
                if (_lastExit != 1)
                {
                    return;
                }

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
