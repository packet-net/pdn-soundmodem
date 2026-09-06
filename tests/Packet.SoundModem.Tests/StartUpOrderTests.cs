using AwesomeAssertions;

namespace Packet.SoundModem.Tests;

/// <summary>
/// Rules about the order the daemon does things in at start-up, asserted against the source text.
/// </summary>
/// <remarks>
/// <para>The same trick as <see cref="SourceTextTests"/>, and for the same reason: the property
/// is real, it is not expressible as a unit test, and it is cheaper to read the file than to
/// leave it unguarded. The ordering here lives in top-level statements that no test exercises,
/// so nothing else in the suite would notice it moving.</para>
/// <para>Ugly, and much better than nothing for a rule whose violation is invisible in CI and
/// intermittent on hardware.</para>
/// </remarks>
public class StartUpOrderTests
{
    /// <summary>
    /// Every ALSA mixer call must be finished with before the capture stream is opened.
    /// </summary>
    /// <remarks>
    /// <para>Opening a capture stream does not start it: the kernel starts the endpoint on the
    /// first <c>snd_pcm_readi</c>, and on a USB card that start is a URB submission which comes
    /// back as <c>-EIO</c> if the device is busy with a control transfer at that moment. Mixer
    /// traffic is control transfers. So mixer work between <c>snd_pcm_open</c> and the first read
    /// sits in a window where reading the card's own levels can stop the card ever delivering
    /// audio.</para>
    /// <para>Measured on radio1, 2026-09-06, with the mixer pass inside that window: 10 runs dead
    /// out of 13, against 12 of 12 alive for the same source doing two fewer reads per control;
    /// with the pass moved ahead of the open, 10 of 10 alive. It presents as flaky hardware, not
    /// as a bug, and it cost thirteen bench runs to find.</para>
    /// <para>This is one tidy-up away from coming back - somebody grouping "all the audio device
    /// setup" together would reintroduce it - so it is pinned here rather than left to the
    /// comment at the block and the roadmap entry. See CLAUDE.md and docs/roadmap.md #17.</para>
    /// </remarks>
    [Fact]
    public void The_Mixer_Is_Read_And_Set_Before_The_Capture_Stream_Is_Opened()
    {
        string program = Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs");
        string source = File.ReadAllText(program);

        int mixerOpened = source.IndexOf("AlsaMixer.TryOpen(mixerCard", StringComparison.Ordinal);
        int mixerWorked = source.IndexOf("MixerRuntime.Start(", StringComparison.Ordinal);
        int captureOpened = source.IndexOf("new AlsaAudioInput(", StringComparison.Ordinal);
        int playbackOpened = source.IndexOf("new AlsaAudioOutput(", StringComparison.Ordinal);

        mixerOpened.Should().BeGreaterThan(
            -1, "the station's mixer is opened with AlsaMixer.TryOpen(mixerCard, ...); if that "
              + "moved or was renamed, this test needs updating rather than deleting");
        mixerWorked.Should().BeGreaterThan(
            -1, "the mixer is read and set by MixerRuntime.Start(...); if that moved or was "
              + "renamed, this test needs updating rather than deleting");
        captureOpened.Should().BeGreaterThan(
            -1, "the capture stream is opened with new AlsaAudioInput(...)");
        playbackOpened.Should().BeGreaterThan(
            -1, "the playback stream is opened with new AlsaAudioOutput(...)");

        // The boundary is whichever stream is opened FIRST on the device, not the capture one.
        // Playback is constructed a few lines earlier on the same card, so anchoring on the
        // capture construction alone would leave room to slip mixer work between the two.
        int streamOpened = Math.Min(captureOpened, playbackOpened);

        mixerOpened.Should().BeLessThan(
            streamOpened,
            "the mixer is opened before either stream on the device, which is where the whole "
            + "block belongs");

        // Both, because the open is not the work. Opening the mixer is a handful of calls; the
        // dB range reads, the sets and the read-backs are the bulk of the control-transfer
        // traffic and they are inside MixerRuntime.Start. Pinning only the open would let
        // somebody name the card in the journal early and defer the apply, which is the shape a
        // future refactor would most plausibly take and puts the traffic straight back in the
        // window. Pin the LAST mixer call, which is what the rule actually says.
        mixerWorked.Should().BeLessThan(
            streamOpened,
            "every ALSA mixer call must finish BEFORE the capture stream is opened. Mixer traffic "
            + "is USB control transfers, and the kernel starts the capture endpoint on the first "
            + "snd_pcm_readi; a control transfer in flight at that moment makes the URB "
            + "submission fail with -EIO and the station never receives. It is intermittent, so "
            + "it presents as flaky hardware: 10 dead runs out of 13 on the bench CM108, against "
            + "10 of 10 alive with the mixer pass ahead of the open. Nothing in the mixer pass "
            + "needs the PCM - --mixer-show reads a card on a running station - so keep the whole "
            + "block above the AlsaAudioOutput/AlsaAudioInput construction");
    }

    /// <summary>
    /// The mixer block also has to sit above the transmit stream, which is opened first of the
    /// two and is the same PCM device.
    /// </summary>
    [Fact]
    public void The_Mixer_Is_Also_Read_Before_The_Playback_Stream_Is_Opened()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Packet.SoundModem.Daemon", "Program.cs"));

        int playbackOpened = source.IndexOf("new AlsaAudioOutput(", StringComparison.Ordinal);

        source.IndexOf("AlsaMixer.TryOpen(mixerCard", StringComparison.Ordinal)
            .Should().BeLessThan(
                playbackOpened,
                "the playback stream is opened just before the capture one, on the same device");

        // As above: the work, not just the open.
        source.IndexOf("MixerRuntime.Start(", StringComparison.Ordinal)
            .Should().BeLessThan(
                playbackOpened,
                "MixerRuntime.Start is where the card is actually read and set, so it is the call "
                + "that has to be finished with before either stream on the device is opened");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
