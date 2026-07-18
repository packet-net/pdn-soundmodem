using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Frequency-diversity BPSK: the coherent detector's narrow tracking loop only pulls in a few Hz
/// of carrier offset within a short preamble, so <see cref="BpskMultiModem"/> runs a bank of
/// stock branches at stepped centres. These tests show the bank decoding off-frequency signals a
/// single centred coherent modem misses, and deduping to one frame per transmission.
/// </summary>
public class BpskMultiModemTests
{
    private const int SampleRate = 12000;

    private static readonly byte[] Frame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static float[] OffTune(int offsetHz, int preambleBits = 45)
    {
        // ~150 ms preamble (45 symbols at 300 baud) — realistic NinoTNC TXDELAY — at a carrier
        // offset from the 1500 Hz channel centre, then padded with lead-in/out silence.
        byte[] wire = Il2pCodec.Encode(Frame, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        float[] audio = new BpskModulator(SampleRate, carrierFrequency: 1500 + offsetHz).Modulate(bits);
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(-12)]
    [InlineData(12)]
    [InlineData(24)]
    public void An_Off_Frequency_Signal_A_Single_Coherent_Modem_Misses_Decodes_On_The_Bank(int offsetHz)
    {
        float[] audio = OffTune(offsetHz);

        var single = new List<byte[]>();
        BpskModem.Bpsk300(SampleRate, single.Add, crc: true, PskDetector.Coherent).Process(audio);
        single.Should().BeEmpty("a single centred coherent modem cannot acquire {0} Hz in a short preamble", offsetHz);

        var banked = new List<byte[]>();
        BpskMultiModem.Bpsk300(SampleRate, banked.Add, crc: true, PskDetector.Coherent).Process(audio);
        banked.Should().ContainSingle("the bank has a branch within tolerance of {0} Hz", offsetHz)
            .Which.Should().Equal(Frame);
    }

    [Fact]
    public void An_On_Frequency_Signal_Is_Emitted_Exactly_Once()
    {
        var frames = new List<byte[]>();
        BpskMultiModem.Bpsk300(SampleRate, frames.Add).Process(OffTune(0));

        frames.Should().ContainSingle("branches decode the same transmission but dedupe to one")
            .Which.Should().Equal(Frame);
    }

    [Fact]
    public void The_Winning_Branch_Reports_Its_Frequency_Offset()
    {
        var qualities = new List<FrameQuality>();
        var modem = BpskMultiModem.Bpsk300(SampleRate, _ => { }, crc: true, PskDetector.Coherent);
        modem.FrameDecoded += (_, quality) => qualities.Add(quality);

        modem.Process(OffTune(24));

        qualities.Should().ContainSingle()
            .Which.FrequencyOffsetHz.Should().BeApproximately(24, 8, "the branch step nearest +24 Hz wins");
    }

    [Fact]
    public void The_Step_And_Pairs_Tuneables_Set_The_Coverage()
    {
        // A +26 Hz signal is outside a narrow bank (1 pair × 8 Hz = ±8 Hz) but inside a wide one
        // (4 pairs × 10 Hz = ±40 Hz) — the exposed tuneables must actually change coverage.
        float[] audio = OffTune(26);

        var narrow = new List<byte[]>();
        new BpskMultiModem(SampleRate, narrow.Add, offsetPairs: 1, offsetHz: 8).Process(audio);
        narrow.Should().BeEmpty("±8 Hz coverage cannot reach +26 Hz");

        var wide = new List<byte[]>();
        new BpskMultiModem(SampleRate, wide.Add, offsetPairs: 4, offsetHz: 10).Process(audio);
        wide.Should().ContainSingle("±40 Hz coverage reaches +26 Hz").Which.Should().Equal(Frame);
    }

    [Fact]
    public void Identical_Content_Later_Is_Not_Deduplicated()
    {
        float[] one = OffTune(0);
        // Same frame content twice, 4 s apart — both must be delivered (the dedupe window is short).
        var audio = new float[one.Length + 4 * SampleRate + one.Length];
        one.CopyTo(audio, 0);
        one.CopyTo(audio, one.Length + 4 * SampleRate);

        var frames = new List<byte[]>();
        BpskMultiModem.Bpsk300(SampleRate, frames.Add, offsetPairs: 2).Process(audio);

        frames.Should().HaveCount(2);
    }

    [Fact]
    public void Transmit_Uses_The_Centre_And_Round_Trips_Through_A_Single_Modem()
    {
        // A bank TX is the centre branch only, so a plain centred modem must decode it.
        var modem = BpskMultiModem.Bpsk300(SampleRate, _ => { });
        float[] audio = modem.Modulate(Frame, txDelayMilliseconds: 300);
        var padded = new float[audio.Length + 2 * (SampleRate / 5)]; // lead-in + flush tail
        audio.CopyTo(padded, SampleRate / 5);

        var received = new List<byte[]>();
        BpskModem.Bpsk300(SampleRate, received.Add).Process(padded);
        received.Should().ContainSingle().Which.Should().Equal(Frame);
    }
}
