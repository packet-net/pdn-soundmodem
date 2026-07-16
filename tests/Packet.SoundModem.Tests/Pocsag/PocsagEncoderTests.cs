using Packet.SoundModem.Pocsag;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// Codeword-stream structure per the spec: preamble, batches of sync + 16 codewords,
/// frame placement by the three low address bits, idle fill, and the content bit
/// packings (numeric nibbles and 7-bit ASCII, both LSB-first). Hand-worked vectors
/// follow the conventions cross-checked against multimon-ng's pocsag.c decode tables
/// and MMDVMHost's POCSAGControl.cpp encoder.
/// </summary>
public class PocsagEncoderTests
{
    [Fact]
    public void A_Transmission_Is_Whole_Batches_Of_Sync_Plus_Sixteen()
    {
        uint[] words = PocsagEncoder.BuildCodewords([PocsagMessage.Tone(0)]);

        words.Length.Should().Be(17);
        words[0].Should().Be(PocsagCodeword.FrameSync);
        words.Skip(2).Should().OnlyContain(w => w == PocsagCodeword.Idle);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(8u, 0)]       // 8 & 7 == 0 → frame 0 again, high bits 1
    [InlineData(133703u, 7)]  // & 7 == 7
    [InlineData(2007287u, 7)]
    public void The_Address_Codeword_Sits_In_The_Frame_The_Low_Address_Bits_Select(
        uint address, int frame)
    {
        uint[] words = PocsagEncoder.BuildCodewords([PocsagMessage.Tone(address, function: 2)]);

        // Word 0 is sync; data slot n is words[1 + n]; frame f occupies slots 2f, 2f+1.
        int slot = 2 * frame;
        for (int i = 0; i < slot; i++)
        {
            words[1 + i].Should().Be(PocsagCodeword.Idle, "slots before the frame are idle fill");
        }

        uint expected = PocsagCodeword.Encode(((address >> 3) << 2) | 2u);
        words[1 + slot].Should().Be(expected);
        (expected & 0x80000000).Should().Be(0u, "address codewords carry a 0 flag bit");
    }

    [Fact]
    public void Numeric_Content_Packs_Nibbles_Lsb_First_And_Pads_With_Spaces()
    {
        // "1234" → nibbles 1,2,3,4 plus one 0xC (space) pad, each sent LSB-first:
        // 1000 0100 1100 0010 0011 → field 0x84C23, flag bit set → data21 0x184C23.
        // (multimon-ng's table "084 2.6]195-3U7[" is this same reversal, read back.)
        uint[] words = PocsagEncoder.BuildCodewords([PocsagMessage.Numeric(0, "1234")]);

        PocsagCodeword.Data(words[2]).Should().Be(0x184C23u);
    }

    [Fact]
    public void Alphanumeric_Content_Packs_Ascii_Lsb_First_And_Pads_With_Zero_Bits()
    {
        // "A" (0x41 = 1000001) sent LSB-first → 1,0,0,0,0,0,1 then 13 zero-bit pad →
        // field 0b1000001_0000000000000 = 0x82000, flag bit set → data21 0x182000.
        uint[] words = PocsagEncoder.BuildCodewords([PocsagMessage.Alphanumeric(0, "A")]);

        PocsagCodeword.Data(words[2]).Should().Be(0x182000u);
    }

    [Fact]
    public void Message_Codewords_Follow_The_Address_Across_The_Batch_Boundary()
    {
        // Address in frame 7 (slots 14/15) with three message codewords: the content
        // must spill into the next batch, straight after its sync word.
        uint[] words = PocsagEncoder.BuildCodewords(
            [PocsagMessage.Alphanumeric(7, "eight chars+")]); // 12 chars → 84 bits → 5 words

        words.Length.Should().Be(34, "two batches of 1 + 16");
        words[17].Should().Be(PocsagCodeword.FrameSync);
        words[15].Should().NotBe(PocsagCodeword.Idle).And.NotBe(PocsagCodeword.FrameSync);
        (words[16] & 0x80000000).Should().Be(0x80000000u, "slot 15 is content");
        (words[18] & 0x80000000).Should().Be(0x80000000u, "content resumes after the sync word");
    }

    [Fact]
    public void A_Second_Page_For_The_Same_Frame_Takes_The_Frames_Other_Codeword()
    {
        // A frame is two codeword slots and the address may occupy either, so two pages
        // for frame 2 (RIC 2 & 7) sit side by side in slots 4 and 5 of one batch.
        uint[] words = PocsagEncoder.BuildCodewords(
        [
            PocsagMessage.Tone(2, function: 0),
            PocsagMessage.Tone(2, function: 1),
        ]);

        words.Length.Should().Be(17);
        words[5].Should().Be(PocsagCodeword.Encode(0u), "first page, slot 4");
        words[6].Should().Be(PocsagCodeword.Encode(1u), "second page, slot 5");
    }

    [Fact]
    public void A_Third_Page_For_The_Same_Frame_Waits_For_The_Next_Batch()
    {
        uint[] words = PocsagEncoder.BuildCodewords(
        [
            PocsagMessage.Tone(2, function: 0),
            PocsagMessage.Tone(2, function: 1),
            PocsagMessage.Tone(2, function: 2),
        ]);

        words.Length.Should().Be(34, "frame 2 is full, so the third page needs a second batch");
        words[22].Should().Be(PocsagCodeword.Encode(2u), "third page, next batch slot 4");
    }

    [Fact]
    public void The_Preamble_Is_576_Bit_Reversals()
    {
        byte[] bits = PocsagEncoder.BuildBits([PocsagMessage.Tone(0)]);

        bits.Length.Should().Be(576 + (17 * 32));
        for (int i = 0; i < 576; i++)
        {
            bits[i].Should().Be((byte)(1 - (i & 1)));
        }

        // The sync codeword follows immediately, MSB first.
        uint sync = 0;
        for (int i = 0; i < 32; i++)
        {
            sync = (sync << 1) | bits[576 + i];
        }

        sync.Should().Be(PocsagCodeword.FrameSync);
    }

    [Fact]
    public void Numeric_Pages_Reject_Characters_Outside_The_Numeric_Set()
    {
        Action act = () => PocsagMessage.Numeric(1, "12A4");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Alphanumeric_Pages_Reject_Non_Ascii()
    {
        Action act = () => PocsagMessage.Alphanumeric(1, "naïve");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Addresses_Above_21_Bits_Are_Rejected()
    {
        Action act = () => PocsagMessage.Tone(0x200000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
