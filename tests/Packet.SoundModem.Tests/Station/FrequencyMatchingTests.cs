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
    public void A_settled_station_gets_a_damped_correction()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        Hear(offsets, "GB7WEM-7", -3.6, -3.7, -3.8, -3.7);

        var policy = new FrequencyMatchingPolicy(offsets, new FrequencyMatchingOptions
        {
            MinSamples = 3,
            MinMeaningfulTrimHz = 0.5,
        });

        // Damped by half: enough to help them, short of a full swing on one estimate.
        policy.TrimFor("GB7WEM-7").Should().BeApproximately(-1.85, 0.05);
    }

    [Fact]
    public void A_wandering_station_is_left_alone()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        // GB7NOT's real shape: a rig that will not sit still.
        Hear(offsets, "GB7NOT", -13.7, 40.6, 8.0, -2.0);

        var policy = new FrequencyMatchingPolicy(offsets, new FrequencyMatchingOptions
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

        var policy = new FrequencyMatchingPolicy(offsets, new FrequencyMatchingOptions
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
        var policy = new FrequencyMatchingPolicy(offsets, new FrequencyMatchingOptions
        {
            MinSamples = 3,
            ChaseThresholdHz = 10,
            MinMeaningfulTrimHz = 2,
        });

        var stoodDown = new List<FrequencyMatchingStandDown>();
        policy.StoodDown += s => stoodDown.Add(s);

        // A peer sitting 20 Hz high, settled. We start answering it 10 Hz off.
        Hear(offsets, "M0ABC-1", 20.0, 20.1, 19.9, 20.0);
        policy.TrimFor("M0ABC-1").Should().BeApproximately(10.0, 0.2);
        stoodDown.Should().BeEmpty("nothing has moved yet");

        // It answers our correction by moving its own transmitter. Our transmitter cannot change
        // what we measure of theirs, so this movement is theirs - they are correcting too.
        time.Advance(TimeSpan.FromSeconds(30));
        // Eight frames, because the estimate is an eight-deep window: a station that moves is
        // only seen to have moved once the window has filled with its new position. On air it
        // just keeps transmitting, so that is a few more frames, not a special case.
        Hear(offsets, "M0ABC-1", 8.0, 7.9, 8.1, 8.0, 8.0, 7.9, 8.1, 8.0);

        policy.TrimFor("M0ABC-1").Should().Be(0);
        policy.HasStoodDown("M0ABC-1").Should().BeTrue();
        stoodDown.Should().ContainSingle();
        stoodDown[0].Callsign.Should().Be("M0ABC-1");
        stoodDown[0].Detail.Should().Contain("correcting for us");
    }

    [Fact]
    public void Standing_down_is_permanent_even_if_the_station_settles_again()
    {
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, new FrequencyMatchingOptions
        {
            MinSamples = 3, ChaseThresholdHz = 10, MinMeaningfulTrimHz = 2,
        });

        Hear(offsets, "M0ABC-1", 20.0, 20.0, 20.0);
        policy.TrimFor("M0ABC-1").Should().BeApproximately(10.0, 0.2);
        time.Advance(TimeSpan.FromSeconds(30));
        Hear(offsets, "M0ABC-1", 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0);
        policy.TrimFor("M0ABC-1").Should().Be(0);

        // Back to a rock-steady 20 Hz. We still do not resume: whatever moved once will move
        // again, and one side correcting is the outcome worth having.
        time.Advance(TimeSpan.FromSeconds(30));
        Hear(offsets, "M0ABC-1", 20.0, 20.0, 20.0, 20.0, 20.0, 20.0, 20.0, 20.0);
        policy.TrimFor("M0ABC-1").Should().Be(0);
        policy.HasStoodDown("M0ABC-1").Should().BeTrue();
    }

    [Fact]
    public void A_station_we_never_corrected_for_is_not_blamed_for_drifting()
    {
        // Below the meaningful-trim floor we are not really correcting, so movement is theirs to
        // do. Without that floor the detector would give up on every wandering rig on the band.
        var time = new FakeTimeProvider();
        StationFrequencyOffsets offsets = Offsets(time);
        var policy = new FrequencyMatchingPolicy(offsets, new FrequencyMatchingOptions
        {
            MinSamples = 3, ChaseThresholdHz = 10, MinMeaningfulTrimHz = 2, Damping = 0.5,
        });

        Hear(offsets, "G0XYZ", 1.0, 1.0, 1.0);
        policy.TrimFor("G0XYZ").Should().BeApproximately(0.5, 0.1);

        time.Advance(TimeSpan.FromSeconds(30));
        Hear(offsets, "G0XYZ", 15.0, 15.0, 15.0, 15.0, 15.0, 15.0, 15.0, 15.0);

        policy.HasStoodDown("G0XYZ").Should().BeFalse();
        policy.TrimFor("G0XYZ").Should().BeApproximately(7.5, 0.2);
    }

    [Fact]
    public void An_unheard_station_gets_no_correction()
    {
        var time = new FakeTimeProvider();
        var policy = new FrequencyMatchingPolicy(Offsets(time));
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
