using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class PayloadScramblerTests
{
    [Fact]
    public void The_Documents_Vector_Gives_The_Documented_Seed_And_Bytes()
    {
        PayloadScrambler.Seed(1, 2, 1).Should().Be(0x41086A5Bu);
        var bits = new byte[64];
        PayloadScrambler.Sequence(1, 2, 1, bits);
        LinkVectorFile.Hex(bits).Should().Be("9296710e0c4bf2fc");
    }

    [Fact]
    public void Twenty_Seeds_Give_The_Prototypes_First_256_Bits()
    {
        var v = LinkVectorFile.Load("scrambler.json");
        v.GetProperty("cases").GetArrayLength().Should().BeGreaterThanOrEqualTo(20);
        foreach (var c in v.GetProperty("cases").EnumerateArray())
        {
            int s = c.GetProperty("sender").GetInt32(), d = c.GetProperty("dest").GetInt32(), b = c.GetProperty("burst").GetInt32();
            PayloadScrambler.Seed(s, d, b).Should().Be((uint)c.GetProperty("seed").GetInt64());
            var bits = new byte[256];
            PayloadScrambler.Sequence(s, d, b, bits);
            LinkVectorFile.Hex(bits).Should().Be(c.GetProperty("bits").GetString());
        }
    }

    [Fact]
    public void A_Long_Sequence_Matches_The_Prototype()
    {
        var l = LinkVectorFile.Load("scrambler.json").GetProperty("long");
        var bits = new byte[l.GetProperty("count").GetInt32()];
        PayloadScrambler.Sequence(l.GetProperty("sender").GetInt32(), l.GetProperty("dest").GetInt32(), l.GetProperty("burst").GetInt32(), bits);
        LinkVectorFile.Hex(bits).Should().Be(l.GetProperty("bits").GetString());
    }

    [Fact]
    public void Applying_Twice_Gives_Back_The_Bits()
    {
        var data = new byte[300];
        new Random(3).NextBytes(data);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] &= 1;
        }

        var copy = (byte[])data.Clone();
        PayloadScrambler.Apply(12, 40, 7, data);
        data.Should().NotEqual(copy);
        PayloadScrambler.Apply(12, 40, 7, data);
        data.Should().Equal(copy);
    }

    [Fact]
    public void Out_Of_Range_Seeds_Are_Refused()
    {
        ((Action)(() => PayloadScrambler.Seed(64, 0, 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => PayloadScrambler.Seed(0, 0, 16))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
