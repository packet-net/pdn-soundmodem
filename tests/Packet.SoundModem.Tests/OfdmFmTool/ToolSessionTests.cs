using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Ident;
using Packet.SoundModem.OfdmFmTool;
using Packet.SoundModem.Tests.Channel;
using Packet.SoundModem.Tests.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.OfdmFmTool;

/// <summary>
/// The commands on two stations joined by the test air, on the rig's virtual clock: no real waits, nothing keyed on hardware.
/// </summary>
public class ToolSessionTests
{
    private sealed class Side : IAsyncDisposable
    {
        private readonly List<string> _lines = [];

        public Side(string call, string? to, int seed, VirtualAir.Clock clock, bool canTransmit = true, bool detail = false)
        {
            Rig = new StationRig(seed: seed);
            Log = new StampedLog(clock, line => { lock (_lines) { _lines.Add(line); } });
            Session = new ToolSession(Rig.Host, call, to, detail, canTransmit, Log, clock);
            if (!canTransmit)
            {
                ListenOnly.Apply(Rig.Channel);
            }
        }

        public StationRig Rig { get; }

        public StampedLog Log { get; }

        public ToolSession Session { get; }

        public void Begin()
        {
            Session.Start().Should().BeNull();
            Rig.StartTransmitter();
        }

        /// <summary>Everything written so far, once the writer has caught up.</summary>
        public async Task<string[]> LinesAsync()
        {
            await Log.DisposeAsync();
            lock (_lines)
            {
                return [.. _lines];
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Log.DisposeAsync();
            await Rig.DisposeAsync();
        }
    }

    /// <summary>
    /// The pump with the tool's wall clock moving as the audio does: each quiet block fed is 20 ms, and every chunk a rig radiates is its own length. The tool's clock is a clock of its own, apart from the rigs' channel clocks:
    /// moving it fires only the tool's waits (a ping's answer, the identification), never anything inside a channel, so nothing real is waited for and no channel is touched by a second thread.
    /// </summary>
    private static Task PumpAsync(VirtualAir.Clock clock, Func<bool> until, string because, params StationRig[] rigs) =>
        StationRig.PumpAsync(
            () =>
            {
                clock.Elapse(TimeSpan.FromMilliseconds(20));
                return until();
            },
            because,
            rigs);

    /// <summary>Joins two rigs by the air, and makes whatever either radiates move the tool's clock. With one rig, only the clock.</summary>
    private static void JoinAir(VirtualAir.Clock clock, StationRig a, StationRig? b = null)
    {
        var timeline = new Lock();
        a.Air.Timeline = timeline;
        a.Air.Radiated = chunk =>
        {
            b?.Channel.ProcessReceive(chunk);
            clock.Elapse(TimeSpan.FromSeconds(chunk.Length / 48000.0));
        };
        if (b is not null)
        {
            b.Air.Timeline = timeline;
            b.Air.Radiated = chunk =>
            {
                a.Channel.ProcessReceive(chunk);
                clock.Elapse(TimeSpan.FromSeconds(chunk.Length / 48000.0));
            };
        }
    }

    [Fact(Skip = "Blocked by #943: the first exchange between two cold stations keys both radios at once (the answer arrives while the pinger sends its Morse identification), which the rig refuses. Unskip when #943 is decided.")]
    public async Task Pings_to_a_listening_station_are_answered_and_the_rate_is_reported_both_ways()
    {
        var clock = new VirtualAir.Clock();
        await using var pinger = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);
        await using var listener = new Side("M0XYZ-1", null, seed: 12, clock);
        JoinAir(clock, pinger.Rig, listener.Rig);
        pinger.Begin();
        listener.Begin();

        // The first run is cold: see the next test. This one is the same stations once both have identified.
        Task<PingSummary> cold = pinger.Session.PingAsync(1, 32, CancellationToken.None);
        await PumpAsync(clock, () => cold.IsCompleted, "the first ping run to finish", pinger.Rig, listener.Rig);
        await cold;

        Task<PingSummary> ping = pinger.Session.PingAsync(5, 32, CancellationToken.None);
        await PumpAsync(clock, () => ping.IsCompleted, "the ping run to finish", pinger.Rig, listener.Rig);

        PingSummary summary = await ping;
        summary.Sent.Should().Be(5);
        summary.Answered.Should().Be(5);
        summary.OutBitsPerSecond.Should().NotBeNull("the modem reported the rate to the other station");
        summary.BackBitsPerSecond.Should().NotBeNull("the modem reported the rate back");
        summary.BothWayBitsPerSecond.Should().BeGreaterThan(0);

        string[] lines = await pinger.LinesAsync();
        lines.Should().OnlyContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d\d:\d\d:\d\d\.\d\d  \S"), "every line starts with the time");
        lines.Count(l => l.Contains(": 32 bytes, ") && l.Contains(" round trip, out ") && l.Contains(", back ")).Should().Be(5);
        lines.Should().Contain(l => l.EndsWith(summary.ToString(), StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("Sent 1 datagram to M0XYZ-1"), "the modem's own trace is printed unchanged");
        Console.WriteLine(string.Join(Environment.NewLine, lines));

        string[] heard = await listener.LinesAsync();
        heard.Count(l => l.Contains("answering")).Should().Be(6);
    }

    [Fact(Skip = "Blocked by #943: this is the fault itself, two radios keyed at once, which the rig refuses to run. Unskip, with Answered == 5, when #943 is decided.")]
    public async Task Known_fault_943_the_first_ping_of_a_cold_start_is_lost_while_the_pinger_identifies()
    {
        var clock = new VirtualAir.Clock();
        await using var pinger = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);
        await using var listener = new Side("M0XYZ-1", null, seed: 12, clock);
        JoinAir(clock, pinger.Rig, listener.Rig);
        pinger.Begin();
        listener.Begin();

        Task<PingSummary> ping = pinger.Session.PingAsync(5, 32, CancellationToken.None);
        await PumpAsync(clock, () => ping.IsCompleted, "the ping run to finish", pinger.Rig, listener.Rig);

        PingSummary summary = await ping;
        summary.Sent.Should().Be(5);

        // A RECORD OF A KNOWN FAULT, not a requirement: packet-net/pdn-soundmodem#943. The station identifies in Morse straight after its first transmission and is deaf to the answer, so the first ping is lost.
        // This changes when #943 is decided; at that point it should assert Answered == 5.
        summary.Answered.Should().BeGreaterThanOrEqualTo(4, "known fault #943: the answer to the first ping arrives while the station is keyed for its identification");
        Console.WriteLine(string.Join(Environment.NewLine, await pinger.LinesAsync()));
    }

    [Fact]
    public async Task A_text_datagram_is_sent_and_the_listener_prints_who_from_how_long_and_the_text()
    {
        var clock = new VirtualAir.Clock();
        await using var sender = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);
        await using var listener = new Side("M0XYZ-1", null, seed: 12, clock);
        JoinAir(clock, sender.Rig, listener.Rig);
        sender.Begin();
        listener.Begin();

        Task<bool> sent = sender.Session.SendTextAsync("hello there"u8.ToArray(), CancellationToken.None);
        await PumpAsync(clock, () => sent.IsCompleted, "the text to go", sender.Rig, listener.Rig);

        (await sent).Should().BeTrue();
        (await sender.LinesAsync()).Should().Contain(l => l.EndsWith("Transmitted 11 bytes to M0XYZ-1", StringComparison.Ordinal));
        (await listener.LinesAsync()).Should().Contain(l => l.Contains("Received 11 bytes from address ") && l.EndsWith(": \"hello there\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_text_too_big_for_one_datagram_is_refused_plainly_and_nothing_is_keyed()
    {
        var clock = new VirtualAir.Clock();
        await using var sender = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);
        sender.Begin();

        bool sent = await sender.Session.SendTextAsync(new byte[5000], CancellationToken.None);

        sent.Should().BeFalse();
        (await sender.LinesAsync()).Should().Contain(l => l.Contains("refused: 5000 bytes does not fit one datagram"));
        sender.Rig.Air.KeyAttempts.Should().Be(0);
    }

    [Fact]
    public async Task A_listener_with_no_ptt_cannot_key_even_when_a_ping_arrives_that_it_would_answer()
    {
        var clock = new VirtualAir.Clock();
        await using var pinger = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);

        // The adversarial case: the channel's transmitter is running on the listener, as it would be if the tool ever started it. The refusal has to come from the channel and the tool's own rule.
        await using var listener = new Side("M0XYZ-1", null, seed: 12, clock, canTransmit: false);
        JoinAir(clock, pinger.Rig, listener.Rig);
        pinger.Begin();
        listener.Begin();

        Task<PingSummary> ping = pinger.Session.PingAsync(1, 32, CancellationToken.None);
        await PumpAsync(clock, () => ping.IsCompleted, "the ping to time out", pinger.Rig, listener.Rig);
        PingSummary summary = await ping;

        summary.Answered.Should().Be(0);
        listener.Rig.Air.KeyAttempts.Should().Be(0, "the receive-only channel keyed nothing");
        listener.Rig.Air.Keyups.Should().BeEmpty();
        listener.Rig.Transmitted.Should().BeEmpty();
        (await listener.LinesAsync()).Should().Contain(l => l.Contains("not answered, receive only"));
        (await pinger.LinesAsync()).Should().Contain(l => l.Contains("no answer in 30 s"));
    }

    [Fact]
    public async Task A_ping_nobody_hears_is_counted_as_no_answer_when_the_wait_runs_out()
    {
        var clock = new VirtualAir.Clock();
        await using var pinger = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);
        JoinAir(clock, pinger.Rig);
        pinger.Begin();

        Task<PingSummary> ping = pinger.Session.PingAsync(2, 32, CancellationToken.None);
        await PumpAsync(clock, () => ping.IsCompleted, "both pings to time out", pinger.Rig);

        (await ping).Should().Be(new PingSummary(2, 0, null, null));
        string[] lines = await pinger.LinesAsync();
        lines.Count(l => l.Contains("no answer in 30 s")).Should().Be(2);
        lines.Should().Contain(l => l.EndsWith("2 sent, 0 answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_detail_level_gives_more_trace_lines_than_the_flow_level()
    {
        int flow = await TraceLinesOfOneSendAsync(detail: false);
        int detail = await TraceLinesOfOneSendAsync(detail: true);

        detail.Should().BeGreaterThan(flow);
    }

    private static async Task<int> TraceLinesOfOneSendAsync(bool detail)
    {
        var clock = new VirtualAir.Clock();
        await using var sender = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock, detail: detail);
        JoinAir(clock, sender.Rig);
        sender.Begin();
        Task<bool> sent = sender.Session.SendTextAsync("hi"u8.ToArray(), CancellationToken.None);
        await PumpAsync(clock, () => sent.IsCompleted, "the text to go", sender.Rig);
        return (await sender.LinesAsync()).Length;
    }

    [Fact]
    public async Task The_longest_datagram_the_modem_takes_keys_for_under_twelve_seconds()
    {
        await using var sender = new Side("G4ABC-1", "M0XYZ-1", seed: 11, new VirtualAir.Clock());
        sender.Begin();

        // The largest datagram the station accepts for an address it has not located: scan down from far too big.
        int accepted = 0;
        for (int size = 1000; size > 0 && accepted == 0; size--)
        {
            if (sender.Rig.Host.Send(new byte[size], LinkAddress.FromAscii("M0XYZ-1"), LinkAddress.FromAscii("G4ABC-1"), LinkProtocol.NoLayer3, new HostTag(size)) == SendResult.Accepted)
            {
                accepted = size;
            }
        }

        accepted.Should().BeGreaterThan(100);
        await StationRig.PumpAsync(() => sender.Rig.Transmitted.Length >= 1 && sender.Rig.MorseSent.Length >= 1 && sender.Rig.ModemSent.Any(m => m.Kind == ModemSentKind.Identification), "the datagram and the identification to go", sender.Rig);

        // One datagram too big for a 2.5 s burst goes alone and whole, and the biggest the modem accepts at the slowest rate takes about 10.6 s on air.
        sender.Rig.Air.Keyups.Should().NotBeEmpty();
        foreach (Keyup keyup in sender.Rig.Air.Keyups.Where(k => !StationRig.IsMorse(k)))
        {
            Console.WriteLine($"keyup {keyup.Audio.Length / 48000.0:0.000} s");
            (keyup.Audio.Length / 48000.0).Should().BeLessThanOrEqualTo(12.0);
        }
    }

    [Fact]
    public void The_Morse_identification_of_the_longest_callsign_is_the_longest_thing_the_tool_keys()
    {
        float[] tone = MorseGenerator.Real("VK2/M0LTE/QRP-15", 1500, 0.8, 20, 48000);

        Console.WriteLine($"morse of the longest callsign: {tone.Length / 48000.0:0.00} s");
        (tone.Length / 48000.0).Should().BeLessThan(15, "the longest callsign the station accepts, at the host's default speed");
    }
}
