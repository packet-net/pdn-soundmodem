using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

public class WavFileTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"pdn-soundmodem-test-{Guid.NewGuid():N}.wav");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Write_Then_Read_Roundtrips_Within_Quantisation()
    {
        var random = new Random(1);
        var samples = new float[4800];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(random.NextDouble() * 1.8 - 0.9);
        }

        WavFile.WriteMono(_path, samples, 12000);
        var (read, sampleRate) = WavFile.ReadMono(_path);

        sampleRate.Should().Be(12000);
        read.Should().HaveCount(samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            Math.Abs(read[i] - samples[i]).Should().BeLessThan(1.5f / 32768, $"sample {i}");
        }
    }

    [Fact]
    public void Values_Are_Clipped_To_Full_Scale()
    {
        WavFile.WriteMono(_path, [2f, -2f, 0f], 8000);
        var (read, _) = WavFile.ReadMono(_path);

        read[0].Should().BeApproximately(1f, 0.01f);
        read[1].Should().BeApproximately(-1f, 0.01f);
        read[2].Should().Be(0f);
    }

    [Fact]
    public void Garbage_Files_Are_Rejected()
    {
        File.WriteAllBytes(_path, new byte[100]);
        var act = () => WavFile.ReadMono(_path);
        act.Should().Throw<InvalidDataException>();
    }
}
