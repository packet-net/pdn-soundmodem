using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The continuous raw-capture writer: chunks roll on audio time, every chunk is a readable
/// WAV even without a clean shutdown (the crash-tolerance the RIFF patching buys), and the
/// byte budget prunes oldest-first without touching the chunk being written.
/// </summary>
public class RawCaptureWriterTests : IDisposable
{
    private const int Rate = 12000;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"raw-capture-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static float[] Tone(int samples)
    {
        var audio = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            audio[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / Rate);
        }

        return audio;
    }

    [Fact]
    public void Chunks_Roll_On_Audio_Time_And_Read_Back_Sample_Exact()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-06T12:00:00Z"));
        using var writer = new RawCaptureWriter(
            _directory, maxBytes: long.MaxValue, chunkMinutes: 1, Rate, time);

        // One minute fills the first chunk exactly; the next block opens a second.
        writer.Process(Tone(60 * Rate));
        time.Advance(TimeSpan.FromMinutes(1));
        writer.Process(Tone(Rate));
        writer.Dispose();

        string[] chunks = Directory.GetFiles(_directory, "raw-*.wav");
        Array.Sort(chunks, StringComparer.Ordinal);
        chunks.Should().HaveCount(2, "one full minute chunk plus the overflow");
        Path.GetFileName(chunks[0]).Should().Be("raw-20260806T120000Z.wav");

        (float[] first, int rate1) = WavFile.ReadMono(chunks[0]);
        (float[] second, int rate2) = WavFile.ReadMono(chunks[1]);
        rate1.Should().Be(Rate);
        rate2.Should().Be(Rate);
        first.Should().HaveCount(60 * Rate, "the chunk boundary is audio time, sample exact");
        second.Should().HaveCount(Rate);
        first[100].Should().BeApproximately(
            0.5f * MathF.Sin(2 * MathF.PI * 1000 * 100 / Rate), 0.001f,
            "16-bit round-trip preserves the waveform");
    }

    [Fact]
    public void A_Chunk_Is_Readable_Without_A_Clean_Shutdown()
    {
        // No Dispose: the periodic RIFF patch is all a crash leaves behind. Everything up to
        // the last patch interval must read back.
        var writer = new RawCaptureWriter(
            _directory, maxBytes: long.MaxValue, chunkMinutes: 60, Rate);
        writer.Process(Tone(12 * Rate));   // two 5 s patch intervals plus a dangling tail

        string chunk = Directory.GetFiles(_directory, "raw-*.wav").Should().ContainSingle().Which;
        (float[] samples, _) = WavFile.ReadMono(chunk);
        samples.Length.Should().BeGreaterThanOrEqualTo(10 * Rate,
            "audio up to the last size patch survives a crash");
    }

    [Fact]
    public void The_Budget_Prunes_Oldest_Chunks_First()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-06T12:00:00Z"));
        // ~1 minute of 12 kHz s16 is 1.44 MB; a 4 MB budget holds two chunks, not four.
        using var writer = new RawCaptureWriter(
            _directory, maxBytes: 4 * 1024 * 1024, chunkMinutes: 1, Rate, time);
        for (int minute = 0; minute < 4; minute++)
        {
            writer.Process(Tone(60 * Rate));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        writer.Dispose();

        string[] chunks = Directory.GetFiles(_directory, "raw-*.wav");
        Array.Sort(chunks, StringComparer.Ordinal);
        chunks.Should().HaveCountLessThanOrEqualTo(3, "the budget holds");
        Path.GetFileName(chunks[^1]).Should().Be("raw-20260806T120300Z.wav",
            "the newest chunk is never the victim");
        Path.GetFileName(chunks[0]).Should().NotBe("raw-20260806T120000Z.wav",
            "the oldest chunk is");
    }
}
