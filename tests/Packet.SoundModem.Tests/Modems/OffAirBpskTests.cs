using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Real off-air regression: decode the committed GB7RDG NinoTNC BPSK300 IL2P+CRC capture
/// (<c>samples/offair/</c>) - the ground-truth interop evidence behind issues #40/#42. This is a
/// live-RF frame with a real carrier offset (~8 Hz here) and a short preamble, so it exercises the
/// receiver against imperfections the synthetic loopback corpus never carries.
/// </summary>
/// <remarks>
/// The capture decodes with the <b>differential</b> detector. Its coherent counterpart is left as
/// the interop cross-check documented in #40/#42: the captured connected-mode frame's preamble is
/// too short for the narrow Costas loop to acquire even on-frequency, and widening the loop enough
/// to acquire it in time forfeits the coherent noise margin and breaks the QtSM interop corpus.
/// Off-frequency coherent acquisition is handled instead by <see cref="BpskMultiModem"/> (see
/// <see cref="BpskMultiModemTests"/>); this test is the honest guard that the real frame still
/// decodes at all.
/// </remarks>
public class OffAirBpskTests
{
    private const int DspRate = 12000;

    // GB7RDG-2>EI0RSI-1, CRC-valid IL2P - the connected-mode frame in the capture.
    private static readonly byte[] ExpectedFrame = Convert.FromHexString("8A9260A4A692E28E846EA4888E6571");

    private static float[] Gb7rdgFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        string path = Path.Combine(dir!.FullName, "samples", "offair", "gb7rdg-ninotnc-bpsk300-il2pc.wav");
        var (raw, rate) = WavFile.ReadMono(path);
        // Production path: FlexRadio DAX 48 kHz → decimate → 12 kHz channel DSP rate.
        var decimator = new Decimator(rate, rate / DspRate);
        var samples = new float[decimator.MaxOutput(raw.Length)];
        int produced = decimator.Process(raw, samples);
        var padded = new float[produced + DspRate / 2]; // flush tail so the final frame drains
        Array.Copy(samples, padded, produced);
        return padded;
    }

    [Fact]
    public void Real_Gb7rdg_Ninotnc_Bpsk300_Frame_Decodes()
    {
        var frames = new List<byte[]>();
        var modem = BpskModem.Bpsk300(DspRate, frames.Add, crc: true, PskDetector.Differential);

        modem.Process(Gb7rdgFixture());

        frames.Should().Contain(f => f.SequenceEqual(ExpectedFrame),
            "the committed off-air GB7RDG NinoTNC frame must still decode (CRC-valid IL2P)");
    }

    [Fact]
    public void Real_Gb7rdg_Frame_Addresses_Are_Gb7rdg_To_Ei0rsi()
    {
        var frames = new List<byte[]>();
        BpskModem.Bpsk300(DspRate, frames.Add, crc: true, PskDetector.Differential)
            .Process(Gb7rdgFixture());

        byte[] frame = frames.Should().ContainSingle().Subject;
        DecodeAddress(frame, 0).Should().Be("EI0RSI-1"); // AX.25 destination first
        DecodeAddress(frame, 7).Should().Be("GB7RDG-2"); // then source
    }

    /// <summary>
    /// The offset the shipping decode path puts on this frame agrees with the standalone
    /// estimator's independent reading of the same audio.
    /// </summary>
    /// <remarks>
    /// Real RF is where issue #202 was visible and where the fix has to hold: the bank used to
    /// report the winning branch's comb position, which for this frame would be a step of the
    /// 7.5 Hz comb rather than the ~8 Hz the carrier actually sits at. The two paths share no
    /// selection logic - the estimator runs one centred chain over the whole file, the bank picks
    /// a branch and adds its residual - so agreement between them is a real cross-check.
    /// </remarks>
    [Fact]
    public void The_Bank_Reports_The_Measured_Offset_Of_A_Real_Off_Air_Frame()
    {
        var host = new List<byte[]>();
        var qualities = new List<FrameQuality>();
        var modem = BpskMultiModem.Bpsk300(DspRate, host.Add);
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(Gb7rdgFixture());

        host.Should().ContainSingle("only the first burst verifies, and only it is the host's")
            .Which.Should().Equal(ExpectedFrame);
        qualities[0].MonitorOnly.Should().BeFalse();
        qualities[0].FrequencyOffsetHz.Should().BeApproximately(
            8, 4, "the captured GB7RDG carrier sits ~8 Hz above the 1500 Hz centre");
    }

    /// <summary>
    /// The capture holds the connected-mode frame <em>twice</em>, 12.5 seconds apart, and only the
    /// first one's trailing CRC verifies. The second is now reported and withheld rather than
    /// discarded in silence.
    /// </summary>
    /// <remarks>
    /// <para>This is the change of behaviour on real RF, on audio that has been committed here
    /// since issue #40 and was until now believed to hold one frame. Every branch demodulates the
    /// second burst perfectly - identical bytes, zero FEC corrections - and no branch can make its
    /// four trailer bytes check out, so an IL2P+CRC modem threw it away and nothing anywhere said
    /// a burst had been read and dropped. A station's own retransmission with a damaged trailer
    /// and a CRC-less neighbour are indistinguishable at the receiver, which is exactly why the
    /// frame is shown rather than delivered.</para>
    /// <para>Two identical frames 12.5 s apart are also not a duplicate to be deduped: that is
    /// ordinary AX.25 retry behaviour, and the bank's content dedupe window is deliberately a few
    /// seconds wide for the same reason.</para>
    /// </remarks>
    [Fact]
    public void The_Second_Burst_In_The_Capture_Is_Reported_As_Plain_And_Withheld()
    {
        var host = new List<byte[]>();
        var qualities = new List<FrameQuality>();
        var modem = BpskMultiModem.Bpsk300(DspRate, host.Add);
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(Gb7rdgFixture());

        qualities.Should().HaveCount(2, "the capture holds the frame twice");
        FrameQuality second = qualities[1];
        second.CrcValid.Should().BeNull("its trailer would not check out on any branch");
        second.CorrectedBytes.Should().Be(0, "the burst itself demodulated perfectly");
        second.PlainIl2p.Should().BeTrue("Reed-Solomon alone stood behind this reading");
        second.MonitorOnly.Should().BeTrue(
            "so the operator sees it and the host does not, which is the whole split");
        host.Should().ContainSingle("the host is told about the burst that verified, and no other");
    }

    [Fact]
    public void Estimated_Carrier_Offset_Is_About_Eight_Hz()
    {
        var estimator = new BpskCarrierOffsetEstimator(DspRate, 1500, 300);
        estimator.Process(Gb7rdgFixture());

        estimator.HasEstimate.Should().BeTrue();
        estimator.OffsetHz.Should().BeApproximately(8, 4,
            "the captured GB7RDG carrier sits ~8 Hz above the 1500 Hz centre (tone ≈1508 Hz)");
    }

    // AX.25 address: six callsign characters left-shifted one bit, then an SSID byte.
    private static string DecodeAddress(byte[] frame, int offset)
    {
        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            chars[i] = (char)(frame[offset + i] >> 1);
        }

        int ssid = (frame[offset + 6] >> 1) & 0x0F;
        return $"{new string(chars).TrimEnd()}-{ssid}";
    }
}
