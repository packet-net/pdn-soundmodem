using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The shared primitive under every "what is in this audio" question the tree asks.
/// </summary>
/// <remarks>
/// <c>pdn-decode</c>'s sweep and the station's own <c>CaptureSweep</c> each used to carry a copy
/// of this, and the copies had already begun to differ. The three decisions here are exactly the
/// ones that must not: what counts as a decode, how the audio is handed over, and what happens
/// to the last frame in a file. A tool telling an operator one thing while the station acts on
/// another is worse than either answer alone.
/// </remarks>
public class ModeReaderTests
{
    [Fact]
    public void A_Frame_Is_Read_Back_Out_Of_Its_Own_Modulation()
    {
        byte[] sent = UiFrame();
        float[] audio = ModemCatalog.Create("afsk1200", 12000, _ => { }).Modulate(sent, 50);

        var read = new List<byte[]>();
        ModeReader.Run("afsk1200", audio, 12000, default, (frame, _) => read.Add(frame));

        read.Should().ContainSingle().Which.Should().Equal(sent);
    }

    [Fact]
    public void The_Last_Frame_Is_Flushed_Out_Of_The_Pipeline()
    {
        // A recording that ends flush with its closing flag still has that frame inside the
        // demodulator's FIR chain. A live stream never ends, so no modem does this for itself,
        // and a survey capture ends by construction.
        byte[] sent = UiFrame();
        float[] audio = ModemCatalog.Create("afsk1200", 12000, _ => { }).Modulate(sent, 50);

        var flushed = new List<byte[]>();
        var unflushed = new List<byte[]>();
        ModeReader.Run("afsk1200", audio, 12000, default, (f, _) => flushed.Add(f));
        ModeReader.Run("afsk1200", audio, 12000, default, (f, _) => unflushed.Add(f), flushSilence: false);

        flushed.Should().ContainSingle();
        unflushed.Should().BeEmpty("which is why the flush is the default");
    }

    [Fact]
    public void A_Mode_With_No_Centre_Is_Given_None()
    {
        // The baseband fsk*/c4fsk* family occupies DC upwards and Create refuses a centre for it
        // outright (issue #39), so asking is the caller's job.
        ModeReader.At("afsk300", 1120).CentreFrequencyHz.Should().Be(1120);
        ModeReader.At("fsk9600", 1120).CentreFrequencyHz.Should().BeNull();
    }

    [Fact]
    public void A_Mode_Too_Wide_For_Where_It_Is_Pointed_Says_So_Rather_Than_Guessing()
    {
        // Both callers rely on being able to tell this apart from a mode that simply heard
        // nothing: one collapses it into a single line of report, the other ignores it silently.
        Action tooWide = () => ModeReader.Run(
            "ms110d-wn0", new float[48000], 48000,
            ModeReader.At("ms110d-wn0", 500), (_, _) => { });

        // ArgumentException, not the narrower ArgumentOutOfRangeException: the catalogue has two
        // guards on a centre and they throw different types. Both callers matched only the
        // narrow one, so half of these escaped the collapse and were reported to an operator as
        // individual failures.
        tooWide.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("centreHz");
    }

    [Fact]
    public void The_Confidence_Ordering_Is_The_One_Both_Callers_Use()
    {
        // A verified check sequence beats Reed-Solomon standing alone, and both beat a frame the
        // receiver read and would not have handed to a host.
        DecodeConfidence.Rank(new FrameQuality("m", 1, 0, CrcValid: true)).Should().Be(0);
        DecodeConfidence.Rank(new FrameQuality("m", 1, 0, null, PlainIl2p: true)).Should().Be(2);
        DecodeConfidence.Rank(new FrameQuality("m", 1, 0, null, PlainIl2p: true, MonitorOnly: true))
            .Should().Be(3);
    }

    [Fact]
    public void Only_A_Verified_Check_Sequence_Counts_As_Evidence_Somebody_Transmitted()
    {
        // Thirty receivers over one recording is thirty chances to find structure that is not
        // there. This is what stops a phantom committing one of a station's modem slots.
        DecodeConfidence.IsEvidence(new FrameQuality("m", 1, 0, CrcValid: true)).Should().BeTrue();
        DecodeConfidence.IsEvidence(new FrameQuality("m", 1, 0, CrcValid: null)).Should().BeTrue(
            "an HDLC frame's FCS passed, which is the whole guarantee that framing has");
        DecodeConfidence.IsEvidence(new FrameQuality("m", 1, 0, null, PlainIl2p: true))
            .Should().BeFalse("reed-solomon standing alone proves nothing about who sent it");
        DecodeConfidence.IsEvidence(new FrameQuality("m", 1, 0, CrcValid: true, MonitorOnly: true))
            .Should().BeFalse();
    }

    private static byte[] UiFrame()
    {
        var frame = new List<byte>();
        foreach (char c in "APDN  ")
        {
            frame.Add((byte)(c << 1));
        }

        frame.Add(0xE0);
        foreach (char c in "M0LTE ")
        {
            frame.Add((byte)(c << 1));
        }

        frame.Add(0x61);
        frame.Add(0x03);
        frame.Add(0xF0);
        frame.AddRange(System.Text.Encoding.ASCII.GetBytes("mode reader round trip"));
        return [.. frame];
    }
}
