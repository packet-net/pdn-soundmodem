using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class MultiDecoderTests
{
    private const int SampleRate = 12000;

    private static byte[] SampleFrame()
    {
        var frame = new byte[40];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(1).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static float[] OffTuneAudio(byte[] frame, double centerOffsetHz)
    {
        // Synthesise mark/space shifted by the offset — an off-tuned transmitter.
        var modulator = new Afsk1200Modulator(SampleRate);
        byte[] bits = HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2);
        float[] audio = modulator.Modulate(bits);

        // Frequency-shift via complex heterodyne (single-sideband-ish is overkill here;
        // remodulating with shifted tones is simpler and exact for FSK):
        var shifted = new Afsk1200ModulatorShifted(SampleRate, centerOffsetHz);
        return shifted.Modulate(bits);
    }

    private sealed class Afsk1200ModulatorShifted(int sampleRate, double shiftHz)
    {
        public float[] Modulate(ReadOnlySpan<byte> hdlcBits)
        {
            var encoder = new NrziEncoder();
            double samplesPerBit = sampleRate / 1200.0;
            var samples = new float[(int)(hdlcBits.Length * samplesPerBit)];
            double phase = 0;
            double clock = 0;
            int position = 0;
            foreach (byte bit in hdlcBits)
            {
                int level = encoder.Encode(bit);
                double tone = (level == 1 ? 1200 : 2200) + shiftHz;
                double step = 2 * Math.PI * tone / sampleRate;
                clock += samplesPerBit;
                while (position < clock && position < samples.Length)
                {
                    phase += step;
                    samples[position++] = 0.8f * (float)Math.Sin(phase);
                }
            }

            return samples[..position];
        }
    }

    [Fact]
    public void An_Off_Tuned_Transmitter_Decodes_On_An_Offset_Branch()
    {
        byte[] frame = SampleFrame();
        float[] audio = OffTuneAudio(frame, centerOffsetHz: 90);
        var padded = new float[audio.Length + 2 * (SampleRate / 5)];
        audio.CopyTo(padded, SampleRate / 5);

        var frames = new List<byte[]>();
        var modem = new Afsk1200MultiModem(SampleRate, frames.Add, offsetPairs: 3);
        modem.Process(padded);

        frames.Should().ContainSingle("the bank decodes it exactly once (dedupe)")
            .Which.Should().Equal(frame);
    }

    [Fact]
    public void An_On_Frequency_Signal_Is_Emitted_Exactly_Once()
    {
        byte[] frame = SampleFrame();
        var modulator = new Afsk1200Modulator(SampleRate);
        float[] audio = modulator.Modulate(HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2));
        var padded = new float[audio.Length + 2 * (SampleRate / 5)];
        audio.CopyTo(padded, SampleRate / 5);

        var frames = new List<byte[]>();
        var modem = new Afsk1200MultiModem(SampleRate, frames.Add, offsetPairs: 3);
        modem.Process(padded);

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Identical_Content_Later_Is_Not_Deduplicated()
    {
        byte[] frame = SampleFrame();
        var modulator = new Afsk1200Modulator(SampleRate);
        float[] one = modulator.Modulate(HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2));

        // Same frame content transmitted twice, 4 seconds apart — both must be delivered.
        var audio = new float[one.Length + 4 * SampleRate + one.Length + SampleRate];
        one.CopyTo(audio, 0);
        one.CopyTo(audio, one.Length + 4 * SampleRate);

        var frames = new List<byte[]>();
        var modem = new Afsk1200MultiModem(SampleRate, frames.Add, offsetPairs: 1);
        modem.Process(audio);

        frames.Should().HaveCount(2);
    }
}
