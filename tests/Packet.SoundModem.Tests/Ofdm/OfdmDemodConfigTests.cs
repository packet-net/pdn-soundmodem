using Packet.SoundModem.Ofdm;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>Locks down the demodulator's derived tables and DSP primitives against the values
/// codec2's <c>ofdm_create</c> computes (codec2 1.2.0, git 310777b) — the deterministic sub-blocks
/// the end-to-end decode relies on.</summary>
public class OfdmDemodConfigTests
{
    [Fact]
    public void Datac0_Derived_Rx_Tables_Match_Codec2()
    {
        var c = new OfdmDemodConfig(OfdmMode.Datac0);

        c.RxNlower.Should().Be(19, "roundf(1500/62.5 − 9/2) − 1");
        c.Nuwframes.Should().Be(3, "the 32-bit UW spans three modem frames");
        c.UwIndSym.Should().HaveCount(16);
        c.UwIndSym[0].Should().Be(5, "uw_step = Nc+1 = 10, floor(1·10/2)");
        c.UwIndSym[15].Should().Be(80, "floor(16·10/2)");
        c.TimingNorm.Should().BeGreaterThan(0.0f);
        c.FtWindowWidth.Should().Be(80);
        c.BadUwErrors.Should().Be(9);
        c.TimingMxThresh.Should().Be(0.08f);
        c.Pilots[0].Should().Be(Cf.Zero, "edge pilots are nulled (edge_pilots == 0)");
        c.Pilots[c.Nc + 1].Should().Be(Cf.Zero);
        c.TxUw.Should().HaveCount(32);
        // first 16 UW bits are the datac0 seed, the rest zero (ofdm_mode.c:136-137)
        c.TxUw[..16].Should().Equal(1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0);
        c.TxUw[16..].Should().OnlyContain(b => b == 0);
    }

    /// <summary>The narrow modes' RX-only scalars and the <c>find_carrier_centre</c> port
    /// (codec2 <c>ofdm_mode.c</c> datac4/13/14 blocks + <c>ofdm.c:570-596</c>): the RX BPF is
    /// enabled and tuned to the mean carrier frequency — the even-Nc modes (datac4/14) sit half
    /// a carrier below the nominal 1500&#160;Hz, datac13 (odd Nc) exactly on it.</summary>
    [Theory]
    [InlineData("datac4", 21, 12, 0.50f, 1468.75f)]
    [InlineData("datac13", 22, 18, 0.45f, 1500.0f)]
    [InlineData("datac14", 24, 12, 0.45f, 1472.2222f)]
    public void Narrow_Mode_Rx_Scalars_And_Carrier_Centre_Match_Codec2(
        string name, int rxNlower, int badUwErrors, float timingMxThresh, float centreHz)
    {
        var c = new OfdmDemodConfig(OfdmMode.ForName(name));

        c.RxNlower.Should().Be(rxNlower);
        c.BadUwErrors.Should().Be(badUwErrors);
        c.TimingMxThresh.Should().Be(timingMxThresh);
        c.RxBpfEnable.Should().BeTrue("datac4/13/14 all run the filtP200S400 RX band-pass");
        c.CarrierCentreHz.Should().BeApproximately(centreHz, 0.01f);

        // datac3/4/13/14 assemble the UW as two copies of the 24-bit seed, the second ending at
        // nuwbits (deliberately overlapping for the 32-bit modes).
        c.TxUw.Should().HaveCount(c.Nuwbits);
        c.TxUw[^24..].Should().Equal(1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0);
    }

    [Fact]
    public void Wide_Modes_Do_Not_Enable_The_Rx_Bpf()
    {
        foreach (string name in new[] { "datac0", "datac1", "datac3" })
        {
            new OfdmDemodConfig(OfdmMode.ForName(name)).RxBpfEnable
                .Should().BeFalse("{0} has rx_bpf_en=false in codec2", name);
        }
    }

    [Fact]
    public void Idft_Then_Dft_Round_Trips_The_Pilots()
    {
        var c = new OfdmDemodConfig(OfdmMode.Datac0);
        var time = new Cf[c.M];
        var back = new Cf[c.Nc + 2];

        c.Idft(time, c.Pilots);
        c.Dft(back, time);   // dft is the exact inverse of idft for this bin geometry

        for (int i = 0; i < c.Nc + 2; i++)
        {
            back[i].Re.Should().BeApproximately(c.Pilots[i].Re, 1e-3f);
            back[i].Im.Should().BeApproximately(c.Pilots[i].Im, 1e-3f);
        }
    }

    [Theory]
    // dibit (bit1,bit0) -> qpsk_mod symbol -> qpsk_demod must recover (bit1,bit0)
    [InlineData(0, 0, 1.0f, 0.0f)]    // qpsk[(0<<1)|0] = 1
    [InlineData(0, 1, 0.0f, 1.0f)]    // qpsk[(0<<1)|1] = j
    [InlineData(1, 0, 0.0f, -1.0f)]   // qpsk[(1<<1)|0] = -j
    [InlineData(1, 1, -1.0f, 0.0f)]   // qpsk[(1<<1)|1] = -1
    public void QpskDemod_Inverts_The_Gray_Mapping(int bit1, int bit0, float re, float im)
    {
        OfdmDemodulator.QpskDemod(new Cf(re, im), out int gotBit1, out int gotBit0);
        gotBit1.Should().Be(bit1);
        gotBit0.Should().Be(bit0);
    }

    [Fact]
    public void Buffer_Sizes_Match_Codec2_Streaming_Model()
    {
        var c = new OfdmDemodConfig(OfdmMode.Datac0);
        c.NrxBufHistory.Should().Be((4 + 2) * 880);          // (np+2)·samplesperframe
        c.NrxBufMin.Should().Be((3 * 880) + (3 * 176));      // 3·spf + 3·sps
        c.NrxBuf.Should().Be(c.NrxBufHistory + c.NrxBufMin);
        c.NsymsPerPacket.Should().Be(144);                   // bitsperpacket/2
        c.NsymsPerFrame.Should().Be(36);                     // rowsperframe·nc
    }
}
