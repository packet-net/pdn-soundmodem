using System.Text.Json;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// The reconnect pacing that keeps a months-long receive-only station from hammering
/// somebody else's public receiver. Found the hard way (2026-08-02): a daemon behind an
/// exhausted daily quota met the old fixed one-second breath / crash-restart path and
/// re-asked the instance every few seconds all evening.
/// </summary>
public class UberSdrReconnectPolicyTests
{
    [Fact]
    public void Refusals_Escalate_To_A_Long_Cap_And_Never_Stop_Waiting()
    {
        var policy = new UberSdrReconnectPolicy();

        policy.After(UberSdrReconnectOutcome.Refused).Should().Be(TimeSpan.FromSeconds(60));
        policy.After(UberSdrReconnectOutcome.Refused).Should().Be(TimeSpan.FromSeconds(120));
        policy.After(UberSdrReconnectOutcome.Refused).Should().Be(TimeSpan.FromSeconds(240));
        policy.After(UberSdrReconnectOutcome.Refused).Should().Be(TimeSpan.FromSeconds(480));
        policy.After(UberSdrReconnectOutcome.Refused).Should().Be(TimeSpan.FromMinutes(15));
        // and stays at the cap, however long the quota takes to reset
        for (int i = 0; i < 50; i++)
        {
            policy.After(UberSdrReconnectOutcome.Refused).Should().Be(TimeSpan.FromMinutes(15));
        }
    }

    [Fact]
    public void Instantly_Dying_Sessions_Escalate_Instead_Of_Breathing_One_Second_Forever()
    {
        var policy = new UberSdrReconnectPolicy();

        policy.After(UberSdrReconnectOutcome.ShortSession).Should().Be(TimeSpan.FromSeconds(5));
        policy.After(UberSdrReconnectOutcome.ShortSession).Should().Be(TimeSpan.FromSeconds(10));
        policy.After(UberSdrReconnectOutcome.ShortSession).Should().Be(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 20; i++)
        {
            policy.After(UberSdrReconnectOutcome.ShortSession);
        }

        policy.After(UberSdrReconnectOutcome.ShortSession).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Transient_Failures_Keep_The_Old_Quick_Ladder()
    {
        var policy = new UberSdrReconnectPolicy();

        policy.After(UberSdrReconnectOutcome.Transient).Should().Be(TimeSpan.FromSeconds(1));
        policy.After(UberSdrReconnectOutcome.Transient).Should().Be(TimeSpan.FromSeconds(2));
        policy.After(UberSdrReconnectOutcome.Transient).Should().Be(TimeSpan.FromSeconds(4));
        for (int i = 0; i < 10; i++)
        {
            policy.After(UberSdrReconnectOutcome.Transient);
        }

        policy.After(UberSdrReconnectOutcome.Transient).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void One_Healthy_Session_Resets_The_Ladder()
    {
        var policy = new UberSdrReconnectPolicy();
        for (int i = 0; i < 6; i++)
        {
            policy.After(UberSdrReconnectOutcome.ShortSession);
        }

        policy.After(UberSdrReconnectOutcome.Healthy).Should().Be(TimeSpan.FromSeconds(1));
        policy.After(UberSdrReconnectOutcome.ShortSession).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_Spent_Daily_Quota_Reads_As_Refused_For_Now()
    {
        // The reply Tom's instance actually returned, 2026-08-02.
        var refusal = JsonSerializer.Deserialize<ConnectionResponse>(
            """{"client_ip":"1.2.3.4","allowed":false,"reason":"Invalid or missing user_session_id","session_timeout":10800,"max_session_time":10800,"bypassed":false,"allowed_iq_modes":["iq48"],"daily_time_used_secs":10809,"daily_time_remaining_secs":0}""");

        refusal!.RefusedForNow.Should().BeTrue("daily_time_remaining_secs 0 is a wait, not a config error");
    }

    [Fact]
    public void An_Unmetered_Or_Ordinary_Refusal_Is_Not_Refused_For_Now()
    {
        var unmetered = JsonSerializer.Deserialize<ConnectionResponse>(
            """{"allowed":true,"daily_time_used_secs":0,"daily_time_remaining_secs":-1}""");
        unmetered!.RefusedForNow.Should().BeFalse();

        // No daily fields at all — an instance that does not meter must not read as a quota
        // refusal even when it refuses for a real (config) reason.
        var refusedNoMeter = JsonSerializer.Deserialize<ConnectionResponse>(
            """{"allowed":false,"reason":"password required"}""");
        refusedNoMeter!.RefusedForNow.Should().BeFalse("absent daily fields default to unmetered (-1)");
    }
}
