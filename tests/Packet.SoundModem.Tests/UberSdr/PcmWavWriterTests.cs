using System.Buffers.Binary;
using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.UberSdr;

namespace Packet.SoundModem.Tests.UberSdr;

/// <summary>Verifies the streaming stereo WAV writer produces a valid 48 kHz 16-bit RIFF file
/// with correct size fields, readable by the repo's own <see cref="WavFile"/> reader.</summary>
public class PcmWavWriterTests
{
    private static byte[] StereoFrames((short I, short Q)[] frames)
    {
        var b = new byte[frames.Length * 4];
        for (int i = 0; i < frames.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 4), frames[i].I);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 4 + 2), frames[i].Q);
        }
        return b;
    }

    [Fact]
    public void Writes_Valid_Stereo_Wav_With_Patched_Sizes()
    {
        (short I, short Q)[] frames = [(1000, -1000), (32767, -32768), (0, 0), (-5, 5)];
        string path = Path.Combine(Path.GetTempPath(), $"iqcap-{Guid.NewGuid():N}.wav");
        try
        {
            using (var w = new PcmWavWriter(path, 48000))
            {
                w.Write(StereoFrames(frames));       // one bulk write
                w.Write(StereoFrames([(7, -7)]));    // and an appended write
                w.FramesWritten.Should().Be(frames.Length + 1);
            }

            byte[] file = File.ReadAllBytes(path);
            file.Length.Should().Be(44 + (frames.Length + 1) * 4);
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)).Should().Be((uint)(file.Length - 8));   // RIFF size
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40)).Should().Be((uint)((frames.Length + 1) * 4)); // data size
            BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(22)).Should().Be(2);      // channels
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)).Should().Be(48000);  // sample rate

            // The repo's own reader accepts it and recovers each channel.
            (float[] i, int rate) = WavFile.ReadMono(path, channel: 0);
            (float[] q, _) = WavFile.ReadMono(path, channel: 1);
            rate.Should().Be(48000);
            i[0].Should().BeApproximately(1000 / 32768f, 1e-6f);
            q[1].Should().BeApproximately(-32768 / 32768f, 1e-6f);
            i[4].Should().BeApproximately(7 / 32768f, 1e-6f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
