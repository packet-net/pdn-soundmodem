using AwesomeAssertions;
using M0LTE.Flex;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The <see cref="SwrInterlock"/> measured-forward-power ceiling - the enforcement behind a strict
/// transmit-power limit (the 5 W ceiling on the attenuated RSP1 rig). A FLEX meter packet carrying
/// FWDPWR is pushed through the mock's in-process delivery, exactly as the radio would emit it, and
/// the interlock must abort the instant the measured power is over the limit and stay quiet while it
/// is under. This is proven here rather than on the air because discovering a broken guard by
/// exceeding the limit on a real transmitter is the one outcome it exists to prevent.
/// </summary>
public sealed class SwrInterlockGuardTests
{
    // FWDPWR is meter id 6 in the mock's FLEX-6500 meter set, unit dBm, scaled raw/128 - so a raw of
    // dBm×128 yields that dBm, and DbmToWatts turns it into watts (37 dBm ≈ 5 W).
    private const int FwdPwrId = 6;

    private static short Dbm(double dbm) => (short)Math.Round(dbm * 128.0);

    private static async Task<(MockFlexRadio Mock, FlexClient Client, FlexMeters Meters)> ArmAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        mock.RxDelivery = client.DeliverVitaPacket; // in-process meter delivery, like the transmit tests
        FlexMeters meters = await FlexMeters.SubscribeAsync(client);
        return (mock, client, meters);
    }

    /// <summary>Waits until the interlock has taken in <paramref name="count"/> forward-power readings.
    /// It records a reading and judges it under one lock, so once a reading is counted its verdict is
    /// already in <see cref="SwrInterlock.AbortReason"/>: a test that looks after this has seen what the
    /// interlock made of what it was pushed, rather than guessed at how long that takes.</summary>
    private static Task ForwardReadingsAsync(SwrInterlock interlock, int count) =>
        Eventually.UntilAsync(
            () => interlock.Collected.Count(r => r.Descriptor.Name.Equals("FWDPWR", StringComparison.OrdinalIgnoreCase)) >= count,
            $"the interlock to take in {count} forward-power reading(s)");

    [Fact]
    public async Task Forward_power_over_the_ceiling_aborts()
    {
        (MockFlexRadio mock, FlexClient client, FlexMeters meters) = await ArmAsync();
        await using (mock)
        await using (client)
        {
            // Envelope false (a data burst): the power ceiling must hold regardless of the SWR
            // path's constant-envelope precondition, and the SWR limit is set high so only power
            // can trip it.
            using var interlock = new SwrInterlock(
                meters, maxSwr: 99.0, constantEnvelope: false, maxForwardWatts: 5.0);
            interlock.AbortReason.Should().BeNull();

            // 40 dBm = 10 W, twice the ceiling.
            mock.PushMeters((FwdPwrId, Dbm(40)));

            await Eventually.UntilAsync(() => interlock.AbortReason is not null, "the interlock to abort on 10 W");
            string? reason = interlock.AbortReason;
            reason.Should().NotBeNull("10 W is over the 5 W ceiling");
            reason.Should().Contain("W").And.Contain("5.0");
        }
    }

    [Fact]
    public async Task Forward_power_under_the_ceiling_does_not_abort()
    {
        (MockFlexRadio mock, FlexClient client, FlexMeters meters) = await ArmAsync();
        await using (mock)
        await using (client)
        {
            using var interlock = new SwrInterlock(
                meters, maxSwr: 99.0, constantEnvelope: false, maxForwardWatts: 5.0);

            // 36 dBm ≈ 4.0 W and 30 dBm = 1 W - both under 5 W, neither may abort.
            mock.PushMeters((FwdPwrId, Dbm(30)));
            mock.PushMeters((FwdPwrId, Dbm(36)));
            await ForwardReadingsAsync(interlock, 2);

            interlock.AbortReason.Should().BeNull("4 W stays under the 5 W ceiling");
            interlock.PeakForwardDbm.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task With_no_ceiling_configured_forward_power_never_aborts()
    {
        (MockFlexRadio mock, FlexClient client, FlexMeters meters) = await ArmAsync();
        await using (mock)
        await using (client)
        {
            // No maxForwardWatts (the dummy-load default): high power is collected but never a fault.
            using var interlock = new SwrInterlock(meters, maxSwr: 99.0, constantEnvelope: false);

            mock.PushMeters((FwdPwrId, Dbm(45))); // ~32 W
            await ForwardReadingsAsync(interlock, 1);

            interlock.AbortReason.Should().BeNull("no power ceiling was set");
        }
    }
}
