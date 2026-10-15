using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

/// <summary>
/// The SDRconnect IQ source against a fake SDRconnect on the other end of a real WebSocket: what
/// it asks and sets and in what order, the rate and LO it takes from what SDRconnect says, someone
/// else retuning the radio, and the connection dropping, going quiet and staying away.
/// </summary>
public sealed class SdrConnectIqSourceTests : IDisposable
{
    private const double Centre = 7_075_000;

    private readonly ConcurrentQueue<string> _journal = new();
    private readonly List<IDisposable> _owned = [];

    private SdrConnectOptions Options(int? rate = 48_000) => new()
    {
        CentreFrequencyHz = Centre,
        SampleRate = rate,
        RetuneSettle = TimeSpan.Zero,
        AnswerTimeout = TimeSpan.FromSeconds(2),
        WatchRate = false,

        // Far above any stall of a loaded test machine: at 5 s, a CI run whose test thread stalled
        // between the stream being turned on and the first IQ being sent had the connection dropped
        // as silent, the IQ written into a dead socket, and the reader waiting out the give-up.
        // The tests of the silence watch set their own.
        NoDataTimeout = TimeSpan.FromSeconds(60),
        Journal = _journal.Enqueue,
    };

    private async Task<(SdrConnectIqSource Source, FakeSdrConnect Fake)> OpenAsync(
        FakeSdrConnectHost host, SdrConnectOptions options)
    {
        var source = new SdrConnectIqSource(FakeSdrConnectHost.Endpoint, options, host.ConnectAsync);
        _owned.Add(source);
        Task opening = source.OpenAsync(TestContext.Current.CancellationToken);
        FakeSdrConnect fake = await host.NextAsync();
        await opening;
        return (source, fake);
    }

    private static short[] Ramp(int pairs, int start = 0)
    {
        var samples = new short[2 * pairs];
        for (int k = 0; k < pairs; k++)
        {
            samples[2 * k] = (short)((start + k) * 3);
            samples[(2 * k) + 1] = (short)(-(start + k) * 3);
        }

        return samples;
    }

    private static float[] ReadPairs(SdrConnectIqSource source, int pairs)
    {
        var got = new List<float>();
        var buffer = new float[1000];
        while (got.Count < 2 * pairs)
        {
            int n = source.Read(buffer);
            if (n == 0)
            {
                break;
            }

            got.AddRange(buffer.AsSpan(0, n));
        }

        return [.. got];
    }

    [Fact]
    public async Task It_Asks_What_SDRconnect_Has_Then_Sets_The_Rate_And_The_LO_And_Turns_The_IQ_On_Last()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options() with { LnaState = 4, Antenna = "Antenna B" });
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        List<SdrConnectMessage> sent = [.. fake.Received];
        int Index(string eventType, string property = "") =>
            sent.FindIndex(m => m.EventType == eventType && (property.Length == 0 || m.Property == property));

        Index("get_property", "api_version").Should().BeGreaterThanOrEqualTo(0);
        Index("get_property", "can_control").Should().BeLessThan(Index("set_property", "device_sample_rate"));
        Index("set_property", "lna_state").Should().BeGreaterThan(Index("get_property", "lna_state_max"));
        Index("set_property", "device_sample_rate").Should().BeLessThan(Index("set_property", "device_center_frequency"));
        Index("set_property", "device_center_frequency").Should().BeLessThan(Index("iq_stream_enable"));
        Index("iq_stream_enable").Should().Be(sent.Count - 1, "the stream is turned on once everything else is set");
        Index("audio_stream_enable").Should().Be(-1);
        Index("device_stream_enable").Should().Be(-1, "the radio was already running");

        fake.Value("device_sample_rate").Should().Be("48000");
        fake.Value("device_center_frequency").Should().Be("7075000");
        fake.Value("lna_state").Should().Be("4");
        fake.Value("active_antenna").Should().Be("Antenna B");
        source.SampleRate.Should().Be(48_000);
        source.CentreFrequencyHz.Should().Be(Centre);
        source.Report["api_version"].Should().Be("1.0.3");
        source.Report["active_device"].Should().Be("RSP1A");
        _journal.Should().Contain(l => l.StartsWith("sdrconnect: API version 1.0.3; device RSP1A (devices: RSP1A); can control: true; running: true", StringComparison.Ordinal));
        _journal.Should().Contain(l => l.Contains("antenna Antenna A (antennas: Antenna A,Antenna B)", StringComparison.Ordinal));
        _journal.Should().AllSatisfy(l => l.Should().MatchRegex("^[\\x20-\\x7E]*$"));
    }

    [Fact]
    public async Task An_LO_SDRconnect_Is_Slow_To_Take_Is_Waited_For_Before_The_Open_Says_Where_It_Is()
    {
        // While a set is under way SDRconnect answers a get with the value it is leaving (#701
        // measured half a second for a rate). Taken at the open, an old LO 75 kHz away puts the
        // channel where the IQ input refuses it, or a long way from the dial.
        using var host = new FakeSdrConnectHost(f => f.Lag["device_center_frequency"] = TimeSpan.FromMilliseconds(300));

        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options());

        source.CentreFrequencyHz.Should().Be(Centre, "the open waits for the LO it asked for to be reported");
        fake.Value("device_center_frequency").Should().Be("7075000");
        _journal.Should().NotContain(l => l.Contains("did not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_LO_Moved_Between_The_Open_And_The_First_Iq_Moves_The_Generation_So_A_Front_End_Built_At_The_Open_Follows()
    {
        // The open says the LO it found; a front end is built from that. If the LO then moves
        // before the first IQ (the SDRconnect window, or a report of the set landing late), the
        // first samples are at another LO, and the front end has to be told.
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options());
        double centreAtOpen = source.CentreFrequencyHz;
        long generationAtOpen = source.TuningGeneration;
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.ChangeAsync("device_center_frequency", "7080000");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await fake.SendIqAsync(Ramp(500));
        ReadPairs(source, 500).Should().HaveCount(1000);

        centreAtOpen.Should().Be(Centre);
        source.CentreFrequencyHz.Should().Be(7_080_000);
        source.TuningGeneration.Should().NotBe(generationAtOpen, "the samples are not at the LO the open said");
    }

    [Fact]
    public async Task The_Rate_And_LO_Are_What_SDRconnect_Reports_Not_What_Was_Asked()
    {
        using var host = new FakeSdrConnectHost(f =>
        {
            f.Coerce["device_sample_rate"] = _ => "62500";
            f.Coerce["device_center_frequency"] = _ => "7074000";
        });

        (SdrConnectIqSource source, _) = await OpenAsync(host, Options());

        source.SampleRate.Should().Be(62_500);
        source.CentreFrequencyHz.Should().Be(7_074_000);
    }

    [Fact]
    public async Task A_Rate_SDRconnect_Refuses_Leaves_It_Where_It_Was_And_Is_Said()
    {
        // As the real one did with 3000000 (#701): no report, the rate unchanged.
        using var host = new FakeSdrConnectHost(f => f.Coerce["device_sample_rate"] = _ => "1000000");

        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options(3_000_000) with { AnswerTimeout = TimeSpan.FromMilliseconds(300) });

        fake.Sets("device_sample_rate").Should().ContainSingle().Which.Value.Should().Be("3000000");
        source.SampleRate.Should().Be(1_000_000);
        _journal.Should().Contain(l => l.StartsWith("sdrconnect: SDRconnect did not take a sample rate of 3000000 and stays at 1000000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Rate_SDRconnect_Already_Has_Is_Not_Said_As_Refused()
    {
        using var host = new FakeSdrConnectHost();

        (SdrConnectIqSource source, _) = await OpenAsync(host, Options(1_000_000));

        source.SampleRate.Should().Be(1_000_000);
        _journal.Should().NotContain(l => l.Contains("did not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Radio_That_Is_Not_Running_Is_Started()
    {
        using var host = new FakeSdrConnectHost(f => f.Properties["started"] = "false");

        (_, FakeSdrConnect fake) = await OpenAsync(host, Options());

        fake.Received.Should().Contain(m => m.EventType == "device_stream_enable" && m.Value == "true");
        _journal.Should().Contain(l => l.Contains("was not running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Client_That_May_Not_Control_The_Radio_Is_Refused_In_Words()
    {
        using var host = new FakeSdrConnectHost(f => f.Properties["can_control"] = "false");
        var source = new SdrConnectIqSource(FakeSdrConnectHost.Endpoint, Options(), host.ConnectAsync);
        _owned.Add(source);

        Func<Task> open = () => source.OpenAsync(TestContext.Current.CancellationToken);

        (await open.Should().ThrowAsync<SdrConnectRefusedException>()).WithMessage("*can_control is false*")
            .Which.Reason.Should().Be(SdrConnectFailure.CannotControl);
    }

    [Fact]
    public async Task A_SDRconnect_That_Never_Says_Its_Rate_Is_Refused()
    {
        using var host = new FakeSdrConnectHost(f => f.Silent.Add("device_sample_rate"));
        var source = new SdrConnectIqSource(FakeSdrConnectHost.Endpoint, Options(rate: null), host.ConnectAsync);
        _owned.Add(source);

        Func<Task> open = () => source.OpenAsync(TestContext.Current.CancellationToken);

        (await open.Should().ThrowAsync<SdrConnectRefusedException>()).WithMessage("*did not say its sample rate*")
            .Which.Reason.Should().Be(SdrConnectFailure.NoSampleRate);
    }

    [Fact]
    public async Task Nothing_Listening_Is_An_IO_Error_At_Start_Up()
    {
        using var host = new FakeSdrConnectHost { Refusing = true };
        var source = new SdrConnectIqSource(FakeSdrConnectHost.Endpoint, Options(), host.ConnectAsync);
        _owned.Add(source);

        Func<Task> open = () => source.OpenAsync(TestContext.Current.CancellationToken);

        (await open.Should().ThrowAsync<SdrConnectUnreachableException>()).WithMessage("*cannot reach SDRconnect at ws://shack-pc.example:5454/*")
            .Which.Should().BeAssignableTo<IOException>();
    }

    [Fact]
    public async Task A_Real_Refused_Connect_Says_Why_In_Its_Message_As_Well_As_Its_Reason()
    {
        // The real ClientWebSocket wraps the socket's error in "Unable to connect to the remote
        // server", which on its own does not tell a refused port from a name that does not resolve.
        int port;
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        var source = new SdrConnectIqSource(new SdrConnectEndpoint("127.0.0.1", port), Options());
        _owned.Add(source);

        Func<Task> open = () => source.OpenAsync(TestContext.Current.CancellationToken);

        SdrConnectUnreachableException failure = (await open.Should().ThrowAsync<SdrConnectUnreachableException>()).Which;
        failure.Reason.Should().Be(SdrConnectFailure.Refused);
        failure.Message.Should().Contain($"cannot reach SDRconnect at ws://127.0.0.1:{port}/")
            .And.ContainEquivalentOf("refused").And.MatchRegex("^[\\x20-\\x7E]*$");
    }

    [Fact]
    public async Task Iq_Arrives_As_Sent_Across_Odd_Message_Splits_And_Audio_Is_Ignored()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options());
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        short[] first = Ramp(500);
        short[] second = Ramp(500, start: 500);
        await fake.SendAudioAsync(Ramp(100, start: 9000));
        await fake.SendIqAsync(first, splitAt: 1);   // the type word itself split
        await fake.SendIqAsync(second, splitAt: 7);  // a frame split between frames

        float[] got = ReadPairs(source, 1000);

        got.Select(v => (short)Math.Round(v * 32768)).Should().Equal([.. first, .. second]);
        source.SamplesLost.Should().Be(0);
        source.TuningGeneration.Should().Be(0);
        source.SessionLive.Should().BeTrue();
    }

    [Fact]
    public async Task A_Retune_By_Someone_Else_Is_Said_Followed_And_Put_Back()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options());
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);
        int setsBefore = fake.Sets("device_center_frequency").Count();

        await fake.ChangeAsync("device_center_frequency", "14100000");
        await fake.WaitForAsync(f => f.Sets("device_center_frequency").Count() > setsBefore, "the LO put back");
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        fake.Value("device_center_frequency").Should().Be("7075000");
        source.Client.ForeignChanges.Should().Be(1);
        source.TuningGeneration.Should().BeGreaterThan(0, "the stream moved with the radio and back");
        _journal.Should().Contain(l => l.Contains("was changed to 14100000 by someone else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Retune_This_Client_Asked_For_Is_Not_Taken_For_Someone_Elses()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options());
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        source.Tune(14_075_000);
        await fake.WaitForAsync(f => f.Value("device_center_frequency") == "14075000", "the retune");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        source.CentreFrequencyHz.Should().Be(14_075_000);
        source.TuningGeneration.Should().Be(1);
        source.Client.ForeignChanges.Should().Be(0);
    }

    [Fact]
    public async Task A_Retune_Is_Followed_When_SDRconnect_Does_Not_Report_A_Set_Back_To_The_Client_That_Made_It()
    {
        using var host = new FakeSdrConnectHost(f => f.EchoSets = false);
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options() with { AnswerTimeout = TimeSpan.FromMilliseconds(500) });
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        source.Tune(14_075_000);
        await fake.WaitForAsync(f => f.Value("device_center_frequency") == "14075000", "the retune");
        await Task.Delay(2500, TestContext.Current.CancellationToken); // past the answer timeout and a watchdog tick
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        source.CentreFrequencyHz.Should().Be(14_075_000, "the stream is where SDRconnect says the LO is once asked");
        source.TargetCentreHz.Should().Be(14_075_000);
        source.TuningGeneration.Should().Be(1);
        _journal.Should().NotContain(l => l.Contains("did not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_New_Rate_From_SDRconnect_Is_Taken_With_The_Block_After_It()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options());
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        await fake.ChangeAsync("device_sample_rate", "96000");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        source.SampleRate.Should().Be(96_000);
        source.TuningGeneration.Should().Be(1);
        source.Client.ForeignChanges.Should().Be(0, "a rate is taken as SDRconnect has it, not fought over");
    }

    [Fact]
    public async Task A_Dropped_Connection_Is_Reconnected_Set_Up_Again_And_The_Time_Away_Is_A_Gap()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(
            host, Options() with { ReconnectDelay = TimeSpan.FromMilliseconds(100) });
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        fake.Drop();
        FakeSdrConnect second = await host.NextAsync();
        await second.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await second.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        source.Client.Reconnects.Should().Be(1);
        source.SamplesLost.Should().BeGreaterThan(0);
        second.Value("device_center_frequency").Should().Be("7075000", "everything is sent again");
        _journal.Should().Contain(l => l.Contains("reconnecting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Time_Away_Is_Counted_Once_When_A_Connection_Between_Brought_Only_Another_Stream()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(
            host, Options() with { ReconnectDelay = TimeSpan.FromSeconds(1) });
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        var away = System.Diagnostics.Stopwatch.StartNew();
        fake.Drop();
        FakeSdrConnect second = await host.NextAsync();
        await second.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await second.SendAudioAsync(Ramp(100)); // binary, but not the stream taken
        await Task.Delay(100, TestContext.Current.CancellationToken);
        second.Drop();
        FakeSdrConnect third = await host.NextAsync();
        await third.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await third.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);
        double seconds = away.Elapsed.TotalSeconds;

        source.Client.Reconnects.Should().Be(2);
        source.SamplesLost.Should().BeGreaterThan(0)
            .And.BeLessThanOrEqualTo(
                (long)((seconds + 0.25) * 48_000),
                "the time away is counted once, not again for each connection (which would add the second's 1 s wait again)");
    }

    [Fact]
    public async Task A_Gap_Waiting_To_Be_Read_Is_Still_Counted_When_A_Faster_Rate_Empties_The_Buffer()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(
            host, Options() with { ReconnectDelay = TimeSpan.FromMilliseconds(500) });
        await fake.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fake.SendIqAsync(Ramp(100));
        ReadPairs(source, 100);

        fake.Drop();
        FakeSdrConnect second = await host.NextAsync();
        await second.Streaming.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await second.SendIqAsync(Ramp(100)); // after the gap, and not read yet
        await second.ChangeAsync("device_sample_rate", "96000");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await second.SendIqAsync(Ramp(100));
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (source.Client.FramesDelivered < 300 && DateTime.UtcNow < giveUp)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        ReadPairs(source, 100);

        source.SampleRate.Should().Be(96_000);
        source.SamplesLost.Should().BeGreaterThan(48_000 / 2, "half a second away at 48 kS/s, as well as the 100 pairs thrown away");
    }

    [Fact]
    public async Task A_Connection_That_Goes_Quiet_Is_Dropped_And_Reconnected()
    {
        using var host = new FakeSdrConnectHost();
        (_, FakeSdrConnect fake) = await OpenAsync(host, Options() with
        {
            NoDataTimeout = TimeSpan.FromSeconds(1),
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
        });

        await fake.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await host.NextAsync();

        _journal.Should().Contain(l => l.Contains("sent nothing for 1 s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SDRconnect_Gone_For_Too_Long_Is_Given_Up_On_Once()
    {
        using var host = new FakeSdrConnectHost();
        (SdrConnectIqSource source, FakeSdrConnect fake) = await OpenAsync(host, Options() with
        {
            GiveUpAfter = TimeSpan.FromSeconds(1),
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
            ReconnectDelayCap = TimeSpan.FromMilliseconds(100),
        });
        var lost = new ConcurrentQueue<string>();
        source.Lost += lost.Enqueue;
        host.Refusing = true;

        fake.Drop();
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (lost.IsEmpty && DateTime.UtcNow < giveUp)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await Task.Delay(500, TestContext.Current.CancellationToken);
        lost.Should().ContainSingle().Which.Should().Contain("has delivered nothing for");
        source.Read(new float[2]).Should().Be(0, "a source that has given up stays closed");
    }

    [Fact]
    public void A_Short_Stream_Is_Said_Without_Naming_Anyones_Settings_Unless_The_Embedder_Gives_Its_Own()
    {
        var endpoint = new SdrConnectEndpoint("192.168.40.61", 5454);

        string plain = SdrConnectClient.ShortfallLine(endpoint, 223_520, 250_000, advice: null);
        string own = SdrConnectClient.ShortfallLine(endpoint, 223_520, 250_000, "Use a cable.");

        plain.Should().Be("sdrconnect: 192.168.40.61:5454 is delivering about 223520 samples a second, not the 250000 it should. "
            + "Frames are being lost on the way: the network link, or this machine, cannot keep up. "
            + "A wired link is best. The missing time is marked as gaps.");
        plain.Should().NotContain("\"", "a library cannot know what settings its embedder has, so it names none");
        own.Should().Contain("cannot keep up. Use a cable. The missing time");
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }
    }
}
