using M0LTE.Flex;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// Issue #790's root cause: tearing down a Flex station after the radio has ended its session.
/// </summary>
/// <remarks>
/// The station's teardown sends a best-effort <c>slice remove</c> and waits for the reply with no
/// timeout. A <see cref="FlexClient"/> whose read loop has ended fails only the commands pending
/// at that moment, so the reply to one sent afterwards never comes and the teardown waited for
/// ever: GB7RDG's daemon said it was stopping and then sat off the air until SIGKILL. Here the
/// mock radio closes the session the way a radio that drops its clients does.
/// </remarks>
public sealed class FlexRuntimeTeardownTests
{
    [Fact]
    public async Task Disposing_After_The_Radio_Ended_The_Session_Completes()
    {
        var mock = new MockFlexRadio(
            DaxStreamFormat.ForDspRate(48000), MockRxMode.Loopback, MockSetupMode.Headless);
        mock.Start();
        FlexRuntime runtime = await FlexDevice.OpenAsync(
            $"flex:127.0.0.1:{mock.TcpPort}", dspRate: 48000, packetBuffer: 3,
            tuning: new FlexTuning(), cancellation: CancellationToken.None);

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Station.Client.Disconnected += () => ended.TrySetResult();

        await mock.DisposeAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task teardown = runtime.DisposeAsync().AsTask();
        Task first = await Task.WhenAny(teardown, Task.Delay(TimeSpan.FromSeconds(10)));

        first.Should().BeSameAs(teardown, "a session that has already ended has nothing to wait for");
        await teardown;
    }
}
