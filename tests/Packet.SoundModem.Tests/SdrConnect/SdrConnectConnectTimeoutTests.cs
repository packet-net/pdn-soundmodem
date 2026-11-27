using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Packet.SoundModem.Iq;
using Packet.SoundModem.SdrConnect;

namespace Packet.SoundModem.Tests.SdrConnect;

/// <summary>The connect limit is a timer on the clock the client was given.</summary>
public sealed class SdrConnectConnectTimeoutTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_Port_That_Takes_The_Connection_And_Never_Answers_Is_Given_Up_On_When_The_Providers_Connect_Timeout_Passes()
    {
        // A listener that takes the connection and never speaks WebSocket.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var endpoint = new SdrConnectEndpoint("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port);
        var clock = new TimerSignalTimeProvider();
        TimeSpan limit = TimeSpan.FromMinutes(5);

        Task<SdrConnectIqInput> opening = SdrConnectIqInput.OpenAsync(
            endpoint,
            new SdrConnectOptions { CentreFrequencyHz = 144_800_000, ConnectTimeout = limit, TimeProvider = clock },
            IqChannelSpec.ForFmMode("afsk1200", 0),
            new IqReceiverOptions { DspRate = 12000 },
            144_800_000,
            TestContext.Current.CancellationToken);

        await Eventually.SignalAsync(clock.WhenCreatedAsync(limit), "clock.WhenCreatedAsync(limit) to complete");
        clock.Advance(limit);

        Func<Task> open = () => opening;
        (await Eventually.SignalAsync(open.Should().ThrowAsync<SdrConnectUnreachableException>(), "open.Should().ThrowAsync<SdrConnectUnreachableException>() to complete")).Which.Reason
            .Should().Be(SdrConnectFailure.NoAnswer);
    }
}
