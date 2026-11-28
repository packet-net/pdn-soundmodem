using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.OfdmFmTool;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class RecorderAndPttTests
{
    [Fact]
    public async Task The_recording_is_the_audio_the_modem_was_given_as_48_kHz_mono()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pdn-ofdm-{Guid.NewGuid():N}.wav");
        try
        {
            await using (var recorder = new AudioRecorder(path, 48000))
            {
                recorder.Tap(new float[] { 0.5f, -0.5f, 0.25f });
                recorder.Tap(new float[480]);
            }

            (float[] samples, int rate) = WavFile.ReadMono(path);
            rate.Should().Be(48000);
            samples.Should().HaveCount(483);
            samples[0].Should().BeApproximately(0.5f, 0.001f);
            samples[1].Should().BeApproximately(-0.5f, 0.001f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_serial_ptt_spec_is_read_with_its_line()
    {
        PttConfig config = PttSpec.TryParse("serial:/dev/ttyUSB0:dtr", out string problem)!;

        problem.Should().BeEmpty();
        (config.Type, config.Device, config.Line, config.Gpio).Should().Be(("serial", "/dev/ttyUSB0", "dtr", null));
    }

    [Fact]
    public void A_cm108_ptt_spec_is_read_with_its_gpio()
    {
        PttConfig config = PttSpec.TryParse("cm108:/dev/hidraw0:2", out _)!;

        (config.Type, config.Device, config.Gpio).Should().Be(("cm108", "/dev/hidraw0", 2));
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("cm108:/dev/hidraw0:x")]
    public void A_malformed_ptt_spec_is_refused_in_the_daemons_words(string spec)
    {
        PttSpec.TryParse(spec, out string problem).Should().BeNull();
        problem.Should().Be(PttSpec.Expected);
    }
}
