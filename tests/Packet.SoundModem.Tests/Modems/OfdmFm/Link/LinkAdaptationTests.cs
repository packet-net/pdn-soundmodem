using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

/// <summary>
/// LinkAdaptation against the prototype's Station (proto/vectors/station_vectors.py): scripted scenarios with explicit times, and the calls
/// the prototype's own two-station simulation makes. After every call the return value and the whole record touched are compared:
/// formats, flags and counts exactly; the margin exactly (it is only ever added to, taken from, held to its bounds); the estimate within 1e-9
/// dB, since it is built from logarithms that the two languages' libraries may round differently in the last bit.
/// </summary>
public class LinkAdaptationTests
{
    private const double EstimateTolerance = 1e-9;

    public static IEnumerable<object[]> ScenarioNames() =>
        StationVectorFile.Scenarios.GetProperty("scenarios").EnumerateArray().Select(s => new object[] { s.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Scenario_Replays_The_Prototype_Call_By_Call(string name)
    {
        JsonElement scenario = StationVectorFile.Scenarios.GetProperty("scenarios").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);
        LinkAdaptation[] stations = scenario.GetProperty("stations").EnumerateArray().Select(n => new LinkAdaptation(n.GetInt32())).ToArray();
        int index = 0;
        foreach (JsonElement call in scenario.GetProperty("calls").EnumerateArray())
        {
            string at = $"{name} call {index} ({call.GetProperty("op").GetString()})";
            LinkAdaptation station = stations[call.GetProperty("station").GetInt32()];
            JsonElement args = call.GetProperty("args");
            JsonElement ret = call.GetProperty("ret");
            switch (call.GetProperty("op").GetString())
            {
                case "heard_data":
                {
                    ReadOnlySpan<double> quality = args.GetProperty("snr").ValueKind == JsonValueKind.Null
                        ? default
                        : StationVectorFile.Value(args.GetProperty("snr").GetInt32());
                    LinkReport? got = station.HeardData(
                        args.GetProperty("sender").GetInt32(),
                        args.GetProperty("dest").GetInt32(),
                        args.GetProperty("format").GetInt32(),
                        args.GetProperty("decoded").GetBoolean(),
                        quality,
                        args.GetProperty("now").GetDouble(),
                        OptionalDouble(args.GetProperty("footing_db")),
                        args.GetProperty("sudden").GetBoolean(),
                        OptionalReport(args.GetProperty("their_report")),
                        OptionalInt(args.GetProperty("capability")));
                    got.Should().Be(OptionalReport(ret), "the report returned by {0}", at);
                    break;
                }

                case "report_for":
                    station.ReportFor(args.GetProperty("dest").GetInt32()).Should().Be(OptionalInt(ret), "the recommendation returned by {0}", at);
                    break;
                case "heard_report":
                    station.HeardReport(args.GetProperty("sender").GetInt32(), OptionalInt(args.GetProperty("recommended")), args.GetProperty("failed").GetBoolean(), args.GetProperty("now").GetDouble());
                    break;
                case "choose":
                {
                    LinkChoice got = station.Choose(args.GetProperty("dest").GetInt32(), args.GetProperty("now").GetDouble());
                    got.Format.Should().Be(ret[0].GetInt32(), "the format chosen by {0}", at);
                    got.ReportWanted.Should().Be(ret[1].GetBoolean(), "report wanted from {0}", at);
                    break;
                }

                case "sent":
                    station.Sent(args.GetProperty("dest").GetInt32());
                    break;
                case "no_answer":
                    station.NoAnswer(args.GetProperty("dest").GetInt32());
                    break;
                default:
                    throw new InvalidOperationException("unknown call " + call.GetProperty("op").GetString());
            }

            AssertState(station, call, at);
            index++;
        }

        index.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_Scenarios_Cover_Every_Call_And_Are_Numerous_Enough_To_Mean_Something()
    {
        var calls = StationVectorFile.Scenarios.GetProperty("scenarios").EnumerateArray()
            .SelectMany(s => s.GetProperty("calls").EnumerateArray())
            .GroupBy(c => c.GetProperty("op").GetString()!)
            .ToDictionary(g => g.Key, g => g.Count());
        calls.Keys.Should().BeEquivalentTo(new[] { "heard_data", "report_for", "heard_report", "choose", "sent", "no_answer" });
        calls.Values.Should().OnlyContain(n => n >= 20);
        StationVectorFile.Scenarios.GetProperty("scenarios").GetArrayLength().Should().BeGreaterThanOrEqualTo(25);
    }

    [Fact]
    public void The_Constants_Are_The_Prototypes()
    {
        var c = StationVectorFile.Scenarios.GetProperty("constants");
        c.GetProperty("FRESH").GetDouble().Should().Be(LinkAdaptation.FreshSeconds);
        c.GetProperty("FORGET").GetDouble().Should().Be(LinkAdaptation.ForgetSeconds);
        c.GetProperty("M_START").GetDouble().Should().Be(LinkAdaptation.MarginStart);
        c.GetProperty("M_UP").GetDouble().Should().Be(LinkAdaptation.MarginUp);
        c.GetProperty("M_DOWN").GetDouble().Should().Be(LinkAdaptation.MarginDown);
        c.GetProperty("CAUTION").GetInt32().Should().Be(LinkAdaptation.Caution);
        c.GetProperty("WEIGHT").GetDouble().Should().Be(LinkAdaptation.Weight);
        c.GetProperty("DROP").GetDouble().Should().Be(LinkAdaptation.DropDb);
        c.GetProperty("SETTLING").GetInt32().Should().Be(LinkAdaptation.Settling);
        c.GetProperty("COARSE").GetDouble().Should().Be(LinkAdaptation.CoarseDb);
    }

    [Fact]
    public void A_Station_With_Nothing_Chooses_The_First_Contact_Format_And_Asks()
    {
        var station = new LinkAdaptation(1);
        station.TryGetNeighbour(2, out _).Should().BeFalse();
        station.Choose(2, 0.0).Should().Be(new LinkChoice(StandardFormats.FirstContact, true));
        station.TryGetNeighbour(2, out LinkNeighbour? nb).Should().BeTrue();
        nb!.Margin.Should().Be(3.0);
        nb.Recommended.Should().BeNull();
        station.ReportFor(2).Should().BeNull();
    }

    [Fact]
    public void Station_Numbers_Are_One_To_Sixty_Three()
    {
        Action a = () => new LinkAdaptation(0);
        a.Should().Throw<ArgumentOutOfRangeException>();
        a = () => new LinkAdaptation(64);
        a.Should().Throw<ArgumentOutOfRangeException>();
        var station = new LinkAdaptation(5);
        a = () => station.Choose(0, 0.0);
        a.Should().Throw<ArgumentOutOfRangeException>();
        a = () => station.Choose(64, 0.0);
        a.Should().Throw<ArgumentOutOfRangeException>();
        a = () => station.HeardData(63, 64, 0, true, default, 0.0);
        a.Should().Throw<ArgumentOutOfRangeException>();
        station.Choose(63, 0.0).Format.Should().Be(StandardFormats.FirstContact);
        station.HeardData(1, 0, 0, true, default, 0.0).Should().BeNull();
    }

    [Fact]
    public void The_Footing_Is_Drive_Plus_The_Power_Spread_Over_Fewer_Carriers()
    {
        // format 0: full band, 0.2 bit a carrier: driven 6 dB, 333 carriers
        LinkAdaptation.Footing(0).Should().Be(6.0);
        LinkAdaptation.Footing(0, opening: true).Should().Be(6.0);
        // 1.5 kHz band bottom rung: the payload uses fewer carriers than the full band, and so does its opening
        int n = 6 * StandardFormats.Rungs;
        LinkAdaptation.Footing(n).Should().BeApproximately((10 * Math.Log10(333.0 / StandardFormats.Describe(n).Carriers)) + 6.0, 1e-12);
        LinkAdaptation.Footing(n, opening: true).Should().Be(LinkAdaptation.Footing(n));
        // a 2 kHz band format opens on the carriers up to 3 kHz
        int m = 5 * StandardFormats.Rungs;
        LinkAdaptation.Footing(m, opening: true).Should().BeLessThan(LinkAdaptation.Footing(m) + 1e-12);
        LinkAdaptation.Footing(m, opening: true).Should().NotBe(LinkAdaptation.Footing(m));
    }

    [Fact]
    public void Calls_On_A_Station_That_Is_Known_Do_Not_Allocate()
    {
        var station = new LinkAdaptation(1);
        double[] good = StationVectorFile.Profile(3).ToArray();
        double[] narrow = StationVectorFile.Profile(StationVectorFile.Functions.GetProperty("count").GetInt32() - 40).ToArray();
        double t = 0.0;
        void Round()
        {
            t += 0.5;
            station.HeardData(2, 1, 20, true, good, t, 0.0, false, new LinkReport(30, false), 1);
            station.HeardData(2, 1, 20, false, narrow, t + 0.1, null);
            station.HeardData(2, 3, 20, true, good, t + 0.2, 0.0, true);
            station.ReportFor(2);
            station.HeardReport(2, 25, false, t + 0.3);
            station.Choose(2, t + 0.4);
            station.Sent(2);
            station.NoAnswer(2);
        }

        for (int i = 0; i < 6; i++)
        {
            Round();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++)
        {
            Round();
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    private static void AssertState(LinkAdaptation station, JsonElement call, string at)
    {
        int who = call.GetProperty("who").GetInt32();
        station.TryGetNeighbour(who, out LinkNeighbour? nb).Should().BeTrue("a record is held for {0} after {1}", who, at);
        JsonElement s = call.GetProperty("state");
        nb!.Margin.Should().Be(s.GetProperty("margin").GetDouble(), "margin after {0}", at);
        nb.Recommended.Should().Be(OptionalInt(s.GetProperty("recommended")), "recommended after {0}", at);
        nb.Pending.Should().Be(OptionalInt(s.GetProperty("pending")), "pending after {0}", at);
        nb.LastHeard.Should().Be(OptionalDouble(s.GetProperty("last_heard")), "last heard after {0}", at);
        nb.Told.Should().Be(OptionalInt(s.GetProperty("told")), "told after {0}", at);
        nb.Cap.Should().Be(OptionalInt(s.GetProperty("cap")), "cap after {0}", at);
        nb.Good.Should().Be(s.GetProperty("good").GetInt32(), "good after {0}", at);
        nb.Measures.Should().Be(s.GetProperty("measures").GetInt32(), "measures after {0}", at);
        nb.MeasuredAt.Should().Be(OptionalDouble(s.GetProperty("measured_at")), "measured at after {0}", at);
        JsonElement report = s.GetProperty("report");
        nb.ReportFormat.Should().Be(report.ValueKind == JsonValueKind.Null ? null : report[0].GetInt32(), "report format after {0}", at);
        nb.ReportTime.Should().Be(report.ValueKind == JsonValueKind.Null ? null : report[1].GetDouble(), "report time after {0}", at);
        nb.Silences.Should().Be(s.GetProperty("silences").GetInt32(), "silences after {0}", at);
        nb.Unanswered.Should().Be(s.GetProperty("unanswered").GetInt32(), "unanswered after {0}", at);
        nb.Capability.Should().Be(OptionalInt(s.GetProperty("capability")), "capability after {0}", at);

        ReadOnlySpan<double> expected = StationVectorFile.Value(s.GetProperty("db").GetInt32());
        ReadOnlySpan<double> got = nb.EstimateDb;
        double worst = 0.0;
        int worstAt = -1;
        for (int i = 0; i < StationVectorFile.Carriers; i++)
        {
            if (double.IsNaN(expected[i]) || double.IsNaN(got[i]))
            {
                double.IsNaN(got[i]).Should().Be(double.IsNaN(expected[i]), "carrier {0} known or not after {1}", i, at);
                continue;
            }

            double d = Math.Abs(got[i] - expected[i]);
            if (d > worst)
            {
                worst = d;
                worstAt = i;
            }
        }

        worst.Should().BeLessThanOrEqualTo(EstimateTolerance, "the estimate after {0} differs most on carrier {1}", at, worstAt);

        var held = new List<int>();
        for (int who2 = 1; who2 < 64; who2++)
        {
            if (station.TryGetNeighbour(who2, out _))
            {
                held.Add(who2);
            }
        }

        held.Should().Equal(call.GetProperty("records").EnumerateArray().Select(e => e.GetInt32()), "the records held after {0}", at);
    }

    private static int? OptionalInt(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt32();

    private static double? OptionalDouble(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();

    private static LinkReport? OptionalReport(JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : new LinkReport(OptionalInt(e[0]), e[1].GetBoolean());
}
