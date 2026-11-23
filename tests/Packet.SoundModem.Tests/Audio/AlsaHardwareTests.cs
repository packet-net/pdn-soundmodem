using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// Hardware-gated smoke tests: run where a sound device exists (dev boxes, the bench
/// rig), skip on headless CI. These prove the P/Invoke surface against a real libasound,
/// not audio quality.
/// </summary>
public class AlsaHardwareTests
{
    private static bool HasAudio =>
        OperatingSystem.IsLinux() && Directory.Exists("/dev/snd")
        && Directory.EnumerateFiles("/dev/snd", "pcmC*c").Any();

    [Fact]
    public void Capture_Delivers_Frames()
    {
        Assert.SkipUnless(HasAudio, "no ALSA capture device on this machine");

        using var pcm = TryOpen(AlsaPcm.Direction.Capture);
        Assert.SkipWhen(pcm is null, "default capture device would not open (busy or access denied)");

        // The negotiated pair, on a real driver rather than the null plugin: everything the
        // overrun fix does rests on the card taking a buffer that is deeper than the period, and
        // on it saying what it took (the start-up line has nothing to print otherwise).
        pcm!.BufferFrames.Should().BeGreaterThan(
            0, "snd_pcm_get_params has to answer for the start-up line to say anything");
        pcm.PeriodFrames.Should().BeGreaterThan(0);
        pcm.PeriodFrames.Should().BeLessThanOrEqualTo(
            pcm.BufferFrames, "a period bigger than the buffer is not a configuration");

        var buffer = new short[4800]; // 0.1 s mono at 48 kHz
        int frames = pcm.Read(buffer);

        frames.Should().Be(4800);
    }

    [Fact]
    public void Playback_Accepts_And_Drains_Silence()
    {
        Assert.SkipUnless(HasAudio, "no ALSA device on this machine");

        using var pcm = TryOpen(AlsaPcm.Direction.Playback);
        Assert.SkipWhen(pcm is null, "default playback device would not open (busy or access denied)");

        pcm!.Write(new short[4800]);
        pcm.Drain();
    }

    [Fact]
    public void Playback_Runs_On_Between_Transmissions_And_A_Drain_Still_Waits_For_The_Audio()
    {
        Assert.SkipUnless(HasAudio, "no ALSA device on this machine");

        using var pcm = TryOpen(AlsaPcm.Direction.Playback);
        Assert.SkipWhen(pcm is null, "default playback device would not open (busy or access denied)");

        // The stream is fed silence from the moment it is opened (issue #828), so a transmission
        // after a pause longer than the buffer goes into a stream that is already running. What a
        // real libasound has to show is that the drain, which no longer calls snd_pcm_drain,
        // still does not come back before the card has had time to play what was written.
        Thread.Sleep(300);
        var burst = new short[4800]; // 0.1 s mono at 48 kHz
        var clock = System.Diagnostics.Stopwatch.StartNew();
        pcm!.Write(burst);
        pcm.Drain();
        TimeSpan first = clock.Elapsed;

        Thread.Sleep(300);
        clock.Restart();
        pcm.Write(burst);
        pcm.Drain();
        TimeSpan second = clock.Elapsed;

        // However much of the queued silence this device gave back at the start of the
        // transmission, no more than the queue can have been ahead of it.
        pcm.TransmitLeadFrames.Should().BeInRange(0, 48000 * 60 / 1000);
        first.Should().BeGreaterThan(TimeSpan.FromMilliseconds(95), "PTT is released when the drain returns");
        second.Should().BeGreaterThan(TimeSpan.FromMilliseconds(95));
        pcm.Xruns.Should().Be(0, "nothing that was transmitted was lost");
    }

    [Fact]
    public void Playback_On_The_Null_Device_Is_Fed_Drained_And_Closed()
    {
        // No sound card needed, only libasound: its "null" device takes everything at once and
        // reports nothing queued. That is enough to prove the calls the running stream makes on
        // its own thread (snd_pcm_delay, the silence write, the start) against the real library,
        // and that closing stops that thread, on machines where the tests above skip.
        using var pcm = TryOpen(AlsaPcm.Direction.Playback, "null");
        Assert.SkipWhen(pcm is null, "no libasound, or it has no null device");

        Thread.Sleep(200);
        pcm!.Write(new short[4800]);
        pcm.Drain();
        Thread.Sleep(200);
        pcm.Write(new short[4800]);
        pcm.Drain();

        pcm.Xruns.Should().Be(0);
        pcm.IdleUnderruns.Should().Be(0, "a device that never runs dry never underruns");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        pcm.Dispose();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the feeder stops when it is told to");
    }

    private static AlsaPcm? TryOpen(AlsaPcm.Direction direction, string device = "default")
    {
        try
        {
            return AlsaPcm.Open(device, direction, channels: 1, sampleRate: 48000);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException)
        {
            return null;
        }
    }
}
