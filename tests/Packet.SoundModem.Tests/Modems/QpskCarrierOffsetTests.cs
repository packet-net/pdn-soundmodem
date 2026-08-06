using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The QPSK family's carrier-offset reading - the measurement <see cref="BpskDemodulator"/>
/// gained in the issue #202 work and <see cref="QpskDemodulator"/> did not, which left every
/// qpsk600/2400/3600 frame (the live NinoTNC network's primary modes) reporting no offset at
/// all: an operator diagnosing an off-frequency station got a reading on bpsk300 links and
/// nothing on QPSK ones.
/// </summary>
public class QpskCarrierOffsetTests
{
    private const int SampleRate = 12000;

    private static byte[] SampleFrame()
    {
        var frame = new byte[16 + 48];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(3).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static FrameQuality DecodeShifted(double transmitCarrierHz, PskDetector detector)
    {
        // An off-tuned transmitter, synthesised exactly: the same modulator, carried a few
        // hertz from where the receiver expects it.
        var tx = QpskModem.Qpsk2400(SampleRate, _ => { }, carrierFrequency: transmitCarrierHz);
        float[] audio = tx.Modulate(SampleFrame(), 100);
        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);

        var qualities = new List<FrameQuality>();
        var rx = QpskModem.Qpsk2400(SampleRate, _ => { }, detector: detector);
        rx.FrameDecoded += (_, q) => qualities.Add(q);
        rx.Process(padded);

        qualities.Should().ContainSingle();
        return qualities[0];
    }

    /// <summary>The shift is per-detector: the coherent Costas loop only pulls in a few
    /// hertz (the qpsk3600 factory documents ~5), while the differential detector has no
    /// loop to pull and reads a bigger error just as well.</summary>
    [Theory]
    [InlineData(PskDetector.Coherent, 5.0)]
    [InlineData(PskDetector.Differential, 10.0)]
    public void A_High_Station_Reads_High(PskDetector detector, double shiftHz)
    {
        FrameQuality quality = DecodeShifted(1500 + shiftHz, detector);
        quality.FrequencyOffsetHz.Should().NotBeNull();
        quality.FrequencyOffsetHz!.Value.Should().BeApproximately(
            shiftHz, shiftHz / 2, "the station really is {0} Hz high of the receiver", shiftHz);
    }

    [Theory]
    [InlineData(PskDetector.Coherent)]
    [InlineData(PskDetector.Differential)]
    public void An_On_Frequency_Station_Reads_Near_Zero(PskDetector detector)
    {
        FrameQuality quality = DecodeShifted(1500, detector);
        quality.FrequencyOffsetHz.Should().NotBeNull();
        Math.Abs(quality.FrequencyOffsetHz!.Value).Should().BeLessThan(3);
    }
}
