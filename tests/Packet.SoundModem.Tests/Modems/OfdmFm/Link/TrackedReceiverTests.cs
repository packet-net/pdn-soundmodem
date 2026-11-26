using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFm.Link;

namespace Packet.SoundModem.Tests.Modems.OfdmFm.Link;

public class TrackedReceiverTests
{
    private static List<TrackedCase> Cases => TrackedVectors.Cases.Value;

    [Fact]
    public void The_Vectors_Cover_Every_Opening_Constellation_Clock_And_Outcome()
    {
        Cases.Count.Should().BeGreaterThanOrEqualTo(16);
        Cases.Count(c => !c.Check).Should().BeGreaterThanOrEqualTo(3);
        Cases.Select(c => c.Opening).Distinct().Count().Should().Be(3);
        Cases.Where(c => c.BitsPerCarrier <= 2).Should().NotBeEmpty().And.OnlyContain(c => !c.FineRan);
        Cases.Where(c => c.BitsPerCarrier >= 4).Should().NotBeEmpty().And.OnlyContain(c => c.FineRan);
        Cases.Select(c => c.BitsPerCarrier).Distinct().OrderBy(x => x).Should().Equal(1, 2, 4, 6, 8, 10);
        Cases.Min(c => c.Header.PayloadSymbols).Should().Be(8);
        Cases.Max(c => c.Header.PayloadSymbols).Should().Be(52);
        Cases.Min(c => c.Ppm).Should().BeGreaterThanOrEqualTo(-300).And.BeLessThan(-250);
        Cases.Max(c => c.Ppm).Should().BeLessThanOrEqualTo(300).And.BeGreaterThan(250);
        Cases.Count(c => c.Drift != 0).Should().BeGreaterThanOrEqualTo(3);
        Cases.Count(c => c.HasStep).Should().Be(2);
        Cases.Count(c => c.Check && !c.UntrackedCheck).Should().BeGreaterThanOrEqualTo(2);
        Cases.Count(c => c.Passes > 4).Should().BeGreaterThan(0);
        Cases.Count(c => c.Resample != null).Should().Be(3);
        // the fine track's endings: all three rounds, settled early, and a correction not believed
        Cases.Count(c => c.FineRan && c.Passes == c.SymbolPasses + 3).Should().BeGreaterThan(0);
        Cases.Count(c => c.FineRan && c.Check && c.Passes < c.SymbolPasses + 3).Should().BeGreaterThan(0);
        Cases.Count(c => c.FineRan && c.Passes == c.SymbolPasses + 1).Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_Outcome_Payload_And_Passes_Of_Every_Case_Match_The_Prototype()
    {
        var receiver = new TrackedReceiver();
        var payload = new byte[20000];
        var wrong = new List<string>();
        foreach (TrackedCase c in Cases)
        {
            PayloadReceiveResult r = receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload);
            if (!r.Accepted || r.AllBlocksSatisfied != c.Converged || r.CheckPassed != c.Check || receiver.Passes != c.Passes
                || receiver.SymbolPasses != c.SymbolPasses || receiver.FineTrackRan != c.FineRan)
            {
                wrong.Add($"{c.Name}: accepted {r.Accepted} converged {r.AllBlocksSatisfied}/{c.Converged} check {r.CheckPassed}/{c.Check} passes {receiver.Passes}/{c.Passes} symbol passes {receiver.SymbolPasses}/{c.SymbolPasses} fine {receiver.FineTrackRan}/{c.FineRan}");
            }
            else if (c.Check && (r.PayloadBytes != c.Payload.Length || !payload.AsSpan(0, r.PayloadBytes).SequenceEqual(c.Payload)))
            {
                wrong.Add(c.Name + ": payload");
            }
            else if (!c.Check && r.PayloadBytes != 0)
            {
                wrong.Add(c.Name + ": payload bytes on a failed check");
            }
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void The_Final_Curve_Of_Every_Case_Matches_The_Prototype_To_A_Picosecond()
    {
        var receiver = new TrackedReceiver();
        var payload = new byte[20000];
        double largest = 0;
        foreach (TrackedCase c in Cases)
        {
            receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload);
            receiver.Curve.Length.Should().Be(c.Curve.Length, c.Name);
            for (int k = 0; k < c.Curve.Length; k++)
            {
                double d = Math.Abs(receiver.Curve[k] - c.Curve[k]);
                largest = Math.Max(largest, d);
                d.Should().BeLessThan(1e-12, $"{c.Name} symbol {k}");
            }

            receiver.FineKnots.Length.Should().Be(c.FineKnots.Length, c.Name);
            receiver.FineDelays.Length.Should().Be(c.FineDelays.Length, c.Name);
            for (int k = 0; k < c.FineKnots.Length; k++)
            {
                double t = Math.Abs(receiver.FineKnots[k] - c.FineKnots[k]);
                double d = Math.Abs(receiver.FineDelays[k] - c.FineDelays[k]);
                largest = Math.Max(largest, Math.Max(t, d));
                t.Should().BeLessThan(1e-12, $"{c.Name} knot {k}");
                d.Should().BeLessThan(1e-12, $"{c.Name} fine delay {k}");
            }
        }

        largest.Should().BeLessThan(1e-12);
    }

    [Fact]
    public void The_Last_Receives_Measurements_Are_Readable_From_The_Tracked_Receiver()
    {
        var receiver = new TrackedReceiver();
        var payload = new byte[20000];
        TrackedCase c = Cases.First(x => x.Check && x.FineRan);
        receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload).CheckPassed.Should().BeTrue();
        receiver.HasResult.Should().BeTrue();
        receiver.KnownSymbols.Should().Be(c.Known);
        receiver.Miss.Length.Should().Be(c.Known + c.Header.PayloadSymbols);
        receiver.Delays.Length.Should().Be(c.Known + c.Header.PayloadSymbols);
        receiver.Quality.Length.Should().Be(333);
        receiver.DecidedReal.Length.Should().Be(c.Header.PayloadSymbols * 333);
        receiver.Sudden.Should().BeFalse();
    }

    [Fact]
    public void The_Untracked_Receive_Of_Every_Case_Matches_The_Prototype_And_Two_Cases_Need_The_Tracking()
    {
        var receiver = new PayloadReceiver();
        var payload = new byte[20000];
        var wrong = new List<string>();
        foreach (TrackedCase c in Cases)
        {
            PayloadReceiveResult r = receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload);
            if (r.CheckPassed != c.UntrackedCheck || r.AllBlocksSatisfied != c.UntrackedConverged)
            {
                wrong.Add($"{c.Name}: check {r.CheckPassed}/{c.UntrackedCheck} converged {r.AllBlocksSatisfied}/{c.UntrackedConverged}");
            }
        }

        wrong.Should().BeEmpty();
        Cases.Count(c => c.Check && !c.UntrackedCheck).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void A_Capture_Too_Short_Is_Refused_And_Leaves_No_Result()
    {
        var receiver = new TrackedReceiver();
        var payload = new byte[20000];
        TrackedCase c = Cases.First(x => x.Check);
        int need = PayloadReceiver.SamplesNeeded(c.Header);
        receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload).CheckPassed.Should().BeTrue();
        receiver.HasResult.Should().BeTrue();
        PayloadReceiveResult r = receiver.Receive(c.Capture.AsSpan(0, c.Start + need - 1), c.Start, c.Header, c.HeaderBits, payload);
        r.Accepted.Should().BeFalse();
        r.CheckPassed.Should().BeFalse();
        receiver.HasResult.Should().BeFalse();
        receiver.Passes.Should().Be(0);
        receiver.Curve.Length.Should().Be(0);
        receiver.Quality.Length.Should().Be(0);
        receiver.Receive(c.Capture, c.Start + 100, c.Header, c.HeaderBits, payload).Accepted.Should().BeFalse();
        receiver.Receive(c.Capture, -1, c.Header, c.HeaderBits, payload).Accepted.Should().BeFalse();
        BurstHeader report = BurstHeader.Report(1, 2, 3, false);
        receiver.Receive(c.Capture, c.Start, report, c.HeaderBits, payload).Accepted.Should().BeFalse();
        Action small = () => receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, new byte[1]);
        small.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_Second_Call_Of_The_Same_Shape_Allocates_Nothing()
    {
        var receiver = new TrackedReceiver();
        var payload = new byte[20000];
        foreach (bool fine in new[] { true, false })
        {
            TrackedCase c = Cases.First(x => x.Check && x.FineRan == fine && x.Header.PayloadSymbols <= 16);
            for (int i = 0; i < 5; i++)
            {
                receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                receiver.Receive(c.Capture, c.Start, c.Header, c.HeaderBits, payload);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0, c.Name);
        }
    }
}
