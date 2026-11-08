using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Linux.Tests;

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
    /// <summary>How long a restart may take on a loaded runner before it is a failure: the page
    /// itself gives up at two minutes (120 s, and says so in its result), so this is five seconds
    /// over that and no more. Waiting longer would only be waiting for a message that has come.</summary>
    private const float RestartMs = 125_000;

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

        // A wrong key is refused and says so; the right one opens the settings.
        await Assertions.Expect(page.Locator("#login")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await page.Locator("#key").FillAsync("not-the-key");
        await page.Locator("#login button").ClickAsync();
        await Assertions.Expect(page.Locator("#login-error")).ToContainTextAsync("not this station's key", new() { Timeout = StepMs });
        await page.Locator("#key").FillAsync(Key);
        await page.Locator("#login button").ClickAsync();
        await Assertions.Expect(page.Locator("#form-view")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#overview-radio")).ToBeVisibleAsync(new() { Timeout = StepMs });

        // The secret is not on the page in any form.
        await GoToAsync(page, "sharing");
        await Assertions.Expect(page.Locator("#f-api_key")).ToHaveValueAsync("", new() { Timeout = StepMs });
        (await page.ContentAsync()).Should().NotContain(Key);

        await GoToAsync(page, "modes");
        await ReplaceAsync(page, "#f-modems_0__frequency", "1600");
        await GoToAsync(page, "sharing");
        await ReplaceAsync(page, "#f-waterfall_title", "after");
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("2 changes", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("Audio frequency", new() { Timeout = StepMs });

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
        await ReloadSettingsAsync(page);
        await GoToAsync(page, "sharing");
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

        await GoToAsync(page, "sharing");
        await ReplaceAsync(page, "#f-waterfall_title", "just this once");
        await Assertions.Expect(page.Locator("#try")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await page.Locator("#try").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync(
            "The station is back", new() { Timeout = RestartMs });

        await _station.WaitForStartsAsync(2);
        File.ReadAllText(_station.ConfigPath).Should().Be(before, "a one-run change never touches the file");
        _station.Starts.Should().Be(2);
        File.Exists(_station.PendingPath).Should().BeFalse("the restart took the one-run change and removed it");

        // Read afresh from the restarted station: it is running the change, and says it is.
        await ReloadSettingsAsync(page);
        await GoToAsync(page, "sharing");
        await Assertions.Expect(page.Locator("#source")).ToContainTextAsync("one-run", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#f-waterfall_title")).ToHaveValueAsync("just this once", new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Configuration_The_Station_Would_Refuse_Is_Shown_At_Its_Field_And_Cannot_Be_Applied()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        string before = File.ReadAllText(_station.ConfigPath);
        IPage page = await OpenConfigAsync();

        // The 2026-08-15 outage: a trailing space in a mode name. The station's own refusal comes
        // back under the field it is about, and in the bar, with nothing to apply while it stands.
        await GoToAsync(page, "modes");
        await OpenMoreAsync(page, "#more-f-modems_0_");
        await ReplaceAsync(page, "#f-modems_0__mode", "bpsk300 ");
        await Assertions.Expect(page.Locator("#e-f-modems_0__mode")).ToContainTextAsync("unknown mode", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("1 problem to fix", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#save")).ToBeDisabledAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#try")).ToBeDisabledAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#nav-modes .dot")).ToBeVisibleAsync(new() { Timeout = StepMs });

        File.ReadAllText(_station.ConfigPath).Should().Be(before);
        File.Exists(_station.PendingPath).Should().BeFalse();
        _station.Starts.Should().Be(1, "a refused configuration never restarts the station");

        // Mending it lifts the refusal and the changes can go; a change that is fine is checked
        // and offered.
        await ReplaceAsync(page, "#f-modems_0__mode", "bpsk300");
        await ReplaceAsync(page, "#f-modems_0__frequency", "1600");
        await Assertions.Expect(page.Locator("#save")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#e-f-modems_0__mode")).ToBeHiddenAsync(new() { Timeout = StepMs });

        // And text that is not a number never reaches the station at all.
        await GoToAsync(page, "software");
        await ReplaceAsync(page, "#f-kissPort", "eighty");
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("needs fixing", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#save")).ToBeDisabledAsync(new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Block_Dragged_On_The_Band_Strip_Changes_The_Modems_Frequency_In_The_File()
    {
        RequireBrowser();
        // A pinned dial keeps the strip still while a block moves; two modems, so the dragged one
        // has company on the strip.
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050800 },
              { "subChannel": 1, "mode": "qpsk600", "rfFrequency": 7051900 } ],
              "sideband": "usb", "dialFrequency": 7050000
            """);
        IPage page = await OpenConfigAsync();

        await GoToAsync(page, "modes");
        ILocator block = page.Locator("#strip-svg .blk[data-sub='0']");
        await Assertions.Expect(block).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#strip.stale")).ToHaveCountAsync(0, new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#f-modems_0__rfFrequency")).ToHaveValueAsync("7.050800", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("[data-hint-for='0']")).ToContainTextAsync("Audio tone 800 Hz", new() { Timeout = StepMs });

        // Drag it right by roughly 300 Hz. How far that is in pixels depends on the width of the
        // strip, which is read from the page, and the answer is a multiple of 100 Hz.
        string before = (await block.GetAttributeAsync("data-centre"))!;
        var box = (await block.Locator("rect.body").BoundingBoxAsync())!;
        double startX = box.X + (box.Width / 2);
        double startY = box.Y + (box.Height / 2);
        double blockPxPer300 = box.Width;
        await page.Mouse.MoveAsync((float)startX, (float)startY);
        await page.Mouse.DownAsync();
        // Hold the block part-way until the station has answered a plan for the move so far (the
        // page asks 250 ms after the last move): the answer must not take the block from the pointer.
        Task<IResponse> planned = page.WaitForResponseAsync(r => r.Url.EndsWith("/api/config/plan", StringComparison.Ordinal),
            new() { Timeout = StepMs });
        await page.Mouse.MoveAsync((float)(startX + (blockPxPer300 / 2)), (float)startY, new() { Steps = 8 });
        await planned;
        await page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => r(true)))");
        (await block.EvaluateAsync<bool>("e => e.isConnected")).Should().BeTrue("the strip is not rebuilt under a held block");
        await page.Mouse.MoveAsync((float)(startX + blockPxPer300), (float)startY, new() { Steps = 8 });
        await page.Mouse.UpAsync();

        // The field follows the block and the change is pending; wait on those, not on time.
        ILocator field = page.Locator("#f-modems_0__rfFrequency");
        await Assertions.Expect(field).Not.ToHaveValueAsync("7.050800", new() { Timeout = StepMs });
        string moved = await field.InputValueAsync();
        double movedHz = Math.Round(double.Parse(moved, System.Globalization.CultureInfo.InvariantCulture) * 1e6);
        (movedHz % 100).Should().Be(0, "a drag lands on a step of 100 Hz");
        movedHz.Should().BeGreaterThan(7050800, "dragged to the right");
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("Radio frequency", new() { Timeout = StepMs });
        before.Should().NotBe(await block.GetAttributeAsync("data-centre"));

        await SaveAsync(page);

        JsonNode file = _station.ReadConfig();
        file["modems"]![0]!["rfFrequency"]!.GetValue<double>().Should().Be(movedHz);
        file["modems"]![1]!["rfFrequency"]!.GetValue<double>().Should().Be(7051900, "the other modem was not touched");
        file["modems"]![0]!["frequency"].Should().BeNull("a modem has one or the other");

        // The arrow keys move the same block, a hundred Hz at a time, a kilohertz with Shift.
        await ReloadSettingsAsync(page);
        await GoToAsync(page, "modes");
        block = page.Locator("#strip-svg .blk[data-sub='0']");
        await Assertions.Expect(block).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#f-modems_0__rfFrequency")).ToHaveValueAsync(moved, new() { Timeout = StepMs });
        await block.FocusAsync();
        await page.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(page.Locator("#f-modems_0__rfFrequency")).ToHaveValueAsync(
            ((movedHz - 100) / 1e6).ToString("F6", System.Globalization.CultureInfo.InvariantCulture), new() { Timeout = StepMs });
        await page.Keyboard.PressAsync("Shift+ArrowRight");
        await Assertions.Expect(page.Locator("#f-modems_0__rfFrequency")).ToHaveValueAsync(
            ((movedHz + 900) / 1e6).ToString("F6", System.Globalization.CultureInfo.InvariantCulture), new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Modem_Added_From_The_Picker_Takes_A_Free_Sub_Channel_And_A_Free_Part_Of_The_Band()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050800 } ],
              "sideband": "usb", "dialFrequency": 7050000
            """);
        IPage page = await OpenConfigAsync();

        await GoToAsync(page, "modes");
        await Assertions.Expect(page.Locator("#strip-svg .blk[data-sub='0']")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await page.Locator("#add-f-modems").ClickAsync();
        await page.Locator("#choice-group-nino").ClickAsync();
        await page.Locator("#choice-mode-qpsk600").ClickAsync();

        await Assertions.Expect(page.Locator("#modem-f-modems_1_")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#modem-f-modems_1_ .head .who .line .name")).ToHaveTextAsync("600 baud QPSK", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#modem-f-modems_1_ .sub")).ToHaveTextAsync("Sub-channel 1", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#modem-f-modems_1_ .talks")).ToContainTextAsync("NinoTNC mode 9", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#strip-svg .blk[data-sub='1']")).ToBeVisibleAsync(new() { Timeout = StepMs });

        // The station's own plan, fetched after the add, finds no overlap and nothing past an edge.
        await Assertions.Expect(page.Locator("#strip.stale")).ToHaveCountAsync(0, new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#strip-svg .blk[data-sub='1']")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("[data-warn-for='1']")).ToBeHiddenAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("[data-warn-for='0']")).ToBeHiddenAsync(new() { Timeout = StepMs });

        await SaveAsync(page);
        JsonNode file = _station.ReadConfig();
        file["modems"]!.AsArray().Should().HaveCount(2);
        file["modems"]![1]!["subChannel"]!.GetValue<int>().Should().Be(1);
        file["modems"]![1]!["mode"]!.GetValue<string>().Should().Be("qpsk600");
        double rf = file["modems"]![1]!["rfFrequency"]!.GetValue<double>();
        (rf % 100).Should().Be(0);
        Math.Abs(rf - 7050800).Should().BeGreaterThanOrEqualTo(300, "beside the first modem, not on it");
    }

    /// <summary>
    /// The Radio and audio and Keying areas offer what the machine has. The daemon is given a
    /// written machine (a CM108 interface with its hidraw, and a serial adapter) through its
    /// discovery fixture, which is the only seam: it is a real daemon, so a fake object cannot be
    /// handed to it, and nothing on the machine running the test is looked at.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task A_Sound_Card_And_A_Ptt_Pin_Picked_From_What_Was_Found_Are_Saved_In_The_File()
    {
        RequireBrowser();
        string machine = new FakeDeviceTree()
            .Cm108(card: 1, hidraw: 1)
            .UsbSerial(0, "/sys/devices/pci0000:00/0000:00:14.0/usb3/3-3", "0403", "6001", "FTDI", "FT232R USB UART", byId: null)
            .Describe()
            + "mixer\tDevice\tMic\tSpeaker\n";
        _station = await Station.StartAsync(_scratch.FullName, key: null, machine: machine);
        IPage page = await OpenConfigAsync();

        await GoToAsync(page, "radio");
        await Assertions.Expect(page.Locator("#option-card-Device")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#option-card-Device")).ToContainTextAsync("plughw:CARD=Device,DEV=0", new() { Timeout = StepMs });
        await page.Locator("#pick-card-Device").CheckAsync();
        await Assertions.Expect(page.Locator("#f-device")).ToHaveValueAsync("plughw:CARD=Device,DEV=0", new() { Timeout = StepMs });

        // Typing by hand is still possible: the field is the same field.
        await Assertions.Expect(page.Locator("#f-device")).ToBeEditableAsync(new() { Timeout = StepMs });

        await GoToAsync(page, "keying");
        await Assertions.Expect(page.Locator("#option-ptt-cm108-hidraw1")).ToContainTextAsync("the same card you picked for audio", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#option-ptt-serial-ttyUSB0")).ToContainTextAsync("FTDI FT232R USB UART", new() { Timeout = StepMs });
        await page.Locator("#pick-ptt-cm108-hidraw1").CheckAsync();
        await Assertions.Expect(page.Locator("#extra-ptt-cm108-hidraw1")).ToHaveValueAsync("3", new() { Timeout = StepMs });
        await page.Locator("#extra-ptt-cm108-hidraw1").SelectOptionAsync("1");
        await Assertions.Expect(page.Locator("#f-ptt_gpio")).ToHaveValueAsync("1", new() { Timeout = StepMs });

        // The pending bar is the page's own count of what is about to be written.
        await Assertions.Expect(page.Locator("#save")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await page.Locator("#save").ClickAsync();

        // The restart onto a sound card this machine does not have cannot finish, so the test
        // waits for the file, which is written before the restart.
        await _station.WaitForLogAsync("configuration written");
        JsonNode file = _station.ReadConfig();
        file["device"]!.GetValue<string>().Should().Be("plughw:CARD=Device,DEV=0");
        file["ptt"]!["type"]!.GetValue<string>().Should().Be("cm108");
        file["ptt"]!["device"]!.GetValue<string>().Should().Be("/dev/hidraw1");
        file["ptt"]!["gpio"]!.GetValue<int>().Should().Be(1);
        file["notASettingYet"]!.GetValue<int>().Should().Be(7, "what the page never showed is kept");
    }

    [Fact(Timeout = 600_000)]
    public async Task Look_Again_Asks_The_Station_For_A_Fresh_Look()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, machine: "");
        IPage page = await NewPageAsync();
        var asked = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("api/config/discover", StringComparison.Ordinal))
            {
                lock (asked)
                {
                    asked.Add(request.Url);
                }
            }
        };
        await page.GotoAsync(_station.Url("config"));
        await Assertions.Expect(page.Locator("#overview-radio")).ToBeVisibleAsync(new() { Timeout = StepMs });

        await GoToAsync(page, "radio");
        await Assertions.Expect(page.Locator("#found .none")).ToContainTextAsync("No sound cards", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#look-again")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await page.Locator("#look-again").ClickAsync();

        await Assertions.Expect(page.Locator("#look-again")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#found .none")).ToBeVisibleAsync(new() { Timeout = StepMs });
        lock (asked)
        {
            asked.Should().HaveCount(2);
            asked[1].Should().EndWith("?fresh=1");
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Click_On_A_Block_Does_Not_Move_The_Modem_And_Mhz_Typed_As_Hz_Is_Refused()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050830 } ],
              "sideband": "usb", "dialFrequency": 7050000
            """);
        IPage page = await OpenConfigAsync();
        await GoToAsync(page, "modes");
        ILocator block = page.Locator("#strip-svg .blk[data-sub='0']");
        await Assertions.Expect(block).ToBeVisibleAsync(new() { Timeout = StepMs });
        var box = (await block.Locator("rect.body").BoundingBoxAsync())!;

        // A press and release with a couple of pixels of jitter, on a frequency off the grid.
        await page.Mouse.MoveAsync((float)(box.X + (box.Width / 2)), (float)(box.Y + (box.Height / 2)));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(box.X + (box.Width / 2) + 2), (float)(box.Y + (box.Height / 2)));
        await page.Mouse.UpAsync();
        await Assertions.Expect(page.Locator("#f-modems_0__rfFrequency")).ToHaveValueAsync("7.050830", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#pending")).ToBeHiddenAsync(new() { Timeout = StepMs });

        // Hz where MHz belongs is not written.
        await ReplaceAsync(page, "#f-modems_0__rfFrequency", "7050800");
        await Assertions.Expect(page.Locator("#e-f-modems_0__rfFrequency")).ToContainTextAsync("looks like Hz", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("needs fixing", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#save")).ToBeDisabledAsync(new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Dial_Left_Beside_Audio_Modems_Leaves_The_Strip_In_Audio_And_The_Arrow_Keys_Write_The_Audio_Frequency()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "bpsk300", "frequency": 1000 },
              { "subChannel": 1, "mode": "qpsk600", "frequency": 2000 } ],
              "sideband": "usb", "dialFrequency": 7050000
            """);
        IPage page = await OpenConfigAsync();
        await GoToAsync(page, "modes");
        ILocator block = page.Locator("#strip-svg .blk[data-sub='0']");
        await Assertions.Expect(block).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#f-modems_0__frequency")).ToHaveValueAsync("1000", new() { Timeout = StepMs });

        await block.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(page.Locator("#f-modems_0__frequency")).ToHaveValueAsync("1100", new() { Timeout = StepMs });
        await SaveAsync(page);

        JsonNode file = _station.ReadConfig();
        file["modems"]![0]!["frequency"]!.GetValue<double>().Should().Be(1100);
        file["modems"]![0]!["rfFrequency"].Should().BeNull();
        file["modems"]![1]!["rfFrequency"].Should().BeNull();
    }

    [Fact(Timeout = 600_000)]
    public async Task On_FM_A_New_Modem_Joins_The_Channel_The_Others_Are_On_And_The_File_Still_Starts()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "afsk1200", "frequency": 1700, "rfFrequency": 144800000 } ],
              "sideband": "fm"
            """);
        IPage page = await OpenConfigAsync();
        await GoToAsync(page, "modes");
        await Assertions.Expect(page.Locator("#modem-f-modems_0_")).ToBeVisibleAsync(new() { Timeout = StepMs });

        await page.Locator("#add-f-modems").ClickAsync();
        await page.Locator("#choice-group-nino").ClickAsync();
        await page.Locator("#choice-mode-afsk1200-il2p").ClickAsync();
        await Assertions.Expect(page.Locator("#modem-f-modems_1_")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await SaveAsync(page);

        JsonNode file = _station.ReadConfig();
        file["modems"]!.AsArray().Should().HaveCount(2);
        file["modems"]![1]!["rfFrequency"]!.GetValue<double>().Should().Be(144800000, "all or none of the modems carry a channel");
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Station_Placed_By_Radio_Frequency_Is_Not_Offered_Modes_With_No_Tone_To_Place()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050800 } ],
              "sideband": "usb", "dialFrequency": 7050000
            """);
        IPage page = await OpenConfigAsync();
        await GoToAsync(page, "modes");
        await page.Locator("#add-f-modems").ClickAsync();

        await Assertions.Expect(page.Locator("#choice-group-p1200")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#choice-group-ofdmfm")).ToHaveCountAsync(0, new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#picker-note")).ToContainTextAsync("radio frequency", new() { Timeout = StepMs });
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Modem_Added_When_The_Plan_Is_Refused_Still_Gets_A_Radio_Frequency()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null, modems: """
            [ { "subChannel": 0, "mode": "bpsk300", "rfFrequency": 7050800 } ],
              "sideband": "usb"
            """);
        IPage page = await OpenConfigAsync();
        await GoToAsync(page, "modes");
        await Assertions.Expect(page.Locator("#strip-svg .blk[data-sub='0']")).ToBeVisibleAsync(new() { Timeout = StepMs });

        await page.Locator("#add-f-modems").ClickAsync();
        await page.Locator("#choice-group-nino").ClickAsync();
        await page.Locator("#choice-mode-qpsk600").ClickAsync();
        await Assertions.Expect(page.Locator("#f-modems_1__rfFrequency")).ToBeVisibleAsync(new() { Timeout = StepMs });
        // Twenty kHz apart: more than one dial can cover.
        await ReplaceAsync(page, "#f-modems_1__rfFrequency", "7.0708");
        await Assertions.Expect(page.Locator("#strip.stale")).ToHaveCountAsync(0, new() { Timeout = StepMs });

        await page.Locator("#add-f-modems").ClickAsync();
        await page.Locator("#choice-group-nino").ClickAsync();
        await page.Locator("#choice-mode-bpsk1200").ClickAsync();

        ILocator added = page.Locator("#f-modems_2__rfFrequency");
        await Assertions.Expect(added).ToBeVisibleAsync(new() { Timeout = StepMs });
        string value = await added.InputValueAsync();
        value.Should().MatchRegex(@"^\d+\.\d{6}$", "the new modem has a radio frequency like the others");
    }

    [Fact(Timeout = 600_000)]
    public async Task A_Section_Added_On_The_Page_And_An_Edit_Pasted_As_Text_Are_Both_Saved()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        string frameLog = Path.Combine(_scratch.FullName, "frames.db");
        IPage page = await OpenConfigAsync();

        // A section the file does not have, set up with its button.
        await GoToAsync(page, "sharing");
        await page.Locator("#add-f-frameLog").ClickAsync();
        await ReplaceAsync(page, "#f-frameLog_path", frameLog);

        // Edit as text shows it, and text pasted there (comments and all) is what gets saved.
        await page.Locator("#edit-text").ClickAsync();
        await Assertions.Expect(page.Locator("#json")).ToHaveValueAsync(
            new System.Text.RegularExpressions.Regex("frameLog"), new() { Timeout = StepMs });
        JsonNode edited = JsonNode.Parse(await page.Locator("#json").InputValueAsync())!;
        edited["waterfall"]!["title"] = "pasted";
        await page.Locator("#json").FillAsync("// pasted from a notebook\n" + edited.ToJsonString() + "\n");
        await page.Locator("#edit-text").ClickAsync();
        await GoToAsync(page, "sharing");
        await Assertions.Expect(page.Locator("#f-waterfall_title")).ToHaveValueAsync("pasted", new() { Timeout = StepMs });

        await SaveAsync(page);

        JsonNode file = _station.ReadConfig();
        file["frameLog"]!["path"]!.GetValue<string>().Should().Be(frameLog);
        file["waterfall"]!["title"]!.GetValue<string>().Should().Be("pasted");
        _station.Starts.Should().Be(2);
    }

    [Fact(Timeout = 600_000)]
    public async Task An_Earlier_Version_Can_Be_Looked_At_And_Gone_Back_To()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        IPage page = await OpenConfigAsync();

        // Nothing has been applied yet, so there is nothing to go back to.
        await page.Locator("#nav-history").ClickAsync();
        await Assertions.Expect(page.Locator("#form-view")).ToContainTextAsync("Nothing yet", new() { Timeout = StepMs });

        // Two applied changes: one saved, one tried.
        await GoToAsync(page, "sharing");
        await ReplaceAsync(page, "#f-waterfall_title", "second");
        await SaveAsync(page);
        await GoToAsync(page, "sharing");
        await ReplaceAsync(page, "#f-waterfall_title", "third");
        await Assertions.Expect(page.Locator("#try")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await page.Locator("#try").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync(
            "The station is back", new() { Timeout = RestartMs });
        await _station.WaitForStartsAsync(3);

        // Newest first: the one tried, the one saved, then the original the station started on.
        // The one tried is what is running, and says so.
        await page.Locator("#nav-history").ClickAsync();
        await Assertions.Expect(page.Locator("button[id^='restore-']")).ToHaveCountAsync(3, new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("[id^='inuse-']")).ToHaveCountAsync(1, new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator(".version").First.Locator("[id^='inuse-']")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await page.Locator("button[id^='look-']").Nth(1).ClickAsync();
        await Assertions.Expect(page.Locator("[id^='look-body-']").Nth(1)).ToContainTextAsync("\"title\": \"second\"", new() { Timeout = StepMs });

        // Going back loads it as a change that is checked like any other, and applying it
        // writes the older title to the file.
        await page.Locator("button[id^='restore-']").Nth(1).ClickAsync();
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("1 change", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#changes")).ToContainTextAsync("Page title", new() { Timeout = StepMs });
        await SaveAsync(page, starts: 4);

        _station.ReadConfig()["waterfall"]!["title"]!.GetValue<string>().Should().Be("second");
    }

    [Fact(Timeout = 600_000)]
    public async Task Moving_Or_Removing_The_Station_Page_Is_Said_Before_It_Is_Applied()
    {
        RequireBrowser();
        _station = await Station.StartAsync(_scratch.FullName, key: null);
        IPage page = await OpenConfigAsync();
        await GoToAsync(page, "sharing");
        int moved = FreePorts.Next();

        await ReplaceAsync(page, "#f-waterfall_port", moved.ToString());
        await Assertions.Expect(page.Locator("#page-notice")).ToContainTextAsync("moves the station page", new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#page-notice")).ToContainTextAsync($":{moved}/config", new() { Timeout = StepMs });

        await page.Locator("#revert").ClickAsync();
        await GoToAsync(page, "sharing");
        await page.Locator("#remove-f-waterfall").ClickAsync();
        await Assertions.Expect(page.Locator("#page-notice")).ToContainTextAsync("removes the station page", new() { Timeout = StepMs });
    }

    // ---------------------------------------------------------------- steps

    private async Task<IPage> NewPageAsync()
    {
        _context = await _browser.Instance!.NewContextAsync();
        _context.SetDefaultTimeout(StepMs);
        _context.SetDefaultNavigationTimeout(StepMs);
        return await _context.NewPageAsync();
    }

    /// <summary>The settings page of a station with no key, showing its overview.</summary>
    private async Task<IPage> OpenConfigAsync()
    {
        IPage page = await NewPageAsync();
        await page.GotoAsync(_station!.Url("config"));
        await Assertions.Expect(page.Locator("#overview-radio")).ToBeVisibleAsync(new() { Timeout = StepMs });
        return page;
    }

    /// <summary>Loads the page again and waits for the settings, with no load error showing.</summary>
    private static async Task ReloadSettingsAsync(IPage page)
    {
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("#form-view")).ToBeVisibleAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#loading")).ToBeHiddenAsync(new() { Timeout = StepMs });
        await Assertions.Expect(page.Locator("#nav")).ToBeVisibleAsync(new() { Timeout = StepMs });
    }

    /// <summary>Opens an area from the menu and waits until it is the one showing.</summary>
    private static async Task GoToAsync(IPage page, string area)
    {
        await page.Locator($"#nav-{area}").ClickAsync();
        await Assertions.Expect(page.Locator($"#nav-{area}")).ToHaveClassAsync(
            new System.Text.RegularExpressions.Regex("\\bon\\b"), new() { Timeout = StepMs });
    }

    /// <summary>Opens a modem's More section and waits until it is open.</summary>
    private static async Task OpenMoreAsync(IPage page, string selector)
    {
        await page.Locator($"{selector} > summary").ClickAsync();
        await Assertions.Expect(page.Locator(selector)).ToHaveAttributeAsync("open", "", new() { Timeout = StepMs });
    }

    /// <summary>Types a field's new text and waits until the page has taken it.</summary>
    private static async Task ReplaceAsync(IPage page, string selector, string text)
    {
        ILocator field = page.Locator(selector);
        await field.FillAsync(text);
        await Assertions.Expect(field).ToHaveValueAsync(text, new() { Timeout = StepMs });
    }

    /// <summary>Waits for the station to have checked the change, saves it, and waits for the
    /// restarted station.</summary>
    private async Task SaveAsync(IPage page, int starts = 2)
    {
        await Assertions.Expect(page.Locator("#save")).ToBeEnabledAsync(new() { Timeout = StepMs });
        await page.Locator("#save").ClickAsync();
        await Assertions.Expect(page.Locator("#result")).ToContainTextAsync(
            "The station is back", new() { Timeout = RestartMs });
        await _station!.WaitForStartsAsync(starts);
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
                // Libraries and fonts CI fetched for a runner that lacks them
                // (scripts/browser-libs.sh), for the browser alone: the daemon and the rest of the
                // suite never see them.
                var launch = new BrowserTypeLaunchOptions { Headless = true };
                string? libs = Environment.GetEnvironmentVariable("PDN_BROWSER_LIBRARY_PATH");
                string? fonts = Environment.GetEnvironmentVariable("PDN_BROWSER_FONTCONFIG_FILE");
                if (!string.IsNullOrEmpty(libs) || !string.IsNullOrEmpty(fonts))
                {
                    // Env replaces the browser's whole environment, so it is this process's
                    // environment with these added, not these alone.
                    var env = new Dictionary<string, string>();
                    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    {
                        env[(string)entry.Key] = (string?)entry.Value ?? "";
                    }

                    if (!string.IsNullOrEmpty(libs))
                    {
                        string? existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                        env["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? libs : $"{libs}:{existing}";
                    }

                    if (!string.IsNullOrEmpty(fonts))
                    {
                        env["FONTCONFIG_FILE"] = fonts;
                    }

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
                        + "install steps should have put it in $PLAYWRIGHT_BROWSERS_PATH, and any libraries "
                        + "and fonts the runner lacks in $PDN_BROWSER_LIBRARY_PATH and "
                        + $"$PDN_BROWSER_FONTCONFIG_FILE. Playwright said:\n{e.Message}", e);
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

        private string? _machine;

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

        /// <param name="machine">A machine for /api/config/discover to find, in the daemon's fixture
        /// format (HardwareFixture), or null for the machine the test runs on.</param>
        public static async Task<Station> StartAsync(string dir, string? key, string? modems = null, string? machine = null)
        {
            int port = FreePorts.Next();
            int kiss = FreePorts.Next();
            string configPath = Path.Combine(dir, "soundmodem.json");
            string api = key is null ? "" : $",\n  \"api\": {{ \"key\": \"{key}\" }}";
            string modemList = modems ?? "[ { \"subChannel\": 0, \"mode\": \"bpsk300\", \"frequency\": 1500 } ]";
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
                  "modems": {{modemList}},
                  "notASettingYet": 7{{api}}
                }
                """);

            var station = new Station(dir, port, configPath);
            if (machine is not null)
            {
                station._machine = Path.Combine(dir, "machine.txt");
                File.WriteAllText(station._machine, machine);
            }

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
                if (_machine is not null)
                {
                    info.Environment[HardwareFixture.Variable] = _machine;
                }

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
