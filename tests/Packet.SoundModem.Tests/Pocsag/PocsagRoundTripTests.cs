using Packet.SoundModem.Pocsag;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// Encoder → decoder loopback through the full audio path, including polarity
/// auto-detection, mixed batches, and injected bit errors at the wire-bit seam (so the
/// BCH sees exact 1/2/3-bit corruption, not slicer artefacts).
/// </summary>
public class PocsagRoundTripTests
{
    private const int SampleRate = 48000;

    private static List<PocsagPage> Decode(float[] audio, int baud = 1200, int sampleRate = SampleRate)
    {
        var pages = new List<PocsagPage>();
        var decoder = new PocsagDecoder(sampleRate, pages.Add, baud);
        decoder.Process(audio);
        decoder.Process(new float[sampleRate / 2]); // flush the FIR pipeline
        decoder.Flush();
        return pages;
    }

    [Fact]
    public void A_Mixed_Transmission_Round_Trips()
    {
        var messages = new List<PocsagMessage>
        {
            PocsagMessage.Alphanumeric(133703, "Hello DAPNET! [round trip] ~"),
            PocsagMessage.Numeric(8, "555 0100-99.U [42]"),
            PocsagMessage.Tone(2007287, function: 1),
            PocsagMessage.Alphanumeric(2097151, "edge of the RIC space", function: 2),
        };

        var pages = Decode(new PocsagEncoder(SampleRate).Modulate(messages));

        pages.Should().HaveCount(4);
        pages[0].Address.Should().Be(133703u);
        pages[0].Function.Should().Be(3);
        pages[0].AlphaText.Should().Be("Hello DAPNET! [round trip] ~");
        pages[0].Text.Should().Be(pages[0].AlphaText);
        pages[0].BitErrorsCorrected.Should().Be(0);
        pages[0].Inverted.Should().BeFalse();
        pages[0].Truncated.Should().BeFalse();

        pages[1].Address.Should().Be(8u);
        pages[1].Function.Should().Be(0);
        pages[1].NumericText.Should().Be("555 0100-99.U [42]");
        pages[1].Text.Should().Be(pages[1].NumericText, "function 0 reads as numeric");

        pages[2].Address.Should().Be(2007287u);
        pages[2].Function.Should().Be(1);
        pages[2].ContentGroups.Should().BeEmpty();
        pages[2].Text.Should().Be("");

        pages[3].Address.Should().Be(2097151u);
        pages[3].Function.Should().Be(2);
        pages[3].AlphaText.Should().Be("edge of the RIC space");
    }

    [Fact]
    public void An_Inverted_Transmission_Is_Detected_And_Decoded()
    {
        var messages = new List<PocsagMessage> { PocsagMessage.Alphanumeric(1234567, "upside down") };
        float[] audio = new PocsagEncoder(SampleRate, polarity: PocsagPolarity.Inverted).Modulate(messages);

        var pages = Decode(audio);

        pages.Should().HaveCount(1);
        pages[0].AlphaText.Should().Be("upside down");
        pages[0].Inverted.Should().BeTrue();
    }

    [Theory]
    [InlineData(512)]   // 93.75 samples/bit at 48 kHz — the fractional-ratio path
    [InlineData(2400)]
    public void The_Other_Standard_Rates_Round_Trip(int baud)
    {
        var messages = new List<PocsagMessage>
        {
            PocsagMessage.Alphanumeric(133703, $"pocsag{baud} leg"),
            PocsagMessage.Numeric(21, "8675309"),
        };

        var pages = Decode(new PocsagEncoder(SampleRate, baud).Modulate(messages), baud);

        pages.Should().HaveCount(2);
        pages[0].AlphaText.Should().Be($"pocsag{baud} leg");
        pages[1].NumericText.Should().Be("8675309");
    }

    [Fact]
    public void A_Non_Integer_Sample_Rate_Ratio_Round_Trips_At_22050()
    {
        // multimon-ng's native rate: 18.375 samples per bit.
        var messages = new List<PocsagMessage> { PocsagMessage.Alphanumeric(99, "at 22050 Hz") };
        float[] audio = new PocsagEncoder(22050).Modulate(messages);

        var pages = Decode(audio, sampleRate: 22050);

        pages.Should().HaveCount(1);
        pages[0].AlphaText.Should().Be("at 22050 Hz");
    }

    /// <summary>Flips wire bits (after the preamble) and re-renders — the audio path
    /// then carries exact, known bit errors into the decoder.</summary>
    private static float[] ModulateWithFlippedBits(
        IReadOnlyList<PocsagMessage> messages, params int[] codewordBitFlips)
    {
        byte[] bits = PocsagEncoder.BuildBits(messages);
        foreach (int bit in codewordBitFlips)
        {
            int position = PocsagEncoder.PreambleBits + bit;
            bits[position] ^= 1;
        }

        return new PocsagEncoder(SampleRate).Render(bits);
    }

    [Fact]
    public void One_And_Two_Bit_Errors_Per_Codeword_Are_Corrected_And_Counted()
    {
        var messages = new List<PocsagMessage> { PocsagMessage.Alphanumeric(42, "correct me") };

        // Wire layout: word 0 = sync, slot n = word n+1. RIC 42 → frame 2 → address at
        // slot 4 (word 5), content at words 6..9. Flip 1 bit in the address word and 2
        // in each of the first two content words — all within BCH reach.
        int Word(int index) => 32 * index;
        var pages = new List<PocsagPage>();
        var decoder = new PocsagDecoder(SampleRate, pages.Add);
        decoder.Process(ModulateWithFlippedBits(
            messages,
            Word(5) + 5,
            Word(6) + 1, Word(6) + 20,
            Word(7) + 7, Word(7) + 31));
        decoder.Process(new float[SampleRate / 2]);

        pages.Should().HaveCount(1);
        pages[0].AlphaText.Should().Be("correct me");
        pages[0].BitErrorsCorrected.Should().Be(5);
        pages[0].Truncated.Should().BeFalse();
    }

    [Fact]
    public void Three_Bit_Errors_In_A_Codeword_Are_Rejected_Not_Miscorrected()
    {
        // One long page spanning into batch 2, then a second page. Three flips land in
        // the first page's first content codeword: the decoder must abandon that page
        // (truncated, no invented text) and still recover the second page at the next
        // batch's sync word.
        var messages = new List<PocsagMessage>
        {
            PocsagMessage.Alphanumeric(0, "a message long enough to spill into the second batch of the transmission"),
            PocsagMessage.Alphanumeric(15, "survivor"),
        };

        var pages = new List<PocsagPage>();
        var decoder = new PocsagDecoder(SampleRate, pages.Add);
        decoder.Process(ModulateWithFlippedBits(messages, 32 * 2 + 12, 32 * 2 + 13, 32 * 2 + 22));
        decoder.Process(new float[SampleRate / 2]);
        decoder.Flush();

        pages.Should().HaveCount(2);
        pages[0].Address.Should().Be(0u);
        pages[0].Truncated.Should().BeTrue("the corrupted codeword ends the page");
        pages[0].AlphaText.Should().BeEmpty("no content codeword survived before the corruption");
        pages[1].Address.Should().Be(15u);
        pages[1].AlphaText.Should().Be("survivor");
        pages[1].Truncated.Should().BeFalse();
    }
}
