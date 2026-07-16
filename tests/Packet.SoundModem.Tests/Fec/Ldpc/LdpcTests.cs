using Packet.SoundModem.Fec.Ldpc;

namespace Packet.SoundModem.Tests.Fec.Ldpc;

/// <summary>
/// The FreeDV/codec2 LDPC port validated against codec2's own artifacts — no libcodec2 build
/// needed: four of the five datac codes ship a built-in (input LLRs → detected bits) decode
/// vector in their source, which we transliterated into <see cref="LdpcOracle"/>. If our
/// decoder reproduces those bit-for-bit, the graph construction, φ0 table, update equations
/// and termination match the reference.
/// </summary>
public class LdpcTests
{
    [Theory]
    [InlineData("H_128_256_5")]    // datac0
    [InlineData("H_256_512_4")]    // datac13 mother code
    [InlineData("H_4096_8192_3d")] // datac1
    [InlineData("HRA_56_56")]      // datac14 mother code
    public void Decoder_Reproduces_Codec2_Builtin_Vector(string codeName)
    {
        (LdpcCode code, float[] input, byte[] detected) = codeName switch
        {
            "H_128_256_5" => (LdpcCodes.H_128_256_5, LdpcOracle.H_128_256_5.Input, LdpcOracle.H_128_256_5.Detected),
            "H_256_512_4" => (LdpcCodes.H_256_512_4, LdpcOracle.H_256_512_4.Input, LdpcOracle.H_256_512_4.Detected),
            "H_4096_8192_3d" => (LdpcCodes.H_4096_8192_3d, LdpcOracle.H_4096_8192_3d.Input, LdpcOracle.H_4096_8192_3d.Detected),
            "HRA_56_56" => (LdpcCodes.HRA_56_56, LdpcOracle.HRA_56_56.Input, LdpcOracle.HRA_56_56.Detected),
            _ => throw new ArgumentOutOfRangeException(nameof(codeName)),
        };

        var decoder = new LdpcDecoder(code);
        var decoded = new byte[code.CodeLength];
        decoder.Decode(input, decoded, out _);

        // Bit-exact reproduction of codec2's own detected bits is the validation. Convergence
        // is NOT asserted: the largest code's built-in vector is a hard decode that runs to the
        // iteration cap in codec2 too (4095/4096 checks, non-converged output) — and we
        // reproduce that output bit-for-bit, which is exactly the point.
        decoded.Should().Equal(detected, "our decoder must reproduce codec2's own detected bits for {0}", codeName);
    }

    [Theory]
    // xf → φ0(xf), read straight off phi0.c's case comments / branches.
    [InlineData(10.0f, 0.0f)]           // x >= SI16(10) → 0
    [InlineData(5.0f, 0.010495133f)]    // Hi case 9  "(5.0)"
    [InlineData(1.0f, 0.745880827f)]    // Mid case 63 "(1.0000)"
    [InlineData(2.5f, 0.159456024f)]    // Mid case 39 "(2.5000)"
    [InlineData(0.0f, 10.0f)]           // low tree floor
    public void Phi0_Matches_The_Table(float xf, float expected)
    {
        Phi0.Compute(xf).Should().Be(expected);
    }

    [Fact]
    public void Encode_Then_Clean_Decode_Recovers_The_Data()
    {
        LdpcCode code = LdpcCodes.H_128_256_5;   // datac0 (256,128)
        var random = new Random(1);
        var data = new byte[code.NumberRowsHcols];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)random.Next(2);
        }

        var parity = new byte[code.NumberParityBits];
        LdpcEncoder.Encode(code, data, parity);

        // Map the codeword (data ++ parity) to strong LLRs: bit 0 → +, bit 1 → −.
        var llr = new float[code.CodeLength];
        for (int i = 0; i < code.NumberRowsHcols; i++)
        {
            llr[i] = data[i] == 0 ? 10f : -10f;
        }

        for (int i = 0; i < code.NumberParityBits; i++)
        {
            llr[code.NumberRowsHcols + i] = parity[i] == 0 ? 10f : -10f;
        }

        var decoded = new byte[code.CodeLength];
        new LdpcDecoder(code).Decode(llr, decoded, out _);

        decoded[..code.NumberRowsHcols].Should().Equal(data, "a clean-channel codeword decodes back to its data");
    }
}
