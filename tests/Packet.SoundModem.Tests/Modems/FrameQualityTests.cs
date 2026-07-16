using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Per-frame receive diagnostics (<see cref="FrameQuality"/>): the FEC correction counts
/// the deframers have always computed, surfaced instead of discarded.
/// </summary>
public class FrameQualityTests
{
    private const int SampleRate = 12000;

    private static byte[] SampleFrame()
    {
        var frame = new byte[16 + 64];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(3).NextBytes(frame.AsSpan(16));
        return frame;
    }

    [Fact]
    public void A_Clean_Il2p_Frame_Reports_Zero_Corrections_And_Crc()
    {
        var qualities = new List<FrameQuality>();
        var modem = QpskModem.Qpsk2400(SampleRate, _ => { });
        modem.FrameDecoded += (_, q) => qualities.Add(q);

        float[] audio = modem.Modulate(SampleFrame(), 100);
        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);
        modem.Process(padded);

        qualities.Should().ContainSingle();
        qualities[0].CorrectedBytes.Should().Be(0, "nothing was damaged");
        qualities[0].CrcValid.Should().BeTrue();
        qualities[0].Mode.Should().Be("qpsk2400-il2pc");
        qualities[0].FrameBytes.Should().Be(16 + 64);
    }

    [Fact]
    public void A_Damaged_Il2p_Frame_Reports_The_Fec_Repairs()
    {
        var qualities = new List<FrameQuality>();
        var modem = QpskModem.Qpsk2400(SampleRate, _ => { });
        modem.FrameDecoded += (_, q) => qualities.Add(q);

        float[] audio = modem.Modulate(SampleFrame(), 100);
        // Null out one byte-time of carrier mid-payload. The physical damage is wider
        // than the hole — differential detection doubles it and the 6-symbol RRC pulse
        // span smears it — so one erased byte-time costs several corrected bytes, and a
        // bigger hole overruns the 8-bytes-per-block budget entirely (empirically: a
        // 2-byte hole kills the frame). What matters here: repairs happened and were
        // REPORTED, not the exact count.
        int symbolsPerByte = 4, samplesPerSymbol = 10;
        int start = audio.Length / 2;
        Array.Clear(audio, start, symbolsPerByte * samplesPerSymbol);

        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);
        modem.Process(padded);

        qualities.Should().ContainSingle("RS exists to survive exactly this");
        qualities[0].CorrectedBytes.Should().BeGreaterThan(0, "bytes were destroyed and repaired");
    }

    [Fact]
    public void A_Classic_Hdlc_Frame_Honestly_Reports_No_Error_Count()
    {
        var qualities = new List<FrameQuality>();
        var modem = new Afsk1200Modem(SampleRate, _ => { });
        modem.FrameDecoded += (_, q) => qualities.Add(q);

        float[] audio = modem.Modulate(SampleFrame(), 100);
        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);
        modem.Process(padded);

        qualities.Should().ContainSingle();
        qualities[0].CorrectedBytes.Should().BeNull(
            "HDLC has no FEC — an FCS pass proves zero residual errors, not an error count");
        qualities[0].CrcValid.Should().BeNull();
    }

    [Fact]
    public void The_Multi_Decoder_Bank_Reports_The_Winning_Branch()
    {
        var qualities = new List<FrameQuality>();
        var bank = new Afsk1200MultiModem(SampleRate, _ => { }, offsetPairs: 2);
        bank.FrameDecoded += (_, q) => qualities.Add(q);

        // Transmit 60 Hz off-centre: an offset branch must win and say so.
        var offTx = new Afsk1200Modem(SampleRate, _ => { }, centerFrequency: 1760);
        float[] audio = offTx.Modulate(SampleFrame(), 100);
        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);
        bank.Process(padded);

        qualities.Should().ContainSingle("the deduper emits one frame however many branches decode");
        // On a CLEAN signal several branches decode and the deduper takes whichever
        // finished first, so the offset is populated but not directionally meaningful —
        // it only points at the transmitter's error when the signal is marginal enough
        // that just the matching branch decodes. Assert the plumbing, not direction.
        qualities[0].FrequencyOffsetHz.Should().NotBeNull();
        Math.Abs(qualities[0].FrequencyOffsetHz!.Value).Should().BeLessThanOrEqualTo(60);
        qualities[0].EmphasisDb.Should().NotBeNull();
    }

    [Fact]
    public void The_Channel_Aggregates_Quality_With_The_Sub_Channel()
    {
        var received = new List<(int Sub, FrameQuality Quality)>();
        var channel = new SoundModemChannel(SampleRate);
        channel.AddModem(3, sink => QpskModem.Qpsk2400(SampleRate, sink));
        channel.FrameReceivedWithQuality += (sub, _, q) => received.Add((sub, q));

        var tx = QpskModem.Qpsk2400(SampleRate, _ => { });
        float[] audio = tx.Modulate(SampleFrame(), 100);
        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);
        channel.ProcessReceive(padded);

        received.Should().ContainSingle();
        received[0].Sub.Should().Be(3);
        received[0].Quality.CorrectedBytes.Should().Be(0);
    }
}
