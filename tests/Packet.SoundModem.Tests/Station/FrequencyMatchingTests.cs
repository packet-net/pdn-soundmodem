using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Station;

namespace Packet.SoundModem.Tests.Station;

/// <summary>
/// Answering an off-frequency station on its own frequency, and knowing when to stop.
/// </summary>
/// <remarks>
/// <para>The measured case this exists for: on the live 40 m station GB7WEM-7 sat at -3.7 Hz
/// with 0.6 Hz of spread across 467 frames, and GB7OXF-2 at -2.8 with 0.7, while GB7NOT ranged
/// over 54 Hz. The first two are rig characteristics worth correcting for; the third is a rig
/// that will not sit still.</para>
/// <para>The hazard is two stations both correcting. That does not run away, it settles - on
/// both of them being wrong. For a true difference D with each applying a fraction k, the pair
/// lands at kD/(1+k) and -kD/(1+k), so at k = 0.5 and D = 5 Hz each transmits 1.7 Hz off and
/// each still hears the other 3.3 Hz out, worse than if one had stayed put. Hence the detector.
/// </para>
/// </remarks>
public sealed class FrequencyMatchingTests
{
    private static StationFrequencyOffsets Offsets(FakeTimeProvider time) =>
        new(time) { MaxSamples = 8, MaxAge = TimeSpan.FromMinutes(10) };

    private static void Hear(StationFrequencyOffsets o, string call, params double[] offsets)
    {
        foreach (double hz in offsets)
        {
            o.Record(call, hz);
        }
    }

    [Fact]
    public void A_settled_station_gets_the_whole_correction()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        Hear(offsets, "GB7WEM-7", -3.6, -3.7, -3.8, -3.7);

        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3,
            MinMeaningfulTrimHz = 0.5,
        });

        // All of it. Nothing is looping - our transmitter is not in the path by which we
        // measure theirs - so there is nothing for damping to stabilise, and holding back would
        // just leave them half as wrong for no reason.
        policy.TrimFor("GB7WEM-7").Should().BeApproximately(-3.7, 0.05);
    }

    [Fact]
    public void A_wandering_station_is_left_alone()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        // GB7NOT's real shape: a rig that will not sit still.
        Hear(offsets, "GB7NOT", -13.7, 40.6, 8.0, -2.0);

        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3,
            MaxSpreadHz = 20,
        });

        policy.TrimFor("GB7NOT").Should().Be(0);
    }

    [Fact]
    public void Too_few_frames_is_not_enough_to_act_on()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        Hear(offsets, "EI0RSI-1", 4.5, 4.6);

        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3,
        });

        policy.TrimFor("EI0RSI-1").Should().Be(0);
    }

    [Fact]
    public void Estimates_go_stale_rather_than_persisting_forever()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        Hear(offsets, "EI0RSI-1", 4.5, 4.6, 4.4, 4.5);
        offsets.TryGet("EI0RSI-1", out StationOffset? fresh).Should().BeTrue();
        fresh!.Value.Samples.Should().Be(4);

        time.Advance(TimeSpan.FromMinutes(11));

        offsets.TryGet("EI0RSI-1", out StationOffset? _).Should().BeFalse();
    }

    /// <summary>The one this was written for.</summary>
    [Fact]
    public void A_station_that_corrects_back_is_given_up_on()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3,
            ChaseThresholdHz = 10,
            MinMeaningfulTrimHz = 2,
        });

        var stoodDown = new List<FrequencyMatchingStandDown>();
        policy.StoodDown += s => stoodDown.Add(s);

        // A peer sitting 20 Hz high, settled. We start answering it 10 Hz off.
        Hear(offsets, "M0ABC-1", 20.0, 20.1, 19.9, 20.0);
        policy.TrimFor("M0ABC-1").Should().BeApproximately(20.0, 0.2);
        stoodDown.Should().BeEmpty("nothing has moved yet");

        // It answers our correction by moving its own transmitter. Our transmitter cannot change
        // what we measure of theirs, so this movement is theirs - they are correcting too.
        time.Advance(TimeSpan.FromSeconds(30));
        // Eight frames, because the estimate is an eight-deep window: a station that moves is
        // only seen to have moved once the window has filled with its new position. On air it
        // just keeps transmitting, so that is a few more frames, not a special case.
        Hear(offsets, "M0ABC-1", 8.0, 7.9, 8.1, 8.0, 8.0, 7.9, 8.1, 8.0);

        policy.TrimFor("M0ABC-1").Should().Be(0);
        policy.IsCoolingDown("M0ABC-1").Should().BeTrue();
        policy.HasRetired("M0ABC-1").Should().BeFalse("one move is a rig that moved, not a chase");
        stoodDown.Should().ContainSingle();
        stoodDown[0].Callsign.Should().Be("M0ABC-1");
        stoodDown[0].Chases.Should().Be(1);
        stoodDown[0].RetryAfter.Should().NotBeNull();
    }

    /// <summary>Somebody knocks the dial on Tuesday and notices on Thursday.</summary>
    [Fact]
    public void A_station_that_simply_moved_is_corrected_for_again_at_its_new_frequency()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3, ChaseThresholdHz = 10, MinMeaningfulTrimHz = 2,
            ChaseCooldown = TimeSpan.FromMinutes(30),
        });

        Hear(offsets, "M0ABC-1", 20.0, 20.0, 20.0);
        policy.TrimFor("M0ABC-1").Should().BeApproximately(20.0, 0.2, "nothing has moved yet");

        // The dial gets knocked: they move once, to somewhere else, and stay there.
        time.Advance(TimeSpan.FromSeconds(30));
        Hear(offsets, "M0ABC-1", -30.0, -30.0, -30.0, -30.0, -30.0, -30.0, -30.0, -30.0);
        policy.TrimFor("M0ABC-1").Should().Be(0, "we back off while we work out what happened");
        policy.IsCoolingDown("M0ABC-1").Should().BeTrue();

        // After the cooldown they are still sitting quietly on their new frequency, so they get
        // corrected for there. A rig that moved is not a rig that argues - but it HAS moved once
        // under our correction, and that is the only evidence we get of a peer that reacts, so
        // this one is damped from here on where an untouched station would not be.
        time.Advance(TimeSpan.FromMinutes(31));
        Hear(offsets, "M0ABC-1", -30.0, -30.0, -30.0, -30.0);

        policy.TrimFor("M0ABC-1").Should().BeApproximately(-15.0, 0.2, "half of -30, having moved once");
        policy.HasRetired("M0ABC-1").Should().BeFalse();
    }

    [Fact]
    public void A_station_that_keeps_moving_every_time_we_correct_is_eventually_left_alone()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3, ChaseThresholdHz = 10, MinMeaningfulTrimHz = 2,
            ChaseCooldown = TimeSpan.FromMinutes(30), MaxChases = 3,
        });

        var stoodDown = new List<FrequencyMatchingStandDown>();
        policy.StoodDown += s => stoodDown.Add(s);

        // Three rounds of: we correct, they move in response, we back off and try again later.
        // That is not a rig that was knocked, it is the far end running this same algorithm.
        double[] positions = [40.0, -40.0, 40.0, -40.0];
        for (int round = 0; round < 3; round++)
        {
            Hear(offsets, "M0ABC-1", Enumerable.Repeat(positions[round], 8).ToArray());
            policy.TrimFor("M0ABC-1").Should().NotBe(0, "round {0} should start correcting", round);

            time.Advance(TimeSpan.FromSeconds(30));
            Hear(offsets, "M0ABC-1", Enumerable.Repeat(positions[round + 1], 8).ToArray());
            policy.TrimFor("M0ABC-1").Should().Be(0, "round {0} should detect the move", round);

            time.Advance(TimeSpan.FromMinutes(31));
        }

        policy.HasRetired("M0ABC-1").Should().BeTrue();
        stoodDown.Should().HaveCount(3);
        stoodDown[^1].RetryAfter.Should().BeNull("the last one is for good");
        stoodDown[^1].Detail.Should().Contain("correcting for us in turn");

        // And it stays that way, however settled they look afterwards.
        Hear(offsets, "M0ABC-1", 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0);
        policy.TrimFor("M0ABC-1").Should().Be(0);
    }

    [Fact]
    public void The_trim_is_capped_however_far_off_the_station_is()
    {
        // The safety cap, and the reason this can be on by default: it bounds the damage on its
        // own, without relying on the chase detector working.
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3, MaxTrimHz = 50, Damping = 1.0, MaxSpreadHz = 20,
        });

        Hear(offsets, "M0FAR", 200.0, 200.0, 200.0, 200.0);
        policy.TrimFor("M0FAR").Should().Be(50);

        Hear(offsets, "M0FAR2", -200.0, -200.0, -200.0, -200.0);
        policy.TrimFor("M0FAR2").Should().Be(-50);
    }

    [Fact]
    public void A_station_we_never_corrected_for_is_not_blamed_for_drifting()
    {
        // Below the meaningful-trim floor we are not really correcting, so movement is theirs to
        // do. Without that floor the detector would give up on every wandering rig on the band.
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3, ChaseThresholdHz = 10, MinMeaningfulTrimHz = 2, Damping = 0.5,
        });

        Hear(offsets, "G0XYZ", 1.0, 1.0, 1.0);
        policy.TrimFor("G0XYZ").Should().BeApproximately(1.0, 0.1);

        time.Advance(TimeSpan.FromSeconds(30));
        Hear(offsets, "G0XYZ", 15.0, 15.0, 15.0, 15.0, 15.0, 15.0, 15.0, 15.0);

        policy.IsCoolingDown("G0XYZ").Should().BeFalse();
        policy.HasRetired("G0XYZ").Should().BeFalse();
        policy.TrimFor("G0XYZ").Should().BeApproximately(15.0, 0.2);
    }

    [Fact]
    public void Frames_replayed_from_a_log_keep_their_own_age()
    {
        // Seeding a restart: the frames come back with the timestamps they were heard at, so the
        // age window still governs. A short restart carries its knowledge over; a long outage
        // seeds nothing, which is the honest answer rather than a stale one.
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        DateTimeOffset now = time.GetUtcNow();

        // Heard two minutes before the restart, and an hour before it.
        offsets.Record("GB7WEM-7", -3.7, now - TimeSpan.FromMinutes(2));
        offsets.Record("GB7WEM-7", -3.8, now - TimeSpan.FromMinutes(2));
        offsets.Record("GB7WEM-7", -3.6, now - TimeSpan.FromMinutes(2));
        offsets.Record("GB7BPQ", -17.9, now - TimeSpan.FromHours(1));
        offsets.Record("GB7BPQ", -17.5, now - TimeSpan.FromHours(1));
        offsets.Record("GB7BPQ", -18.2, now - TimeSpan.FromHours(1));

        offsets.TryGet("GB7WEM-7", out StationOffset? recent).Should().BeTrue();
        recent!.Value.Samples.Should().Be(3);
        recent.Value.OffsetHz.Should().BeApproximately(-3.7, 0.1);

        offsets.TryGet("GB7BPQ", out StationOffset? stale).Should()
            .BeFalse("an hour old is well outside the window, restart or no restart");
        stale.Should().BeNull();
    }

    [Fact]
    public void A_seeded_station_can_be_corrected_for_immediately()
    {
        // The point of seeding: the first frame we send after a restart can already be aimed,
        // rather than waiting to re-learn a station we have heard hundreds of times.
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        DateTimeOffset justBefore = time.GetUtcNow() - TimeSpan.FromMinutes(1);
        foreach (double hz in new[] { -2.8, -2.9, -2.7, -2.8 })
        {
            offsets.Record("GB7OXF-2", hz, justBefore);
        }

        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3, MinMeaningfulTrimHz = 0.5,
        });

        policy.TrimFor("GB7OXF-2").Should().BeApproximately(-2.8, 0.1);
    }

    /// <summary>Why the damping is conditional at all.</summary>
    [Fact]
    public void Damping_arrives_only_once_a_station_has_shown_it_reacts()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, timeProvider: time, options: new FrequencyMatchingOptions
        {
            MinSamples = 3, ChaseThresholdHz = 10, MinMeaningfulTrimHz = 2,
            ChaseCooldown = TimeSpan.FromMinutes(30), Damping = 0.5,
        });

        // A station that has never moved: nothing is looping, so it gets all of it.
        Hear(offsets, "M0AAA", 30.0, 30.0, 30.0, 30.0);
        policy.TrimFor("M0AAA").Should().BeApproximately(30.0, 0.2);

        // One that has moved under our correction: damped from here on, because moving under a
        // correction is the only evidence we get that the far end is correcting too.
        Hear(offsets, "M0BBB", 30.0, 30.0, 30.0, 30.0);
        policy.TrimFor("M0BBB").Should().BeApproximately(30.0, 0.2);
        time.Advance(TimeSpan.FromSeconds(30));
        Hear(offsets, "M0BBB", 8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 8.0, 8.0);
        policy.TrimFor("M0BBB").Should().Be(0, "backing off");

        time.Advance(TimeSpan.FromMinutes(31));
        Hear(offsets, "M0BBB", 30.0, 30.0, 30.0, 30.0, 30.0, 30.0, 30.0, 30.0);
        policy.TrimFor("M0BBB").Should().BeApproximately(15.0, 0.2);

        // And the untouched station is still on the full correction; one station's behaviour
        // does not change how anybody else is treated. Heard afresh, because half an hour has
        // passed in this test and its earlier frames have aged out of the window - which is the
        // tracker doing its job, not an inconvenience.
        Hear(offsets, "M0AAA", 30.0, 30.0, 30.0, 30.0);
        policy.TrimFor("M0AAA").Should().BeApproximately(30.0, 0.2);
    }

    [Fact]
    public void An_unheard_station_gets_no_correction()
    {
        var time = new FakeTimeProvider();
        var policy = new FrequencyMatchingPolicy(Offsets(time), timeProvider: time);
        policy.TrimFor("NOBODY").Should().Be(0);
        policy.TrimFor(null).Should().Be(0);
    }

    [Fact]
    public void An_absurd_offset_is_not_recorded_at_all()
    {
        // A wild branch win on a marginal frame measures nobody's oscillator.
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        Hear(offsets, "M0ABC", 5.0, 5.0, 900.0, 5.0);

        offsets.TryGet("M0ABC", out StationOffset? o).Should().BeTrue();
        o!.Value.Samples.Should().Be(3);
        o.Value.OffsetHz.Should().BeApproximately(5.0, 0.01);
    }

}
