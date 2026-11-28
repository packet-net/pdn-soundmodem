using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.RtlTcp;

namespace Packet.SoundModem.Tests.RtlTcp;

/// <summary>
/// The rtl_tcp source against a fake server on loopback: what it sends, what it does not, and
/// how it reports a server that goes away, goes quiet, or changes under it.
/// </summary>
public sealed class RtlTcpIqSourceTests
{
    private const int Rate = 250_000;

    private static readonly TimeSpan HangGuard = FakeRtlTcpServer.HangGuard;

    private readonly ConcurrentQueue<string> _journal = new();

    // The source's connect and no-data timeouts are on the machine's own clock (the kernel's,
    // for the no-data one), and a test run whose thread pool is taken by other tests' work can
    // go past the defaults (10 s and 5 s) with the fake server only waiting to be scheduled
    // (#712): a connect then fails with "accepted the connection and then sent nothing", or a
    // reader waiting for the test's next send drops and reconnects, and on a fake clock nobody
    // advances it then waits for its reconnect delay for ever. So both are hang guards here, and
    // the tests that are about one of them set it themselves.
    private RtlTcpSourceOptions Controlled(double centreHz = 144_775_000) => new()
    {
        CentreFrequencyHz = centreHz,
        SampleRate = Rate,
        TunerGainDb = 29.6,
        FrequencyCorrectionPpm = -12,
        RetuneSettle = TimeSpan.FromMilliseconds(1),
        RetuneDiscardFloorBytes = 0,
        WatchProfile = false,
        ConnectTimeout = HangGuard,
        NoDataTimeout = HangGuard,
        Journal = _journal.Enqueue,
    };

    private RtlTcpSourceOptions ReadOnly() => new()
    {
        ReadOnly = true,
        CentreFrequencyHz = 144_775_000,
        SampleRate = Rate,
        WatchProfile = false,
        ConnectTimeout = HangGuard,
        NoDataTimeout = HangGuard,
        Journal = _journal.Enqueue,
    };

    [Fact]
    public async Task A_Controlled_Server_Is_Sent_Rate_Centre_Correction_Gain_And_Agc_In_That_Order()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled());

        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(6);

        server.Commands.Select(c => (c.Command, c.Parameter)).Should().Equal(
            (RtlTcpCommand.SetSampleRate, 250_000u),
            (RtlTcpCommand.SetFrequency, 144_775_000u),
            (RtlTcpCommand.SetFrequencyCorrection, unchecked((uint)-12)),
            (RtlTcpCommand.SetGainMode, 1u),
            (RtlTcpCommand.SetGain, 296u),
            (RtlTcpCommand.SetAgcMode, 0u));
        source.Header.Should().Be(new RtlTcpHeader(RtlTcpTuner.R820T, 29));
        _journal.Should().Contain(l => l.Contains("R820T tuner", StringComparison.Ordinal));
    }

    [Fact]
    public void The_0_90_Option_Names_Still_Set_The_Converter_And_The_Swap()
    {
#pragma warning disable CS0618 // the old names are what is under test
        RtlTcpSourceOptions options = Controlled() with { LfoOffsetHz = 80_090_100, IqSwap = true };
#pragma warning restore CS0618

        options.ConverterLoHz.Should().Be(80_090_100);
        options.SwapIq.Should().BeTrue();
    }

    [Fact]
    public async Task A_Converter_Lo_Is_Added_To_The_Centre_Before_It_Is_Sent()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled(73_061_628 - 66_021_528) with { ConverterLoHz = 66_021_528 });

        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(2);

        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetFrequency && c.Parameter == 73_061_628u);
        source.CentreFrequencyHz.Should().Be(73_061_628 - 66_021_528, "everything above this source still reads the logical centre, not the dongle's own tuned frequency");
    }

    [Fact]
    public async Task A_Converter_Lo_Is_Added_Again_On_A_Retune()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled() with { ConverterLoHz = 66_021_528 });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(6);

        source.Tune(144_900_000);
        server.WaitForCommands(7);

        server.Commands.Last().Should().Be((1, RtlTcpCommand.SetFrequency, 144_900_000u + 66_021_528u));
    }

    [Fact]
    public void A_Centre_That_Overflows_Once_The_Converter_Lo_Is_Added_Is_Refused()
    {
        Action open = () => new RtlTcpIqSource(new RtlTcpEndpoint("127.0.0.1", 1), Controlled(4_000_000_000) with { ConverterLoHz = 400_000_000 });

        open.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*converter's oscillator*");
    }

    [Fact]
    public async Task With_Iq_Swap_The_Dongle_Is_Tuned_To_The_Converter_Lo_Less_The_Centre()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled(1_000_000) with { ConverterLoHz = 50_000_000, SwapIq = true });

        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(2);

        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetFrequency && c.Parameter == 49_000_000u);
    }

    /// <summary>
    /// Tom's review of #703: with <c>swapIq</c> on, a tone really sitting where the station's
    /// own DC-clearance places the dial (<see cref="IqTuning.DcClearOffsetHz"/> below the dongle's
    /// own tuned frequency, before the swap) has to come out, after the swap, where everything
    /// above this source expects the dial: the same distance above <see cref="RtlTcpIqSource.CentreFrequencyHz"/>,
    /// not mirrored to the far side of it. This is the end-to-end check that would have caught a
    /// fixed offset with the wrong sign (the bug #703 was reviewed for, worked out from the code
    /// rather than run): it fails at this offset specifically because a sign error moves the dial
    /// to twice the gap away, on the wrong side, not merely a little off.
    /// </summary>
    [Fact]
    public async Task With_Iq_Swap_A_Tone_Below_The_Tuned_Frequency_Lands_Above_The_Centre()
    {
        const double ConverterLoHz = 50_000_000;
        const double CentreHz = 1_000_000;
        const double DcClearHz = 25_000;
        double tunerHz = ConverterLoHz - CentreHz; // what Tune sends with swapIq on

        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled(CentreHz) with { ConverterLoHz = ConverterLoHz, SwapIq = true });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(2);
        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetFrequency && c.Parameter == (uint)tunerHz);

        // The dongle really is at tunerHz; a tone DcClearHz below it is what a converter at
        // ConverterLoHz puts there when the real signal sits at CentreHz + DcClearHz - exactly
        // where this station's own DC-clearance places the dial before any swap.
        server.Send(ComplexToneBytes(Rate, offsetHz: -DcClearHz, seconds: 0.05));
        float[] iq = ReadAtLeast(source, (int)(Rate * 0.03) * 2);

        ComplexMagnitudeAt(iq, DcClearHz, Rate).Should().BeGreaterThan(0.3f, "the dial lands above the centre, the same side the station's own DC clearance always puts it");
        ComplexMagnitudeAt(iq, -DcClearHz, Rate).Should().BeLessThan(0.05f, "not mirrored to below the centre");
    }

    [Fact]
    public async Task Iq_Swap_Exchanges_I_And_Q_After_Conversion()
    {
        using var server = new FakeRtlTcpServer(header: null) { Header = null };
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with { SwapIq = true });
        Task open = source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(new byte[12]);
        await open;

        server.Send([0, 255, 127, 128]);
        float[] got = ReadAtLeast(source, 4);

        // Swapped, so Q comes first and I second for each sample.
        got.Should().Equal(1f, -1f, 0.5f / 127.5f, -0.5f / 127.5f);
    }

    [Fact]
    public async Task With_No_Gain_Given_The_Tuner_Is_Left_On_Its_Own_Gain()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled() with { TunerGainDb = null, RtlAgc = true });

        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(5);

        // Gain comes before AGC in the order the commands go, so with AGC in hand none is coming.
        source.Dispose();
        server.WaitForClientsEnded(1);

        server.Commands.Select(c => c.Command).Should().NotContain(RtlTcpCommand.SetGain);
        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetGainMode && c.Parameter == 0);
        server.Commands.Should().Contain(c => c.Command == RtlTcpCommand.SetAgcMode && c.Parameter == 1);
    }

    [Fact]
    public async Task A_Read_Only_Server_Is_Sent_Nothing_And_Cannot_Be_Retuned()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly());
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);

        server.Send([0, 255, 127, 128]);
        float[] got = ReadAtLeast(source, 4);

        got.Should().Equal(-1f, 1f, -0.5f / 127.5f, 0.5f / 127.5f);
        Action tune = () => source.Tune(145_000_000);
        tune.Should().Throw<InvalidOperationException>().WithMessage("*read-only*");
        source.TuningGeneration.Should().Be(0);
        source.SamplesLost.Should().Be(0);

        // Closing our end ends the server's read, so what it counted is everything that was sent.
        source.Dispose();
        server.WaitForClientsEnded(1);
        server.BytesFromClient.Should().Be(0, "a read-only server is never sent a byte");
    }

    [Fact]
    public async Task A_Server_With_No_Header_Is_Taken_As_Samples_From_The_Start()
    {
        using var server = new FakeRtlTcpServer(header: null) { Header = null };
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly());

        // The first twelve bytes are waited for as a header; without the magic they are let go.
        Task open = source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(new byte[12]);
        await open;
        server.Send([255, 0]);

        ReadAtLeast(source, 2).Should().Equal(1f, -1f);
        source.Header.Should().BeNull();
        _journal.Should().Contain(l => l.Contains("sent no dongle header", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Dropped_Connection_Is_Reconnected_Settings_Resent_And_The_Time_Away_Reported_As_A_Gap()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled() with
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.WaitForCommands(6);

        // 500 bytes of settling are thrown away after the settings; then samples.
        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        ReadAtLeast(source, 1000).Length.Should().Be(1000);
        source.SamplesLost.Should().Be(0, "nothing came before the first block, so nothing was lost");

        server.DropClient();
        Task<float[]> after = Task.Run(() => ReadAtLeast(source, 1000));
        server.WaitForConnections(2);
        server.WaitForCommands(12);
        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        (await after).Length.Should().Be(1000);

        source.Reconnects.Should().Be(1);
        source.SamplesLost.Should().BeGreaterThanOrEqualTo(251, "the time away, at least one sample, and the 250 settling samples");
        source.TuningGeneration.Should().Be(0, "the tuning is the same as before the drop");
        server.Commands.Where(c => c.Connection == 2).Select(c => c.Command).Should().Equal(
            server.Commands.Where(c => c.Connection == 1).Select(c => c.Command));
        _journal.Should().Contain(l => l.Contains("reconnected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Server_That_Goes_Quiet_Is_Dropped_After_The_Timeout_And_Reconnected()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            NoDataTimeout = TimeSpan.FromSeconds(1),
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(Ramp(100));
        ReadAtLeast(source, 100);

        // The silence is the source's to notice, on the kernel's clock, and the
        // reconnect after it takes a few thread pool hops on both sides: a loaded runner once took
        // longer than the 10 s this used to allow (#712), so the wait is woken by the accept and
        // bounded only by the hang guard. A stall only makes the silence longer, never shorter.
        Task<float[]> after = Task.Run(() => ReadAtLeast(source, 100));
        server.WaitForConnections(2);
        server.Send(Ramp(100));
        await after;

        _journal.Should().Contain(l => l.Contains("sent nothing for", StringComparison.Ordinal));
        source.Reconnects.Should().BeGreaterThanOrEqualTo(1);
        source.SamplesLost.Should().BeGreaterThan(Rate, "at least a second went missing at 250 kS/s");
    }

    [Fact]
    public async Task A_Server_Gone_For_Good_Is_Given_Up_On_And_Said_Once()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            ReconnectDelayCap = TimeSpan.FromMilliseconds(50),
            GiveUpAfter = TimeSpan.FromMilliseconds(500),
        });
        var lost = new ConcurrentQueue<string>();
        source.Lost += lost.Enqueue;
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(Ramp(10));
        ReadAtLeast(source, 10);

        server.Stop();
        int got = await Eventually.SignalAsync(Task.Run(() => source.Read(new float[64])), "Task.Run(() => source.Read(new float[64])) to complete");

        got.Should().Be(0, "a source that has given up is closing");
        lost.Should().ContainSingle().Which.Should().Contain("delivered nothing").And.Contain("Connection refused");
        source.Connected.Should().BeFalse();
        source.SessionLive.Should().BeFalse();
    }

    [Fact]
    public async Task A_Server_That_Takes_The_Connection_And_Sends_Nothing_Is_Given_Up_On_Without_A_Flood()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            ReconnectDelayCap = TimeSpan.FromMilliseconds(200),
            GiveUpAfter = TimeSpan.FromSeconds(1.5),
        });
        var lost = new ConcurrentQueue<string>();
        source.Lost += lost.Enqueue;
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.Send(Ramp(10));
        ReadAtLeast(source, 10);

        // From now on every connection is taken, given a header, and closed at once.
        server.CloseOnAccept = true;
        server.DropClient();
        int got = await Eventually.SignalAsync(Task.Run(() => source.Read(new float[64])), "Task.Run(() => source.Read(new float[64])) to complete");

        got.Should().Be(0);
        lost.Should().ContainSingle().Which.Should().Contain("delivered nothing");
        server.Connections.Should().BeLessThan(20, "a server that accepts and drops is retried with a growing wait, not hammered");
        source.Read(new float[64]).Should().Be(0, "a source that has given up stays closed");
    }

    // A server that takes the connection and never sends the header: the connect is then waiting
    // on the connect timeout, on the source's clock, and only that, a dispose or a cancel.
    private static FakeRtlTcpServer Silent() => new() { Header = null };

    [Fact]
    public async Task A_Connect_That_Gets_No_Header_Gives_Up_When_The_Providers_Connect_Timeout_Passes()
    {
        var clock = new TimerSignalTimeProvider();
        using FakeRtlTcpServer server = Silent();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            ConnectTimeout = TimeSpan.FromMinutes(5),
            TimeProvider = clock,
        });

        Task opening = source.OpenAsync(CancellationToken.None);
        await Eventually.SignalAsync(clock.WhenCreatedAsync(TimeSpan.FromMinutes(5)), "clock.WhenCreatedAsync(TimeSpan.FromMinutes(5)) to complete");
        server.WaitForConnections(1);
        clock.Advance(TimeSpan.FromMinutes(5));

        Func<Task> open = () => opening;
        (await Eventually.SignalAsync(open.Should().ThrowAsync<IOException>(), "open.Should().ThrowAsync<IOException>() to complete")).Which.Message
            .Should().Contain("accepted the connection and then sent nothing in 300 s");
    }

    [Fact]
    public async Task Disposing_While_Connecting_To_A_Host_That_Never_Answers_Ends_The_Wait()
    {
        using FakeRtlTcpServer server = Silent();
        var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            ConnectTimeout = TimeSpan.FromMinutes(5),
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
            GiveUpAfter = TimeSpan.MaxValue,
        });
        Task<int> reading = Task.Run(() => source.Read(new float[64]));
        server.WaitForConnections(1);

        source.Dispose();
        int got = await Eventually.SignalAsync(reading, "reading to complete");

        got.Should().Be(0, "the dispose ends the wait, not the five minute connect timeout");
    }

    [Fact]
    public async Task An_Open_To_A_Host_That_Never_Answers_Is_Ended_By_Cancelling_It()
    {
        using FakeRtlTcpServer server = Silent();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with { ConnectTimeout = TimeSpan.FromMinutes(5) });
        using var cancel = new CancellationTokenSource();

        Task opening = source.OpenAsync(cancel.Token);
        server.WaitForConnections(1);
        await cancel.CancelAsync();

        Func<Task> open = () => opening;
        await Eventually.SignalAsync(open.Should().ThrowAsync<OperationCanceledException>(), "open.Should().ThrowAsync<OperationCanceledException>() to complete");
    }

    [Fact]
    public async Task An_Unreachable_Server_Fails_The_Open_So_Start_Up_Can_Say_So()
    {
        int port;
        using (var server = new FakeRtlTcpServer())
        {
            port = server.Port;
        }

        using var source = new RtlTcpIqSource(new RtlTcpEndpoint("127.0.0.1", port), ReadOnly());
        Func<Task> open = () => source.OpenAsync(CancellationToken.None);

        await open.Should().ThrowAsync<System.Net.Sockets.SocketException>();
    }

    [Fact]
    public async Task A_Retune_Is_Sent_And_The_First_Block_After_It_Is_Marked_New_With_The_Settling_Counted_As_Lost()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled());
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);
        server.WaitForCommands(6);
        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        ReadAtLeast(source, 1000);

        source.Tune(145_000_000);
        server.WaitForCommands(7);
        server.Commands[^1].Should().Be((1, RtlTcpCommand.SetFrequency, 145_000_000u));
        source.CentreFrequencyHz.Should().Be(144_775_000, "nothing at the new tuning has been read yet");
        source.TargetCentreHz.Should().Be(145_000_000);

        server.Send(new byte[500]);
        server.Send(Ramp(1000));
        float[] after = ReadAtLeast(source, 1000);

        after.Length.Should().Be(1000);
        source.CentreFrequencyHz.Should().Be(145_000_000);
        source.TuningGeneration.Should().Be(1);
        source.SamplesLost.Should().Be(250, "1 ms of settling at 250 kS/s is thrown away and counted");
    }

    [Fact]
    public async Task A_Retune_Can_Change_The_Rate_Too()
    {
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, Controlled());
        await source.OpenAsync(CancellationToken.None);
        server.WaitForCommands(6);
        server.Send(new byte[500]);
        server.Send(Ramp(100));
        ReadAtLeast(source, 100);

        source.Tune(145_000_000, 1_024_000);
        server.WaitForCommands(8);

        server.Commands.Skip(6).Select(c => (c.Command, c.Parameter)).Should().Equal(
            (RtlTcpCommand.SetSampleRate, 1_024_000u),
            (RtlTcpCommand.SetFrequency, 145_000_000u));
        server.Send(new byte[2048]);
        server.Send(Ramp(100));
        ReadAtLeast(source, 100);
        source.SampleRate.Should().Be(1_024_000);
        source.TuningGeneration.Should().Be(1);
    }

    [Fact]
    public void A_Rate_No_Dongle_Runs_At_Is_Refused_Before_Anything_Is_Sent()
    {
        Action make = () => _ = new RtlTcpIqSource(new RtlTcpEndpoint("127.0.0.1", 1), Controlled() with { SampleRate = 600_000 });

        make.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*225001 to 300000*");
    }

    [Fact]
    public async Task A_Read_Only_Server_Whose_Rate_Changes_Is_Dropped_Until_It_Is_Right_Again()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            SampleRate = 100_000,
            WatchProfile = true,
            RateWindow = TimeSpan.FromSeconds(1),
            TimeProvider = clock,
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);

        long lastGeneration = 0;
        long lastLost = 0;
        using var reading = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            var buffer = new float[65536];
            while (!reading.IsCancellationRequested && source.Read(buffer) > 0)
            {
                Volatile.Write(ref lastGeneration, source.TuningGeneration);
                Volatile.Write(ref lastLost, source.SamplesLost);
            }
        });

        long sent = 0;
        void Stream(double seconds, int actualRate)
        {
            // A tenth of a second at a time: send it, let the source take it, move the clock on.
            for (int step = 0; step < seconds * 10; step++)
            {
                int bytes = 2 * actualRate / 10;
                server.Send(Ramp(bytes));
                sent += bytes;
                Eventually.Until(() => source.BytesReceived >= sent, "the source to take what was sent");
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }
        }

        Stream(8, 100_000);
        source.ProfileDrops.Should().Be(0, "the server is at the rate it was said to be");
        source.SessionLive.Should().BeTrue();

        Stream(5, 50_000);
        source.ProfileDrops.Should().Be(1, "half the rate it should be is a profile switch");
        source.SessionLive.Should().BeFalse("the station waits while the stream is wrong rather than restarting");
        _journal.Should().Contain(l => l.Contains("profile has probably been switched", StringComparison.Ordinal));

        Stream(5, 100_000);
        source.SessionLive.Should().BeTrue();
        _journal.Should().Contain(l => l.Contains("again", StringComparison.Ordinal));
        Eventually.Until(() => Volatile.Read(ref lastGeneration) == 1, "the first block back, as a fresh start");
        Volatile.Read(ref lastLost).Should().BeGreaterThan(0, "what was dropped is reported as lost");

        reading.Cancel();
        source.Dispose();
        await Eventually.SignalAsync(reader, "reader to complete");
    }

    [Fact]
    public async Task A_Steady_Stream_Is_Read_Without_Allocating()
    {
        const int rate = 1_000_000;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        using var server = new FakeRtlTcpServer();
        using var source = new RtlTcpIqSource(server.Endpoint, ReadOnly() with
        {
            SampleRate = rate,
            WatchProfile = true,
            RateWindow = TimeSpan.FromMilliseconds(500),
            TimeProvider = clock,
        });
        await source.OpenAsync(CancellationToken.None);
        server.WaitForConnections(1);

        // Noise-like bytes, the same block over and over, so the step watch has nothing to say.
        var block = new byte[65536];
        new Random(9).NextBytes(block);
        for (int k = 0; k < block.Length; k++)
        {
            block[k] = (byte)(124 + (block[k] & 7));
        }

        // The feeder sends one eighth of a block each time the reader is about to read, and only
        // then, so a read that finds nothing waiting has a chunk on its way and waits for it: the
        // chunk is a quarter of what a read asks for, so after a read has taken what there is the
        // next one waits again, warm-up included. A read that waits is the one that would allocate
        // on a socket left non-blocking. Nothing is paced by a clock: the reader's own reads are
        // the pace, and a read never waits for a byte that has not been sent after it began.
        using var streaming = new CancellationTokenSource();
        using var wanted = new SemaphoreSlim(0);
        Task feeder = Task.Run(() =>
        {
            try
            {
                while (!streaming.IsCancellationRequested)
                {
                    wanted.Wait(streaming.Token);
                    server.Send(block.AsSpan(0, block.Length / 8));
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException
                                        or OperationCanceledException)
            {
            }
        });

        // The reads move the clock on by exactly what they returned, so the rate watch closes its
        // windows on a stream at its rate and has nothing to say either: the steady state.
        var buffer = new float[16384];
        int ReadWanted()
        {
            wanted.Release();
            return source.Read(buffer);
        }

        void AdvanceBy(int got) => clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond * (got / 2) / rate));

        // Warm-up: a hundred reads, the clock moved on after each.
        for (int read = 0; read < 100; read++)
        {
            int got = ReadWanted();
            got.Should().BeGreaterThan(0);
            AdvanceBy(got);
        }

        // Then stretches of a hundred reads, each through the shared helper. The reads that have to
        // wait for the feeder's next block are the ones that would allocate on a socket left
        // non-blocking by an async connect (measured: about a quarter of the reads), and a stretch
        // holds dozens of them, so a read path that allocates when it waits allocates in every run
        // and fails this whatever the collector does. The clock is moved on by what the stretch read,
        // before the next run, not between its reads, so the rate watch closes one window per stretch
        // on a stream exactly at its rate.
        int pending = 0;
        Action stretch = () =>
        {
            pending = 0;
            for (int read = 0; read < 100; read++)
            {
                pending += ReadWanted();
            }
        };
        Action advance = () =>
        {
            AdvanceBy(pending);
            pending = 0;
        };

        long allocated = AllocationMeasure.Smallest(stretch, advance);
        AdvanceBy(pending);

        await streaming.CancelAsync();
        allocated.Should().Be(0, "a read of a steady stream, rate watch and all, allocates nothing");
        source.ProfileDrops.Should().Be(0);
        source.SuspectedProfileChanges.Should().Be(0);
    }

    private static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (int k = 0; k < length; k++)
        {
            bytes[k] = (byte)(k * 7);
        }

        return bytes;
    }

    private static float[] ReadAtLeast(RtlTcpIqSource source, int floats)
    {
        var result = new List<float>();
        var buffer = new float[Math.Max(2, floats)];
        while (result.Count < floats)
        {
            int got = source.Read(buffer.AsSpan(0, Math.Max(2, floats - result.Count)));
            if (got == 0)
            {
                break;
            }

            result.AddRange(buffer.AsSpan(0, got));
        }

        return [.. result];
    }

    /// <summary>A complex tone at <paramref name="offsetHz"/> relative to DC, as dongle bytes.</summary>
    private static byte[] ComplexToneBytes(int rate, double offsetHz, double seconds)
    {
        int samples = (int)(rate * seconds);
        var bytes = new byte[samples * 2];
        double w = 2.0 * Math.PI * offsetHz / rate;
        for (int k = 0; k < samples; k++)
        {
            bytes[2 * k] = ToByte(Math.Cos(w * k));
            bytes[(2 * k) + 1] = ToByte(Math.Sin(w * k));
        }

        return bytes;

        static byte ToByte(double sample) => (byte)Math.Clamp(Math.Round(127.5 + (127.5 * sample)), 0, 255);
    }

    /// <summary>The magnitude of whatever is at <paramref name="freqHz"/> relative to DC in
    /// <paramref name="interleaved"/>, a complex (I,Q) correlation rather than a real Goertzel: a
    /// complex signal's energy at +f and -f are independent, which is exactly what distinguishes
    /// a tone landing above the centre from one mirrored below it.</summary>
    private static float ComplexMagnitudeAt(ReadOnlySpan<float> interleaved, double freqHz, double rate)
    {
        int samples = interleaved.Length / 2;
        double w = 2.0 * Math.PI * freqHz / rate;
        double sumRe = 0, sumIm = 0;
        for (int k = 0; k < samples; k++)
        {
            double i = interleaved[2 * k], q = interleaved[(2 * k) + 1];
            double c = Math.Cos(w * k), s = Math.Sin(w * k);
            // (i + jq) * exp(-j*w*k)
            sumRe += (i * c) + (q * s);
            sumIm += (q * c) - (i * s);
        }

        return (float)(Math.Sqrt((sumRe * sumRe) + (sumIm * sumIm)) / samples);
    }
}
