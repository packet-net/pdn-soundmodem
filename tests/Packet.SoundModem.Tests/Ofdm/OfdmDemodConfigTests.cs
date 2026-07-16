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
