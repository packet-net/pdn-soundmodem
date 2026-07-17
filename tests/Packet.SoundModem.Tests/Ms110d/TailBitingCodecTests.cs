using Packet.SoundModem.Ms110d.Fec;

namespace Packet.SoundModem.Tests.Ms110d;

public class TailBitingCodecTests
{
    [Fact]
    public void All_Zero_Input_Encodes_To_All_Zero_Output()
    {
        var coded = new byte[64];
        TailBitingEncoder.Encode(ConvolutionalCode.K7, new byte[32], coded);
        coded.Should().AllBeEquivalentTo(0);
    }

    [Fact]
    public void K7_Impulse_Response_Matches_The_Printed_Polynomials()
    {
        // Independently derived from Figure D-9 (T1 = 0o133 = x⁶+x⁴+x³+x+1,
        // T2 = 0o171 = x⁶+x⁵+x⁴+x³+1): a single 1 at info[6] of a 16-bit block produces the
        // impulse responses LSB-first at output pairs 0…6: T1 = 1,1,0,1,1,0,1;
        // T2 = 1,0,0,1,1,1,1. Hand-derived, not read back from the implementation.
        var info = new byte[16];
        info[6] = 1;
        var coded = new byte[32];
        TailBitingEncoder.Encode(ConvolutionalCode.K7, info, coded);

        byte[] expectedT1 = [1, 1, 0, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] expectedT2 = [1, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        for (int o = 0; o < 16; o++)
        {
            coded[2 * o].Should().Be(expectedT1[o], $"T1 at pair {o}");
            coded[(2 * o) + 1].Should().Be(expectedT2[o], $"T2 at pair {o}");
        }
    }

    [Fact]
    public void K9_Impulse_Response_Matches_The_Printed_Polynomials()
    {
        // Figure D-10: T1 = 0o561 = x⁸+x⁶+x⁵+x⁴+1 → taps at exponents {0,4,5,6,8};
        // T2 = 0o753 = x⁸+x⁷+x⁶+x⁵+x³+x+1 → {0,1,3,5,6,7,8}. Impulse at info[8], 20-bit block.
        var info = new byte[20];
        info[8] = 1;
        var coded = new byte[40];
        TailBitingEncoder.Encode(ConvolutionalCode.K9, info, coded);

        byte[] expectedT1 = [1, 0, 0, 0, 1, 1, 1, 0, 1];
        byte[] expectedT2 = [1, 1, 0, 1, 0, 1, 1, 1, 1];
        for (int o = 0; o < 20; o++)
        {
            coded[2 * o].Should().Be(o < 9 ? expectedT1[o] : (byte)0, $"T1 at pair {o}");
            coded[(2 * o) + 1].Should().Be(o < 9 ? expectedT2[o] : (byte)0, $"T2 at pair {o}");
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void Tail_Biting_Rotation_Property_Holds(int k)
    {
        // Rotating the input by r bits rotates the output by 2r bits (design §3.1) — the
        // signature of a correctly wrapped tail-biting encoder.
        ConvolutionalCode code = k == 7 ? ConvolutionalCode.K7 : ConvolutionalCode.K9;
        var random = new Random(99);
        var info = new byte[48];
        for (int i = 0; i < info.Length; i++)
        {
            info[i] = (byte)random.Next(2);
        }

        var coded = new byte[96];
        TailBitingEncoder.Encode(code, info, coded);

        const int r = 5;
        var rotated = new byte[48];
        for (int i = 0; i < info.Length; i++)
        {
            rotated[(i + r) % info.Length] = info[i];
        }

        var codedRotated = new byte[96];
        TailBitingEncoder.Encode(code, rotated, codedRotated);

        for (int i = 0; i < coded.Length; i++)
        {
            codedRotated[(i + (2 * r)) % coded.Length].Should().Be(coded[i]);
        }
    }

    [Theory]
    [InlineData(7, 96)]
    [InlineData(9, 96)]
    [InlineData(7, 40)]   // the WID0 Short block — smallest Phase A tail-biting block
    [InlineData(9, 24)]   // the WID1 UltraShort block
    public void Viterbi_Decodes_Clean_Coded_Blocks_Exactly(int k, int n)
    {
        ConvolutionalCode code = k == 7 ? ConvolutionalCode.K7 : ConvolutionalCode.K9;
        var random = new Random(4242);
        var info = new byte[n];
        for (int i = 0; i < n; i++)
        {
            info[i] = (byte)random.Next(2);
        }

        var coded = new byte[2 * n];
        TailBitingEncoder.Encode(code, info, coded);

        var llrs = new float[2 * n];
        for (int i = 0; i < llrs.Length; i++)
        {
            llrs[i] = coded[i] == 0 ? 1f : -1f; // positive ⇒ bit 0
        }

        var decoded = new byte[n];
        new TailBitingViterbiDecoder(code).Decode(llrs, decoded);
        decoded.Should().Equal(info);
    }

    [Theory]
    [InlineData(7, 0.70)]  // Eb/N0 ≈ 3.1 dB: uncoded BER ≈ 7.6e-2, textbook K7 coded ≈ 8e-4
    [InlineData(9, 0.72)]
    public void Viterbi_Corrects_Noisy_Llrs_At_Moderate_Snr(int k, double sigma)
    {
        ConvolutionalCode code = k == 7 ? ConvolutionalCode.K7 : ConvolutionalCode.K9;
        var random = new Random(777);
        var decoder = new TailBitingViterbiDecoder(code);
        int errors = 0;
        const int blocks = 20;
        const int n = 256;
        for (int b = 0; b < blocks; b++)
        {
            var info = new byte[n];
            for (int i = 0; i < n; i++)
            {
                info[i] = (byte)random.Next(2);
            }

            var coded = new byte[2 * n];
            TailBitingEncoder.Encode(code, info, coded);
            var llrs = new float[2 * n];
            for (int i = 0; i < llrs.Length; i++)
            {
                double noise = Gaussian(random) * sigma;
                llrs[i] = (float)((coded[i] == 0 ? 1.0 : -1.0) + noise);
            }

            var decoded = new byte[n];
            decoder.Decode(llrs, decoded);
            for (int i = 0; i < n; i++)
            {
                if (decoded[i] != info[i])
                {
                    errors++;
                }
            }
        }

        // Uncoded BER at this SNR would give ~390 errors over 5120 bits; a working soft
        // decoder sits two orders below. Bound leaves head-room for seed variation.
        errors.Should().BeLessThan(20);
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
