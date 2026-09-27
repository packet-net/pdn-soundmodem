using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Linux.Tests;

public class MixerTests
{
    /// <summary>An AIOC as snd-usb-audio presents it: one control each way, named for itself.</summary>
    private static FakeMixer Aioc(double inLevel = 0, double outLevel = 0, bool inOn = true, bool outOn = true) => new FakeMixer()
        .Add("AIOC Audio Out Volume", playback: (-96, 0, outLevel), playbackSwitch: outOn)
        .Add("AIOC Audio In", capture: (-96, 0, inLevel), captureSwitch: inOn);

    /// <summary>A CM108: "Mic" has a capture side and a playback (monitor) side, and the gain
    /// goes above 0 dB.</summary>
    private static FakeMixer Cm108(double micCapture = 10, bool micMonitor = true, bool agc = true) => new FakeMixer()
        .Add("Speaker", playback: (-37, 0, -6), playbackSwitch: true)
        .Add("Mic", capture: (-12, 23, micCapture), playback: (-23, 8, 0), captureSwitch: true, playbackSwitch: micMonitor)
        .Add("Auto Gain Control", globalSwitch: agc);

    [Fact]
    public void An_aiocs_own_control_names_are_found_by_what_they_do()
    {
        MixerRoles roles = MixerHygiene.Identify(Aioc());

        roles.Capture.Should().Be("AIOC Audio In");
        roles.Playback.Should().Be("AIOC Audio Out Volume");
        roles.Monitors.Should().BeEmpty();
        roles.Agc.Should().BeEmpty();
    }

    [Fact]
    public void A_cm108s_mic_is_the_receive_level_and_a_monitor_path_and_never_the_transmit_level()
    {
        MixerRoles roles = MixerHygiene.Identify(Cm108());

        roles.Capture.Should().Be("Mic");
        roles.Playback.Should().Be("Speaker");
        roles.Monitors.Should().Equal("Mic");
        roles.Agc.Should().Equal("Auto Gain Control");
    }

    [Fact]
    public void A_well_set_aioc_has_nothing_wrong()
    {
        MixerHygiene.Check(Aioc(inLevel: -3, outLevel: -10)).Should().BeEmpty();
    }

    [Fact]
    public void Closing_the_monitor_path_leaves_the_receiver_hearing()
    {
        FakeMixer mixer = Cm108();

        IReadOnlyList<MixerIssue> remaining = MixerHygiene.Fix(mixer);

        remaining.Should().BeEmpty();
        mixer.SwitchOf("Mic", MixerDirection.Playback).Should().BeFalse("the monitor path is closed");
        mixer.SwitchOf("Mic", MixerDirection.Capture).Should().BeTrue("and the receive side is untouched");
        mixer.GlobalSwitchOf("Auto Gain Control").Should().BeFalse();
        mixer.VolumeOf("Mic", MixerDirection.Capture)!.Level.Should().Be(0, "nothing above 0 dB");
    }

    [Fact]
    public void A_monitor_with_no_switch_is_turned_all_the_way_down()
    {
        FakeMixer mixer = new FakeMixer()
            .Add("Speaker", playback: (-37, 0, -6))
            .Add("Line", capture: (-12, 0, 0), playback: (-40, 0, -10));

        MixerHygiene.Fix(mixer).Should().BeEmpty();

        mixer.VolumeOf("Line", MixerDirection.Playback)!.Level.Should().Be(-40);
    }

    [Fact]
    public void Muted_sides_are_unmuted_and_levels_above_zero_come_down()
    {
        FakeMixer mixer = new FakeMixer()
            .Add("PCM", playback: (-40, 6, 4), playbackSwitch: false)
            .Add("Capture", capture: (-20, 30, 12), captureSwitch: false);

        IReadOnlyList<MixerIssue> before = MixerHygiene.Check(mixer);
        IReadOnlyList<MixerIssue> after = MixerHygiene.Fix(mixer);

        before.Should().HaveCount(4).And.OnlyContain(i => i.CanFix);
        after.Should().BeEmpty();
        mixer.SwitchOf("PCM", MixerDirection.Playback).Should().BeTrue();
        mixer.SwitchOf("Capture", MixerDirection.Capture).Should().BeTrue();
        mixer.VolumeOf("PCM", MixerDirection.Playback)!.Level.Should().Be(0);
        mixer.VolumeOf("Capture", MixerDirection.Capture)!.Level.Should().Be(0);
    }

    [Fact]
    public void Each_fix_is_reported_as_it_is_made()
    {
        var fixes = new List<string>();

        MixerHygiene.Fix(Cm108(), fixes.Add);

        fixes.Should().HaveCount(3);
        fixes.Should().Contain(f => f.Contains("automatic gain control", StringComparison.Ordinal));
        fixes.Should().Contain(f => f.Contains("monitored to the output", StringComparison.Ordinal));
        fixes.Should().Contain(f => f.Contains("above 0 dB", StringComparison.Ordinal));
    }

    [Fact]
    public void A_control_with_no_db_scale_is_said_rather_than_guessed_at()
    {
        FakeMixer mixer = new FakeMixer()
            .Add("Speaker", playback: (-37, 0, -6))
            .Add("Mic", captureWithoutScale: true);

        MixerIssue issue = MixerHygiene.Fix(mixer).Should().ContainSingle().Subject;

        issue.CanFix.Should().BeFalse();
        issue.Description.Should().Contain("no dB scale").And.Contain("alsamixer");
    }

    [Fact]
    public void A_card_whose_bottom_step_is_above_zero_says_so()
    {
        FakeMixer mixer = new FakeMixer()
            .Add("Speaker", playback: (-37, 0, -6))
            .Add("Mic", capture: (6, 30, 20));

        IReadOnlyList<MixerIssue> remaining = MixerHygiene.Fix(mixer);

        mixer.VolumeOf("Mic", MixerDirection.Capture)!.Level.Should().Be(6);
        remaining.Should().ContainSingle(i => !i.CanFix && i.Description.Contains("cannot go below +6.0 dB", StringComparison.Ordinal));
    }

    [Fact]
    public void A_level_is_clamped_to_its_ceiling()
    {
        var time = new FakeTimeProvider();
        FakeMixer mixer = new FakeMixer().Add("Capture", capture: (-20, 30, -5));
        using AlsaLevel level = AlsaLevel.Open(mixer, "Capture", MixerDirection.Capture, time)!;

        level.CeilingDb.Should().Be(0);
        level.LevelDb = 12;
        level.LevelDb.Should().Be(0);
        level.LevelDb = -30;
        level.LevelDb.Should().Be(-20);
    }

    [Fact]
    public void A_level_raised_above_zero_elsewhere_is_put_back()
    {
        var time = new FakeTimeProvider();
        FakeMixer mixer = new FakeMixer().Add("Capture", capture: (-20, 30, -5));
        using AlsaLevel level = AlsaLevel.Open(mixer, "Capture", MixerDirection.Capture, time)!;
        int changes = 0;
        level.Changed += () => changes++;

        mixer.SetElsewhere("Capture", MixerDirection.Capture, 9);
        time.Advance(TimeSpan.FromMilliseconds(600));

        mixer.VolumeOf("Capture", MixerDirection.Capture)!.Level.Should().Be(0);
        level.LevelDb.Should().Be(0);
        changes.Should().Be(1);
    }

    [Fact]
    public void A_level_moved_elsewhere_is_noticed()
    {
        var time = new FakeTimeProvider();
        FakeMixer mixer = Aioc(inLevel: -3);
        using AlsaLevel level = AlsaLevel.Open(mixer, "AIOC Audio In", MixerDirection.Capture, time)!;
        int changes = 0;
        level.Changed += () => changes++;

        mixer.SetElsewhere("AIOC Audio In", MixerDirection.Capture, -12);
        time.Advance(TimeSpan.FromMilliseconds(600));

        level.LevelDb.Should().Be(-12);
        changes.Should().Be(1);
    }

    [Fact]
    public void A_control_with_no_db_scale_has_no_level_control()
    {
        AlsaLevel.Open(new FakeMixer().Add("Mic", captureWithoutScale: true), "Mic", MixerDirection.Capture).Should().BeNull();
    }
}
