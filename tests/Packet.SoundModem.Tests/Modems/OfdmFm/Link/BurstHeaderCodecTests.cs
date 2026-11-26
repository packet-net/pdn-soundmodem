using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class BurstHeaderCodecTests
{
    [Fact]
    public void Every_Prototype_Header_Reads_As_The_Prototype_Read_It()
    {
        var cases = LinkVectorFile.Load("headers.json");
        cases.GetArrayLength().Should().BeGreaterThan(500);
        int ok = 0, bad = 0, unknown = 0;
        foreach (var c in cases.EnumerateArray())
        {
            byte[] bits = LinkVectorFile.Bits(c.GetProperty("bits").GetString()!, 51);
            bool read = BurstHeaderCodec.TryRead(bits, out BurstHeader h);
            switch (c.GetProperty("result").GetString())
            {
                case "bad":
                    read.Should().BeFalse();
                    bad++;
                    break;
                case "unknown_version":
                    read.Should().BeTrue();
                    h.IsKnownVersion.Should().BeFalse();
                    h.Version.Should().Be(c.GetProperty("version").GetInt32());
                    unknown++;
                    break;
                default:
                    read.Should().BeTrue();
                    h.Should().Be(new BurstHeader(0, (BurstKind)c.GetProperty("kind").GetInt32(), c.GetProperty("sender").GetInt32(), c.GetProperty("dest").GetInt32(),
                        c.GetProperty("format").GetInt32(), c.GetProperty("symbols").GetInt32(), c.GetProperty("burst").GetInt32()));
                    ok++;
                    break;
            }
        }

        ok.Should().BeGreaterThan(450);
        bad.Should().BeGreaterThanOrEqualTo(60);
        unknown.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Every_Header_We_Write_Has_The_Prototypes_Bits()
    {
        int written = 0;
        foreach (var c in LinkVectorFile.Load("headers.json").EnumerateArray())
        {
            if (!c.TryGetProperty("in", out var f))
            {
                continue;
            }

            var a = f.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var bits = new byte[51];
            BurstHeaderCodec.Write(new BurstHeader(a[0], (BurstKind)a[1], a[2], a[3], a[4], a[5], a[6]), bits);
            LinkVectorFile.Hex(bits).Should().Be(c.GetProperty("bits").GetString());
            written++;
        }

        written.Should().BeGreaterThan(380);
    }

    [Fact]
    public void Every_Prototype_Report_Is_Written_And_Read_Back()
    {
        int n = 0;
        foreach (var c in LinkVectorFile.Load("headers.json").EnumerateArray())
        {
            if (!c.TryGetProperty("report", out var r))
            {
                continue;
            }

            var bits = new byte[51];
            BurstHeaderCodec.Write(BurstHeader.Report(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetBoolean()), bits);
            LinkVectorFile.Hex(bits).Should().Be(c.GetProperty("bits").GetString());
            BurstHeaderCodec.TryRead(bits, out BurstHeader h).Should().BeTrue();
            h.Kind.Should().Be(BurstKind.Report);
            h.Failed.Should().Be(r[3].GetBoolean());
            h.RecommendedFormat.Should().Be(r[2].GetInt32());
            h.Destination.Should().Be(r[1].GetInt32());
            h.PayloadSymbols.Should().Be(0);
            n++;
        }

        n.Should().BeGreaterThanOrEqualTo(100);
    }

    [Fact]
    public void A_Single_Flipped_Bit_Is_Always_Refused()
    {
        var bits = new byte[51];
        BurstHeaderCodec.Write(BurstHeader.Data(9, 4, 130, 88, 11), bits);
        for (int i = 0; i < 51; i++)
        {
            bits[i] ^= 1;
            BurstHeaderCodec.TryRead(bits, out _).Should().BeFalse();
            bits[i] ^= 1;
        }

        BurstHeaderCodec.TryRead(bits, out BurstHeader h).Should().BeTrue();
        h.Should().Be(BurstHeader.Data(9, 4, 130, 88, 11));
    }

    [Fact]
    public void Too_Few_Bits_Are_Refused()
    {
        BurstHeaderCodec.TryRead(new byte[50], out _).Should().BeFalse();
    }

    [Fact]
    public void Out_Of_Range_Fields_Are_Refused_And_The_Burst_Number_Is_Reduced()
    {
        var bits = new byte[51];
        foreach (var h in new[]
        {
            BurstHeader.Data(64, 0, 0, 0, 0), BurstHeader.Data(0, 64, 0, 0, 0), BurstHeader.Data(0, 0, 256, 0, 0),
            BurstHeader.Data(0, 0, 0, 6, 0), BurstHeader.Data(0, 0, 0, 256, 0), BurstHeader.Data(0, 0, 0, -4, 0),
            new BurstHeader(3, BurstKind.Data, 0, 0, 0, 0, 0), new BurstHeader(0, (BurstKind)8, 0, 0, 0, 0, 0),
        })
        {
            FluentThrow(() => BurstHeaderCodec.Write(h, bits));
        }

        BurstHeaderCodec.Write(BurstHeader.Data(1, 2, 3, 4, 17), bits);
        BurstHeaderCodec.TryRead(bits, out BurstHeader back).Should().BeTrue();
        back.BurstNumber.Should().Be(1);
    }

    private static void FluentThrow(Action a) => a.Should().Throw<ArgumentOutOfRangeException>();
}
