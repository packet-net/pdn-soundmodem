using AwesomeAssertions;
using Packet.SoundModem.OfdmFmTool.Ax25;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class LinkTunerTests
{
    private static Tuning At(double outKbit, double backKbit, bool located = true) => LinkTuner.Compute(new LinkView(located, outKbit * 1000, backKbit * 1000));

    [Fact]
    public void Until_the_link_is_located_and_a_rate_is_known_each_way_it_is_one_frame_and_thirty_seconds()
    {
        LinkTuner.Compute(new LinkView(false, null, null)).Should().Be(new Tuning(1, TimeSpan.FromSeconds(30)));
        LinkTuner.Compute(new LinkView(false, 70400, 70400)).Should().Be(new Tuning(1, TimeSpan.FromSeconds(30)), "not located: the rates are not about this peer yet");
        LinkTuner.Compute(new LinkView(true, 70400, null)).Should().Be(new Tuning(1, TimeSpan.FromSeconds(30)), "the way back is not known");
        LinkTuner.Compute(new LinkView(true, null, 70400)).Should().Be(new Tuning(1, TimeSpan.FromSeconds(30)));
        LinkTuner.Compute(new LinkView(true, 0, 70400)).Should().Be(new Tuning(1, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void The_window_is_the_frames_one_burst_carries_at_the_rate_out()
    {
        At(0.25, 0.25).K.Should().Be(1, "a burst at first contact carries about 66 bytes");
        At(12.5, 12.5).K.Should().Be(11, "12.5 kbit/s for 2.15 s is 3359 bytes, 11 frames of 282");
        At(26.1, 26.1).K.Should().Be(24, "7014 bytes a burst, 24 frames of 282");
        At(70.4, 70.4).K.Should().Be(32, "the cap");
    }

    [Fact]
    public void The_window_follows_the_rate_out_only()
    {
        At(70.4, 0.25).K.Should().Be(32);
        At(0.25, 70.4).K.Should().Be(1);
    }

    [Fact]
    public void The_window_never_falls_under_one_or_passes_the_cap()
    {
        At(0.001, 0.001).K.Should().Be(1);
        At(1000, 1000).K.Should().Be(LinkTuner.MostFrames);
    }

    [Fact]
    public void The_timer_covers_the_peers_delay_channel_wait_burst_acknowledgement_and_our_decode_with_a_margin()
    {
        // 1 + 2 + 2.5 + 0.352 + 0.5 = 6.35 s, times 1.5, up to a whole second.
        At(70.4, 70.4).T1V.Should().Be(TimeSpan.FromSeconds(10));
        At(12.5, 12.5).T1V.Should().Be(TimeSpan.FromSeconds(10));

        // At 0.25 kbit/s back the peer may be in the middle of a 9.4 s frame and its acknowledgement takes 1 s: 13.86 s, times 1.5.
        At(0.25, 0.25).T1V.Should().Be(TimeSpan.FromSeconds(21));
    }

    [Fact]
    public void The_timer_is_never_under_six_seconds()
    {
        LinkTuner.Compute(new LinkView(true, 1e9, 1e9)).T1V.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void A_falling_rate_shrinks_the_window_and_lengthens_the_timer()
    {
        Tuning fast = At(70.4, 70.4);
        Tuning middle = At(12.5, 12.5);
        Tuning slow = At(0.25, 0.25);

        fast.K.Should().BeGreaterThan(middle.K);
        middle.K.Should().BeGreaterThan(slow.K);
        slow.T1V.Should().BeGreaterThan(fast.T1V);
    }
}
