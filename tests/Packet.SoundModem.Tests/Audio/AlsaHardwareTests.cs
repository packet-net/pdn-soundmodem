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

    [SkippableFact]
    public void Capture_Delivers_Frames()
    {
        Skip.IfNot(HasAudio, "no ALSA capture device on this machine");

        using var pcm = TryOpen(AlsaPcm.Direction.Capture);
        Skip.If(pcm is null, "default capture device would not open (busy or access denied)");

        var buffer = new short[4800]; // 0.1 s mono at 48 kHz
        int frames = pcm!.Read(buffer);

        frames.Should().Be(4800);
    }

    [SkippableFact]
    public void Playback_Accepts_And_Drains_Silence()
    {
        Skip.IfNot(HasAudio, "no ALSA device on this machine");

        using var pcm = TryOpen(AlsaPcm.Direction.Playback);
        Skip.If(pcm is null, "default playback device would not open (busy or access denied)");

        pcm!.Write(new short[4800]);
        pcm.Drain();
    }

    private static AlsaPcm? TryOpen(AlsaPcm.Direction direction)
    {
        try
        {
            return AlsaPcm.Open("default", direction, channels: 1, sampleRate: 48000);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
