using M0LTE.Flex;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The Morse identification interval, verified by moving a clock rather than waiting on one.
/// </summary>
/// <remarks>
/// <para>Identification is a licence condition, so "it probably still works" is not good enough —
/// but proving a ten-minute rule against the system clock costs ten minutes per assertion, and
/// nobody runs a suite like that twice. With <see cref="FakeTimeProvider"/> the same proof is
/// instant, which is the difference between a rule that is tested and a rule that is assumed.
/// House rule (CLAUDE.md): wall-clock via <see cref="TimeProvider"/> only.</para>
/// <para>The decision is asserted through <see cref="FlexIqTransmitter.IdentificationDue"/>
/// rather than by keying the mock. That is deliberate, and it is what the injected clock made
/// possible: <c>EnsureIdentifiedAsync</c> ends in a settle delay, and on a fake clock a delay
/// nothing advances never completes — the first version of this test hung on exactly that. The
/// fix was not to pump the clock from the test but to separate the policy from the mechanism,
/// which is a better shape anyway: a ladder wants to know an identification is coming so it can
/// place it in a gap, not discover it mid-pass.</para>
/// </remarks>
public sealed class IdentificationIntervalTests
{
    private static async Task<(MockFlexRadio Mock, FlexClient Client, FlexIqTransmitter Tx)> OpenAsync(
        FakeTimeProvider time, bool identify = true)
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexIqTransmitter tx = await FlexIqTransmitter.AttachAsync(client, new FlexTransmitterOptions
        {
            Radio = "mock",
            FrequencyMHz = "18.098000",
            Antenna = "ANT1",
            RfPower = 1,
            Callsign = "M0LTE",
            Identify = identify,
            Time = time,
        });

        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;
        return (mock, client, tx);
    }

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_session_opens_owing_an_identification()
    {
        FakeTimeProvider time = Clock();
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync(time);
        await using (mock)
        await using (client)
        {
            tx.IdentificationDue.Should().BeTrue(
                "a station that has not identified this session owes one before it transmits");
        }
    }

    [Fact]
    public async Task Having_identified_it_stays_quiet_until_the_interval_elapses()
    {
        FakeTimeProvider time = Clock();
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync(time);
        await using (mock)
        await using (client)
        {
            await tx.IdentifyAsync();
            tx.LastIdentifiedUtc.Should().NotBeNull();

            tx.IdentificationDue.Should().BeFalse("it has just identified");

            time.Advance(TimeSpan.FromMinutes(9));
            tx.IdentificationDue.Should().BeFalse("nine minutes is inside the ten-minute rule");

            time.Advance(TimeSpan.FromMinutes(1));
            tx.IdentificationDue.Should().BeTrue(
                "at ten minutes the station owes another identification — this is the licence "
                + "condition, not a preference");
        }
    }

    [Fact]
    public async Task The_interval_runs_from_the_last_identification_not_from_the_session_start()
    {
        // Two hours into a long pass, the question is still "how long since the last one".
        FakeTimeProvider time = Clock();
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync(time);
        await using (mock)
        await using (client)
        {
            await tx.IdentifyAsync();
            time.Advance(TimeSpan.FromHours(2));
            tx.IdentificationDue.Should().BeTrue();

            await tx.IdentifyAsync();
            tx.IdentificationDue.Should().BeFalse("the clock restarts at each identification");

            time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
            tx.IdentificationDue.Should().BeFalse();
            time.Advance(TimeSpan.FromSeconds(1));
            tx.IdentificationDue.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Identification_is_off_only_when_it_has_been_turned_off_deliberately()
    {
        // Off is a legitimate choice into a dummy load, but it must be an explicit one: the
        // default is on precisely so that going on the air unidentified takes an act of will.
        FakeTimeProvider time = Clock();
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync(time, identify: false);
        await using (mock)
        await using (client)
        {
            tx.IdentificationDue.Should().BeFalse();
            await tx.EnsureIdentifiedAsync();
            tx.LastIdentifiedUtc.Should().BeNull();
        }

        var defaults = new FlexTransmitterOptions { RfPower = 1 };
        defaults.Identify.Should().BeTrue("the default must be to identify");
        defaults.IdentifyInterval.Should().Be(TimeSpan.FromMinutes(10));
        defaults.Time.Should().BeSameAs(TimeProvider.System,
            "the injected clock is for tests and for pacing; production runs on the real one");
    }
}
