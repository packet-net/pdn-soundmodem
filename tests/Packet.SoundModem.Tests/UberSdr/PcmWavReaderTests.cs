using System.Buffers.Binary;
using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>
/// The chunked WAV reader that lets an hour-long capture be converted at all. Its contract is
/// that it hands out exactly what <see cref="WavFile.ReadMono"/> would, in pieces.
/// </summary>
public class PcmWavReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pcmwavreader").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteCapture(int frames, int channels, int sampleRate = 48000)
    {
        string path = Path.Combine(_dir, $"cap{frames}x{channels}.wav");
        using var w = new PcmWavWriter(path, sampleRate, channels);
        var bytes = new byte[frames * channels * 2];
        var rng = new Random(frames + channels);
        for (int i = 0; i < frames * channels; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)rng.Next(-30000, 30000));
        }

        w.Write(bytes);
        return path;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1024)]
    [InlineData(100000)]
    public void It_reads_the_same_samples_the_whole_file_reader_does(int blockFrames)
    {
        string path = WriteCapture(frames: 5000, channels: 2);
        (float[] expectedI, _) = WavFile.ReadMono(path, channel: 0);
        (float[] expectedQ, _) = WavFile.ReadMono(path, channel: 1);

        using var reader = new PcmWavReader(path);
        var buffer = new short[blockFrames * 2];
        var gotI = new List<float>();
        var gotQ = new List<float>();
        int frames;
        while ((frames = reader.ReadFrames(buffer)) > 0)
        {
            for (int k = 0; k < frames; k++)
            {
                gotI.Add(buffer[2 * k] / 32768f);
                gotQ.Add(buffer[2 * k + 1] / 32768f);
            }
        }

        reader.SampleRate.Should().Be(48000);
        reader.Channels.Should().Be(2);
        reader.FrameCount.Should().Be(5000);
        gotI.Should().Equal(expectedI);
        gotQ.Should().Equal(expectedQ);
    }

    [Fact]
    public void It_reports_the_frame_count_before_anything_is_read()
    {
        // The converter sizes its report from this without touching the data.
        string path = WriteCapture(frames: 1234, channels: 2);
        using var reader = new PcmWavReader(path);

        reader.FrameCount.Should().Be(1234);
        reader.FramesRead.Should().Be(0);
    }

    [Fact]
    public void A_capture_cut_off_mid_write_is_read_as_far_as_it_goes()
    {
        // Every interrupted session leaves one of these: the header claims more data than the
        // file holds. Refusing it would throw away a pass that is otherwise perfectly good.
        string path = WriteCapture(frames: 2000, channels: 2);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(44 + (1500 * 4)); // header says 2000 frames, only 1500 survive
        }

        using var reader = new PcmWavReader(path);
        var buffer = new short[512 * 2];
        long total = 0;
        int frames;
        while ((frames = reader.ReadFrames(buffer)) > 0)
        {
            total += frames;
        }

        reader.FrameCount.Should().Be(1500);
        total.Should().Be(1500);
    }

    [Fact]
    public void Mono_files_are_read_too()
    {
        string path = WriteCapture(frames: 300, channels: 1, sampleRate: 9600);
        (float[] expected, _) = WavFile.ReadMono(path);

        using var reader = new PcmWavReader(path);
        var buffer = new short[300];
        int frames = reader.ReadFrames(buffer);

        reader.Channels.Should().Be(1);
        reader.SampleRate.Should().Be(9600);
        frames.Should().Be(300);
        for (int k = 0; k < 300; k++)
        {
            (buffer[k] / 32768f).Should().Be(expected[k]);
        }
    }

    [Fact]
    public void A_destination_that_is_not_a_whole_number_of_frames_is_refused()
    {
        string path = WriteCapture(frames: 100, channels: 2);
        using var reader = new PcmWavReader(path);

        Action odd = () => reader.ReadFrames(new short[7]);

        odd.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Something_that_is_not_a_wav_file_is_refused()
    {
        string path = Path.Combine(_dir, "not.wav");
        File.WriteAllBytes(path, "this is not a RIFF file at all, not even close"u8.ToArray());

        Action open = () => new PcmWavReader(path).Dispose();

        open.Should().Throw<InvalidDataException>();
    }
}
