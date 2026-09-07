using AwesomeAssertions;
using Packet.SoundModem.TncTest;

namespace Packet.SoundModem.Tests.TncTest;

/// <summary>
/// The FLAC decoder sm-tnctest reads its corpus with. The WA8LMF TNC Test CD tracks are
/// distributed as FLAC, so this sits directly under every benchmark score: audio decoded even
/// slightly wrong would move the numbers without moving anything anyone would look at.
/// </summary>
/// <remarks>
/// Every case round-trips through <see cref="FlacTestWriter"/>, so a stream that both sides
/// agree on wrongly would have to be wrong in two independently written directions. The MD5
/// assertion is the guard against exactly that: the writer hashes the samples it was handed,
/// before encoding, and the reader hashes the samples it decoded, so the check spans the whole
/// encode-decode path rather than either half of it.
/// </remarks>
public class FlacReaderTests
{
    [Theory]
    [InlineData(FlacEncoding.Constant)]
    [InlineData(FlacEncoding.Verbatim)]
    [InlineData(FlacEncoding.Fixed0)]
    [InlineData(FlacEncoding.Fixed1)]
    [InlineData(FlacEncoding.Fixed2)]
    [InlineData(FlacEncoding.Fixed3)]
    [InlineData(FlacEncoding.Fixed4)]
    [InlineData(FlacEncoding.FixedEscaped)]
    [InlineData(FlacEncoding.FixedWasted)]
    [InlineData(FlacEncoding.Lpc)]
    public void Every_Subframe_Shape_Decodes_Back_To_The_Samples_It_Was_Given(FlacEncoding encoding)
    {
        int[] samples = Signal(encoding, 500);

        float[] decoded = RoundTrip(samples, encoding, out bool? verified);

        verified.Should().BeTrue("the stream carries the encoder's own MD5 of these samples");
        decoded.Should().HaveCount(samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            decoded[i].Should().Be(Normalise(samples[i]), $"sample {i} must survive the round trip");
        }
    }

    [Theory]
    [InlineData(1)]                                          // two independent channels
    [InlineData(8)]                                          // left and side
    [InlineData(9)]                                          // side and right
    [InlineData(10)]                                         // mid and side
    public void Both_Channels_Survive_Every_Stereo_Decorrelation(int assignment)
    {
        int[] left = Signal(FlacEncoding.Fixed2, 320);
        var right = new int[left.Length];
        for (int i = 0; i < left.Length; i++)
        {
            // Correlated with the left channel but not equal to it, which is the case the side
            // channel exists for and the case where an off-by-one in the parity bit shows up.
            right[i] = (left[i] / 2) + ((i % 7) - 3);
        }

        byte[] stream = FlacTestWriter.Write(
            [left, right], 44100, 16, FlacEncoding.Fixed2, assignment);
        FlacAudio audio = ReadBytes(stream);

        audio.Md5Verified.Should().BeTrue();
        audio.Channels.Should().HaveCount(2);
        audio.Channels[0].Should().BeEquivalentTo(left.Select(Normalise));
        audio.Channels[1].Should().BeEquivalentTo(right.Select(Normalise));
    }

    [Fact]
    public void A_Stream_Whose_Last_Frame_Is_Short_Decodes_To_Its_Real_Length()
    {
        // 500 samples in blocks of 64 leaves a final frame of 52, which is the ordinary shape of
        // a real file and the one where a decoder that trusts the block size loses the end.
        int[] samples = Signal(FlacEncoding.Fixed2, 500);

        float[] decoded = RoundTrip(samples, FlacEncoding.Fixed2, out _);

        decoded.Should().HaveCount(500);
    }

    [Fact]
    public void The_Declared_Rate_Depth_And_Channel_Count_Come_Back()
    {
        byte[] stream = FlacTestWriter.Write(
            [Signal(FlacEncoding.Fixed2, 128)], 22050, 16, FlacEncoding.Fixed2);

        FlacAudio audio = ReadBytes(stream);

        audio.SampleRate.Should().Be(22050);
        audio.BitsPerSample.Should().Be(16);
        audio.Channels.Should().HaveCount(1);
    }

    [Fact]
    public void A_Corrupted_Frame_Is_Refused_Rather_Than_Decoded()
    {
        // The whole reason this decoder can be trusted at the bottom of a benchmark is that it
        // checks what it reads. A flipped bit in the audio has to be a failure, not a number.
        byte[] stream = FlacTestWriter.Write(
            [Signal(FlacEncoding.Fixed2, 256)], 44100, 16, FlacEncoding.Fixed2);
        stream[^8] ^= 0x01;

        Action reading = () => ReadBytes(stream);

        reading.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_File_That_Is_Not_Flac_Is_Named_As_Such()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(path, "RIFFnot a flac file at all"u8.ToArray());

        try
        {
            Action reading = () => FlacReader.Read(path);

            reading.Should().Throw<InvalidDataException>().WithMessage("*not a FLAC stream*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Something with structure for the predictors to work on: a ramp with a wobble,
    /// which no fixed order predicts exactly, so every residual path is really exercised.</summary>
    private static int[] Signal(FlacEncoding encoding, int count)
    {
        var samples = new int[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = encoding switch
            {
                FlacEncoding.Constant => -1234,
                // Multiples of four, so the two wasted bits the encoder declares are really wasted.
                FlacEncoding.FixedWasted => 4 * (int)(3000 * Math.Sin(i * 0.05)),
                _ => (int)(9000 * Math.Sin(i * 0.03)) + ((i % 11) * 17) - 90,
            };
        }

        return samples;
    }

    private static float[] RoundTrip(int[] samples, FlacEncoding encoding, out bool? verified)
    {
        FlacAudio audio = ReadBytes(FlacTestWriter.Write([samples], 44100, 16, encoding));
        verified = audio.Md5Verified;
        return audio.Channels[0];
    }

    private static float Normalise(int sample) => sample / 32768f;

    private static FlacAudio ReadBytes(byte[] stream)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(path, stream);
        try
        {
            return FlacReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
