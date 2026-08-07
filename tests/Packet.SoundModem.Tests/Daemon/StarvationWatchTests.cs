using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The starvation watch: no samples delivered for a threshold of wall clock declares the feed
/// starved. Wall clock is an injected TimeProvider, so every case here advances a fake clock
/// rather than sleeping through one.
/// </summary>
public class StarvationWatchTests
{
    [Fact]
    public void Fires_Exactly_Once_When_Nothing_Is_Delivered_For_The_Threshold()
    {
        var clock = new FakeTimeProvider();
        var watch = new StarvationWatch(clock, thresholdSeconds: 30);

        clock.Advance(TimeSpan.FromSeconds(29));
        watch.IsStarved().Should().BeFalse("29 s without samples is not yet starvation");

        clock.Advance(TimeSpan.FromSeconds(1));
        watch.IsStarved().Should().BeTrue("the threshold has been reached");

        clock.Advance(TimeSpan.FromMinutes(10));
        watch.IsStarved().Should().BeFalse("a latched watch must not spam the journal every poll");
    }

    [Fact]
    public void A_Delivery_Restarts_The_Clock()
    {
        var clock = new FakeTimeProvider();
        var watch = new StarvationWatch(clock, thresholdSeconds: 30);

        clock.Advance(TimeSpan.FromSeconds(29));
        watch.NoteDelivery();

        clock.Advance(TimeSpan.FromSeconds(29));
        watch.IsStarved().Should().BeFalse("the clock restarted at the delivery");

        clock.Advance(TimeSpan.FromSeconds(1));
        watch.IsStarved().Should().BeTrue();
    }

    [Fact]
    public void After_Firing_A_Recovered_Feed_Rearms_For_The_Next_Starvation()
    {
        var clock = new FakeTimeProvider();
        var watch = new StarvationWatch(clock, thresholdSeconds: 30);

        clock.Advance(TimeSpan.FromSeconds(30));
        watch.IsStarved().Should().BeTrue();

        watch.NoteDelivery();
        watch.IsStarved().Should().BeFalse("samples arrived - the feed recovered");

        clock.Advance(TimeSpan.FromSeconds(30));
        watch.IsStarved().Should().BeTrue("a second starvation after recovery must be reported again");
    }

    [Fact]
    public void Expected_Quiet_Holds_The_Clock_Without_Samples()
    {
        // An UberSDR between sessions (reconnect backoff, a spent daily quota) delivers
        // nothing on purpose; the daemon declares that through NoteExpectedQuiet, and the
        // watch must not read it as death - a starvation restart there re-asks a public
        // receiver with a fresh session every threshold.
        var clock = new FakeTimeProvider();
        var watch = new StarvationWatch(clock, thresholdSeconds: 30);

        for (int i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(29));
            watch.NoteExpectedQuiet();
            watch.IsStarved().Should().BeFalse("deliberate quiet is not starvation");
        }

        // The wait ends (a session opens) and delivery still does not resume: now it counts.
        clock.Advance(TimeSpan.FromSeconds(30));
        watch.IsStarved().Should().BeTrue();
    }

    [Fact]
    public void A_Disabled_Or_Nonsense_Threshold_Is_Refused_At_Construction()
    {
        // "Off" is the daemon not constructing a watch at all - a configured 0 resolves to
        // that in DeadFeedConfig.Resolve - so the watch itself refuses a threshold it could
        // only misinterpret.
        var clock = new FakeTimeProvider();

        Action zero = () => _ = new StarvationWatch(clock, thresholdSeconds: 0);
        Action negative = () => _ = new StarvationWatch(clock, thresholdSeconds: -5);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
