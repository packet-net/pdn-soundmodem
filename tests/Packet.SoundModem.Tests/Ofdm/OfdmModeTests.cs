using Packet.SoundModem.Ofdm;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>The per-mode OFDM parameters, with every derived size checked against the values
/// codec2's <c>ofdm_create</c> computes (docs/ofdm-design.md §2) — the deterministic foundation
/// the modulator and demodulator build on.</summary>
public class OfdmModeTests
{
    [Theory]
    //          name       M   Ncp  perFrame perPacket bitsFrame bitsPkt symsPkt nlower
    [InlineData("datac0", 128, 48, 880, 3520, 72, 288, 144, 19)]
    [InlineData("datac1", 128, 48, 880, 33440, 216, 8208, 4104, 10)]
    [InlineData("datac3", 128, 48, 880, 25520, 72, 2088, 1044, 19)]
    [InlineData("datac4", 128, 48, 880, 41360, 32, 1504, 752, 21)]
    [InlineData("datac13", 128, 48, 880, 15840, 24, 432, 216, 22)]
    [InlineData("datac14", 144, 40, 920, 3680, 32, 128, 64, 24)]
    public void Derived_Sizes_Match_Codec2(
        string name, int m, int ncp, int perFrame, int perPacket,
        int bitsFrame, int bitsPacket, int symsPacket, int nlower)
    {
        OfdmMode mode = OfdmMode.ForName(name);
        mode.M.Should().Be(m);
        mode.Ncp.Should().Be(ncp);
        mode.SamplesPerFrame.Should().Be(perFrame);
        mode.SamplesPerPacket.Should().Be(perPacket);
        mode.BitsPerFrame.Should().Be(bitsFrame);
        mode.BitsPerPacket.Should().Be(bitsPacket);
        mode.SymsPerPacket.Should().Be(symsPacket);
        mode.TxNlower.Should().Be(nlower, "the roundf(centre/Rs − Nc/2)−1 half-away-from-zero bin");
    }
}
