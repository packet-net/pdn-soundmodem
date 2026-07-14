using Packet.SoundModem.Fec;

namespace Packet.SoundModem.Tests.Fec;

public class ReedSolomonTests
{
    // Wire bytes from the IL2P spec's example packets (draft v0.6). RS is systematic,
    // so the scrambled data travels unchanged and the parity bytes are directly visible.

    [Fact]
    public void Two_Parity_Encode_Matches_The_Spec_S_Frame_Header()
    {
        byte[] scrambledHeader = Convert.FromHexString("26574D57F1D2A8F06AF27BAD23");
        var rs = new ReedSolomon(paritySymbols: 2);

        var parity = new byte[2];
        rs.Encode(scrambledHeader, parity);

        parity.Should().Equal(Convert.FromHexString("BDC0"));
    }

    [Fact]
    public void Sixteen_Parity_Encode_Matches_The_Spec_I_Frame_Payload_Block()
    {
        byte[] scrambledPayload = Convert.FromHexString("3C699F0C755A38A17F");
        var rs = new ReedSolomon(paritySymbols: 16);

        var parity = new byte[16];
        rs.Encode(scrambledPayload, parity);

        parity.Should().Equal(Convert.FromHexString("A5DAD8F6EA57373DB12AB0DE44A820D0"));
    }

    [Fact]
    public void Clean_Codewords_Decode_With_Zero_Corrections()
    {
        var rs = new ReedSolomon(paritySymbols: 16);
        byte[] codeword = MakeCodeword(rs, dataLength: 100, seed: 1);

        rs.Decode(codeword).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Errors_Up_To_Half_The_Parity_Count_Are_Corrected(int errorCount)
    {
        var rs = new ReedSolomon(paritySymbols: 16);
        byte[] original = MakeCodeword(rs, dataLength: 200, seed: errorCount);
        byte[] corrupted = (byte[])original.Clone();
        Corrupt(corrupted, errorCount, seed: errorCount * 7);

        int corrected = rs.Decode(corrupted);

        corrected.Should().Be(errorCount);
        corrupted.Should().Equal(original);
    }

    [Fact]
    public void A_Single_Error_In_A_Two_Parity_Header_Block_Is_Corrected()
    {
        var rs = new ReedSolomon(paritySymbols: 2);
        byte[] original = MakeCodeword(rs, dataLength: 13, seed: 42);
        byte[] corrupted = (byte[])original.Clone();
        corrupted[5] ^= 0xA5;

        rs.Decode(corrupted).Should().Be(1);
        corrupted.Should().Equal(original);
    }

    [Fact]
    public void Corrections_Survive_Errors_Landing_In_The_Parity_Bytes()
    {
        var rs = new ReedSolomon(paritySymbols: 16);
        byte[] original = MakeCodeword(rs, dataLength: 50, seed: 9);
        byte[] corrupted = (byte[])original.Clone();
        corrupted[^1] ^= 0x01;
        corrupted[^7] ^= 0xFF;
        corrupted[10] ^= 0x80;

        rs.Decode(corrupted).Should().Be(3);
        corrupted.Should().Equal(original);
    }

    [Fact]
    public void Fx25_Style_First_Root_One_Roundtrips()
    {
        var rs = new ReedSolomon(paritySymbols: 16, firstConsecutiveRoot: 1);
        byte[] original = MakeCodeword(rs, dataLength: 64, seed: 3);
        byte[] corrupted = (byte[])original.Clone();
        Corrupt(corrupted, 8, seed: 21);

        rs.Decode(corrupted).Should().Be(8);
        corrupted.Should().Equal(original);
    }

    [Fact]
    public void Random_Error_Fuzz_Roundtrips_Across_Sizes_And_Counts()
    {
        var rs = new ReedSolomon(paritySymbols: 16);
        var random = new Random(20260714);
        for (int trial = 0; trial < 200; trial++)
        {
            int dataLength = random.Next(1, 240);
            byte[] original = MakeCodeword(rs, dataLength, seed: random.Next());
            byte[] corrupted = (byte[])original.Clone();
            int errorCount = random.Next(0, 9);
            Corrupt(corrupted, errorCount, seed: random.Next());

            int corrected = rs.Decode(corrupted);

            corrected.Should().Be(errorCount, $"trial {trial}, {dataLength} data bytes");
            corrupted.Should().Equal(original, $"trial {trial}");
        }
    }

    private static byte[] MakeCodeword(ReedSolomon rs, int dataLength, int seed)
    {
        var random = new Random(seed);
        var codeword = new byte[dataLength + rs.ParitySymbols];
        random.NextBytes(codeword.AsSpan(0, dataLength));
        rs.Encode(codeword.AsSpan(0, dataLength), codeword.AsSpan(dataLength));
        return codeword;
    }

    private static void Corrupt(byte[] codeword, int errorCount, int seed)
    {
        var random = new Random(seed);
        var positions = new HashSet<int>();
        while (positions.Count < errorCount)
        {
            positions.Add(random.Next(codeword.Length));
        }

        foreach (int position in positions)
        {
            byte flip;
            do
            {
                flip = (byte)random.Next(1, 256);
            }
            while (flip == 0);
            codeword[position] ^= flip;
        }
    }
}
