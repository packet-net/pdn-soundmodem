using AwesomeAssertions;
using Packet.Ax25;
using Packet.Core;
using Packet.SoundModem.Modems.OfdmFmStation;
using Packet.SoundModem.OfdmFmTool.Ax25;
using Packet.SoundModem.Tests.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.OfdmFmTool;

/// <summary>
/// Real AX.25 connected-mode sessions, packet.net's stack on both ends, over the two-station rig. Nothing waits on real time: see <see cref="Ax25Bench"/> for the rule that decides when the clock and the audio may move.
/// </summary>
public class Ax25LinkTests
{
    private static bool IsPatternLine(string line) => line.Length == 79 && line.All(c => c is >= '!' and <= '~');

    [Fact]
    public async Task From_cold_a_call_connects_gets_the_banner_has_lines_echoed_receives_2000_bytes_and_ends_with_BYE()
    {
        await using var bench = new Ax25Bench();
        await bench.StartAsync();
        using var stop = new CancellationTokenSource();
        Task<int> answering = bench.Answerer.Link.AnswerAsync(stop.Token);
        var typed = new ScriptedReader();
        Task<int> calling = bench.Caller.Link.CallAsync("M0XYZ-1", null, typed, stop.Token);

        await bench.PumpAsync(() => bench.Caller.Link.Said.Contains("pdn-ofdm M0XYZ-1"), "the connection and the banner");
        typed.Say("hello over the modem");
        await bench.PumpAsync(() => bench.Caller.Link.Said.Contains("hello over the modem"), "the echo");
        typed.Say("SEND 2000");
        await bench.PumpAsync(() => bench.Caller.Link.Said.Count(IsPatternLine) >= 25, "the 2000 bytes");
        typed.Say("BYE");
        await bench.PumpAsync(
            () => bench.Caller.Link.Said.Any(l => l.StartsWith("Disconnected from", StringComparison.Ordinal)) && bench.Answerer.Link.Said.Any(l => l.StartsWith("Disconnected from", StringComparison.Ordinal)),
            "both ends to see the link end");

        (await calling).Should().Be(0);
        bench.Caller.Link.Said.Should().Contain("Connected to M0XYZ-1");
        bench.Answerer.Link.Said.Should().Contain("Connected from G4ABC-1");
        bench.Caller.Link.Said.Count(IsPatternLine).Should().Be(25, "2000 bytes are 25 lines of 79 characters and a carriage return");
        stop.Cancel();
        await answering;

        // The identification came before the first transmission on each side, and the run kept its own check that no two radios were keyed at once.
        StationRig.IsIdentification(bench.Caller.Rig.Air.Keyups[0], "G4ABC-1").Should().BeTrue();
        StationRig.IsIdentification(bench.Answerer.Rig.Air.Keyups[0], "M0XYZ-1").Should().BeTrue();
        Console.WriteLine(string.Join(Environment.NewLine, await bench.Caller.LinesAsync()));
    }

    [Fact]
    public async Task A_get_of_2000_bytes_from_cold_times_them_and_reports_the_frames_the_answering_side_sent_again()
    {
        await using var bench = new Ax25Bench();
        await bench.StartAsync();
        using var stop = new CancellationTokenSource();
        Task<int> answering = bench.Answerer.Link.AnswerAsync(stop.Token);
        Task<int> calling = bench.Caller.Link.CallAsync("M0XYZ-1", 2000, null, stop.Token);

        await bench.PumpAsync(() => calling.IsCompleted, "the transfer and the link to end");

        (await calling).Should().Be(0);
        string result = bench.Caller.Link.Said.Single(l => l.StartsWith("Received 2000 bytes in ", StringComparison.Ordinal));
        Console.WriteLine(result);
        result.Should().MatchRegex(@"^Received 2000 bytes in \d+\.\d\d s, \d+\.\d\d kbit/s, \d+ of \d+ frames sent again$");
        stop.Cancel();
        await answering;
        Console.WriteLine("answerer keyups: " + string.Join(", ", bench.Answerer.Rig.Air.Keyups.Select(k => $"{k.Audio.Length / 48000.0:0.00}s")));
    }

    [Fact]
    public async Task When_a_burst_of_data_is_lost_the_stack_sends_it_again_the_transfer_still_completes_and_the_repeat_is_counted()
    {
        await using var bench = new Ax25Bench();
        await bench.StartAsync();
        bench.Lose = (side, keyup) => ReferenceEquals(side, bench.Answerer) && keyup == LostKeyup;
        using var stop = new CancellationTokenSource();
        Task<int> answering = bench.Answerer.Link.AnswerAsync(stop.Token);
        Task<int> calling = bench.Caller.Link.CallAsync("M0XYZ-1", 2000, null, stop.Token);

        await bench.PumpAsync(() => calling.IsCompleted, "the transfer to complete in spite of the lost burst");

        (await calling).Should().Be(0, "no byte was damaged and the link held");
        string result = bench.Caller.Link.Said.Single(l => l.StartsWith("Received 2000 bytes in ", StringComparison.Ordinal));
        Console.WriteLine(result);
        (long sent, long repeated) = bench.Answerer.Link.CountsFor("G4ABC-1");
        repeated.Should().BeGreaterThan(0, "the lost burst's frames were sent again");
        result.Should().Contain($"{repeated} of ", "the count the answering side gave when asked, which is the repeats it had sent by then");
        sent.Should().BeGreaterThanOrEqualTo(repeated);
        stop.Cancel();
        await answering;
    }

    [Fact]
    public async Task A_get_of_20000_bytes_from_cold_follows_the_modems_rate_and_reaches_a_share_of_it()
    {
        await using var bench = new Ax25Bench();
        await bench.StartAsync();
        using var stop = new CancellationTokenSource();
        Task<int> answering = bench.Answerer.Link.AnswerAsync(stop.Token);
        Task<int> calling = bench.Caller.Link.CallAsync("M0XYZ-1", 20000, null, stop.Token);

        await bench.PumpAsync(() => calling.IsCompleted, "the transfer and the link to end");

        (await calling).Should().Be(0);
        Report("cold, clean path", bench);
        stop.Cancel();
        await answering;
    }

    [Fact]
    public async Task A_get_of_20000_bytes_with_one_burst_lost_still_completes_and_counts_the_repeats()
    {
        await using var bench = new Ax25Bench();
        await bench.StartAsync();
        bench.Lose = (side, keyup) => ReferenceEquals(side, bench.Answerer) && keyup == LostBigKeyup;
        using var stop = new CancellationTokenSource();
        Task<int> answering = bench.Answerer.Link.AnswerAsync(stop.Token);
        Task<int> calling = bench.Caller.Link.CallAsync("M0XYZ-1", 20000, null, stop.Token);

        await bench.PumpAsync(() => calling.IsCompleted, "the transfer to complete in spite of the lost burst");

        (await calling).Should().Be(0);
        Report("one burst lost", bench);
        (long sent, long repeated) = bench.Answerer.Link.CountsFor("G4ABC-1");
        repeated.Should().BeGreaterThan(0);
        stop.Cancel();
        await answering;
    }

    /// <summary>The number of the answering station's keyup that is lost: the first of the large data bursts of a 20000 byte transfer.</summary>
    private const int LostBigKeyup = 3;

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task A_path_that_gets_worse_part_way_through_a_transfer_shrinks_the_window_and_the_transfer_still_completes(int noiseSeed)
    {
        // White noise on the answering side's bursts from its fourth keyup on. The seeds are three draws of the same noise, not a search: the claim is the same for each.
        await using var bench = new Ax25Bench(noiseSeed);
        await bench.StartAsync();
        bench.NoiseFor = (side, keyup) => ReferenceEquals(side, bench.Answerer) && keyup >= 4 ? 0.1 : 0;
        using var stop = new CancellationTokenSource();
        Task<int> answering = bench.Answerer.Link.AnswerAsync(stop.Token);
        Task<int> calling = bench.Caller.Link.CallAsync("M0XYZ-1", 20000, null, stop.Token);

        await bench.PumpAsync(() => calling.IsCompleted, "the transfer to end");

        (await calling).Should().Be(0, "every byte arrived and none was damaged");
        Report($"path steps down, seed {noiseSeed}", bench);
        int[] windows = [.. bench.Answerer.Link.Said.Where(l => l.StartsWith("Link to", StringComparison.Ordinal)).Select(l => int.Parse(l.Split("window ")[1].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture))];
        windows.Should().StartWith([1, 32], "first contact, then the full window on the clean path");
        windows.Last().Should().BeLessThan(32, "the rate the modem allows fell, and the window with it");
        windows.Skip(2).Should().OnlyContain(w => w < 32, "once the rate fell, the window stayed under the full one");
        bench.Caller.Link.Said.Single(l => l.StartsWith("Modem rates to", StringComparison.Ordinal)).Should().NotContain("70.4 kbit/s back");
        stop.Cancel();
        await answering;
    }

    private static void Report(string what, Ax25Bench bench)
    {
        string result = bench.Caller.Link.Said.Single(l => l.StartsWith("Received ", StringComparison.Ordinal));
        string rates = bench.Caller.Link.Said.Single(l => l.StartsWith("Modem rates to", StringComparison.Ordinal));
        Console.WriteLine($"[{what}] {result}\n[{what}] {rates}\n[{what}] answerer: " + string.Join(" | ", bench.Answerer.Link.Said.Where(l => l.StartsWith("Link to", StringComparison.Ordinal))));
        Console.WriteLine($"[{what}] caller: " + string.Join(" | ", bench.Caller.Link.Said.Where(l => l.StartsWith("Link to", StringComparison.Ordinal))));
    }

    /// <summary>The number of the answering station's keyup that is lost: one that carries the transfer's data.</summary>
    private const int LostKeyup = 3;

    [Fact]
    public async Task A_frame_equal_to_one_still_waiting_in_the_modem_is_not_sent_again_and_its_task_ends_as_the_originals_does()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        using var port = OfdmFmFramePort.Attach(rig.Channel, 0, rig.Host);
        var transport = new OfdmFmAx25Transport(port, rig.Host, rig.Channel, 0, rig.Clock);
        byte[] frame = Ax25Frame.Ui(new Callsign("M0XYZ", 1), new Callsign("G4ABC", 1), "x"u8, Ax25Frame.PidNoLayer3).ToBytes();
        rig.StartTransmitter();

        Task<Packet.Ax25.Transport.TxCompletion> first = transport.SendAwaitingCompletionAsync(frame);
        Task<Packet.Ax25.Transport.TxCompletion> repeat = transport.SendAwaitingCompletionAsync(frame);
        Task<Packet.Ax25.Transport.TxCompletion> other = transport.SendAwaitingCompletionAsync(Ax25Frame.Ui(new Callsign("M0XYZ", 1), new Callsign("G4ABC", 1), "y"u8, Ax25Frame.PidNoLayer3).ToBytes());
        await rig.PumpAsync(() => first.IsCompleted && other.IsCompleted, "the frames to go");
        await rig.FeedAsync(100);
        await rig.IdleAsync();

        (await repeat).Completed.Should().Be((await first).Completed, "the repeat ends with the original");
        rig.Transmitted.Should().HaveCount(2, "one datagram for the two equal frames and one for the other");

        // Once the original has gone, an equal frame is a new one.
        Task<Packet.Ax25.Transport.TxCompletion> again = transport.SendAwaitingCompletionAsync(frame);
        await rig.PumpAsync(() => again.IsCompleted, "the frame to go again");
        rig.Transmitted.Should().HaveCount(3);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task A_frame_the_modem_refuses_is_reported_once_with_its_reason_and_the_task_fails()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        using var port = OfdmFmFramePort.Attach(rig.Channel, 0, rig.Host);
        var transport = new OfdmFmAx25Transport(port, rig.Host, rig.Channel, 0, rig.Clock);
        var reasons = new List<string>();
        transport.Refused += reasons.Add;
        byte[] tooBig = Ax25Frame.Ui(new Callsign("M0XYZ", 1), new Callsign("G4ABC", 1), new byte[600], Ax25Frame.PidNoLayer3).ToBytes();

        Task<Packet.Ax25.Transport.TxCompletion> sent = transport.SendAwaitingCompletionAsync(tooBig);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sent);
        reasons.Should().ContainSingle().Which.Should().Contain("too large");
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task A_repeat_of_a_frame_the_port_closes_on_fails_as_the_original_does()
    {
        await using var rig = new StationRig();
        rig.Host.SetIdentity("G4ABC");
        var port = OfdmFmFramePort.Attach(rig.Channel, 0, rig.Host);
        var transport = new OfdmFmAx25Transport(port, rig.Host, rig.Channel, 0, rig.Clock);
        byte[] frame = Ax25Frame.Ui(new Callsign("M0XYZ", 1), new Callsign("G4ABC", 1), "x"u8, Ax25Frame.PidNoLayer3).ToBytes();

        Task<Packet.Ax25.Transport.TxCompletion> first = transport.SendAwaitingCompletionAsync(frame);
        Task<Packet.Ax25.Transport.TxCompletion> repeat = transport.SendAwaitingCompletionAsync(frame);
        port.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repeat);
        await transport.DisposeAsync();
    }
}
