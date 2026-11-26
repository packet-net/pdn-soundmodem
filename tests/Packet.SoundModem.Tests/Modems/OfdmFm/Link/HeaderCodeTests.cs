using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>The burst header's code against the prototype's <c>modem.layout_code(layout, 3).encode(bits)</c> (Vectors/header_code_vectors.json).</summary>
public class HeaderCodeTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Modems", "OfdmFm", "Link", "Vectors", "header_code_vectors.json"))));

    public static IEnumerable<object[]> Openings() =>
        Enum.GetValues<FormatOpening>().Select(o => new object[] { o });

    private static JsonElement Entry(FormatOpening opening) =>
        Doc.Value.RootElement.GetProperty("openings").EnumerateArray().First(e => e.GetProperty("opening").GetString() == opening.ToString());

    [Theory]
    [MemberData(nameof(Openings))]
    public void The_Coded_Bit_Counts_And_Plan_Match_The_Prototype(FormatOpening opening)
    {
        JsonElement e = Entry(opening);
        HeaderCode.CodedBits(opening).Should().Be(e.GetProperty("codedBits").GetInt32());
        HeaderCode.TransmitOrder(opening).ToArray().Should().Equal(CodeVectorFile.Ints(e.GetProperty("transmitOrder")), "transmit order");
        HeaderCode.CheckRows(opening).Should().Be(e.GetProperty("rows").GetInt32(), "check rows kept");
        e.GetProperty("z").GetInt32().Should().Be(9);
        e.GetProperty("fillers").GetArrayLength().Should().Be(39);
        HeaderCode.EncoderKind(opening).Should().Be(e.GetProperty("encoderKind").GetString());
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void Every_Vector_Matches_The_Prototype_Bit_For_Bit(FormatOpening opening)
    {
        JsonElement e = Entry(opening);
        int n = e.GetProperty("codedBits").GetInt32();
        int count = 0;
        foreach (JsonElement h in e.GetProperty("headers").EnumerateArray())
        {
            byte[] header = CodeVectorFile.Bits(h.GetProperty("header").GetString()!, BurstHeaderCodec.HeaderBits);
            byte[] expected = CodeVectorFile.Bits(h.GetProperty("coded").GetString()!, n);
            var coded = new byte[n];
            HeaderCode.Encode(opening, header, coded);
            coded.AsSpan().CommonPrefixLength(expected).Should().Be(n, $"{opening} header {count}: first differing coded bit");
            count++;
        }

        count.Should().Be(200);
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void Every_Full_Codeword_Has_A_Zero_Syndrome(FormatOpening opening)
    {
        foreach (JsonElement h in Entry(opening).GetProperty("headers").EnumerateArray())
        {
            byte[] header = CodeVectorFile.Bits(h.GetProperty("header").GetString()!, BurstHeaderCodec.HeaderBits);
            var word = new byte[HeaderCode.CodewordLength(opening)];
            HeaderCode.EncodeCodeword(opening, header, word);
            HeaderCode.SyndromeIsZero(opening, word).Should().BeTrue();
            word[^1] ^= 1;
            HeaderCode.SyndromeIsZero(opening, word).Should().BeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(Openings))]
    public void Encoding_Allocates_Nothing_After_First_Use(FormatOpening opening)
    {
        var header = new byte[BurstHeaderCodec.HeaderBits];
        header[3] = 1;
        var coded = new byte[HeaderCode.CodedBits(opening)];
        HeaderCode.Encode(opening, header, coded);
        long before = GC.GetAllocatedBytesForCurrentThread();
        HeaderCode.Encode(opening, header, coded);
        GC.GetAllocatedBytesForCurrentThread().Should().Be(before);
    }

    [Fact]
    public void The_Three_Openings_Take_666_480_And_336_Coded_Bits()
    {
        HeaderCode.CodedBits(FormatOpening.Full).Should().Be(666);
        HeaderCode.CodedBits(FormatOpening.UpTo3000Hz).Should().Be(480);
        HeaderCode.CodedBits(FormatOpening.UpTo1500Hz).Should().Be(336);
    }

    [Fact]
    public void A_Wrong_Length_Input_Or_Output_Is_Refused()
    {
        var ok = new byte[BurstHeaderCodec.HeaderBits];
        var coded = new byte[666];
        ((Action)(() => HeaderCode.Encode(FormatOpening.Full, new byte[50], coded))).Should().Throw<ArgumentException>();
        ((Action)(() => HeaderCode.Encode(FormatOpening.Full, new byte[52], coded))).Should().Throw<ArgumentException>();
        ((Action)(() => HeaderCode.Encode(FormatOpening.Full, ok, new byte[665]))).Should().Throw<ArgumentException>();
        ((Action)(() => HeaderCode.Encode(FormatOpening.UpTo1500Hz, ok, coded))).Should().Throw<ArgumentException>();
        ((Action)(() => HeaderCode.CodedBits((FormatOpening)7))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
