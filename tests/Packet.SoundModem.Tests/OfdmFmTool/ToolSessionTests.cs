using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Modems.OfdmFmStation;
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

        public Side(string call, string? to, int seed, VirtualAir.Clock clock, bool canTransmit = true, bool detail = false, IdentificationNote? note = null, StationRig? rig = null)
        {
            Rig = rig ?? new StationRig(seed: seed, cold: true);
            Log = new StampedLog(clock, line => { lock (_lines) { _lines.Add(line); } });
            Session = new ToolSession(Rig.Host, call, to, detail, canTransmit, Log, clock, note);
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

    [Fact]
    public async Task Pings_between_two_cold_stations_are_all_answered_and_the_rate_is_reported_both_ways()
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
        summary.Answered.Should().Be(5, "each station identifies before it first transmits, so the answer comes back into a quiet channel");
        summary.OutBitsPerSecond.Should().NotBeNull("the modem reported the rate to the other station");
        summary.BackBitsPerSecond.Should().NotBeNull("the modem reported the rate back");
        summary.BothWayBitsPerSecond.Should().BeGreaterThan(0);

        // The run is the rig's own check that no two radios were keyed at once: it throws if they were.
        string[] lines = await pinger.LinesAsync();
        lines.Should().OnlyContain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d\d:\d\d:\d\d\.\d\d  \S"), "every line starts with the time");
        lines.Count(l => l.Contains(": 32 bytes, ") && l.Contains(" round trip, out ") && l.Contains(", back ")).Should().Be(5);
        lines.Should().Contain(l => l.EndsWith(summary.ToString(), StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("Sent 1 datagram to"), "the modem's own trace is printed unchanged");
        Console.WriteLine(string.Join(Environment.NewLine, lines));

        string[] heard = await listener.LinesAsync();
        heard.Count(l => l.Contains("answering")).Should().Be(5);
        StationRig.IsIdentification(pinger.Rig.Air.Keyups[0], "G4ABC-1").Should().BeTrue("the pinger's first transmission is its identification");
        StationRig.IsIdentification(listener.Rig.Air.Keyups[0], "M0XYZ-1").Should().BeTrue("and the listener's");
    }

    [Fact]
    public async Task On_air_from_cold_each_station_sends_its_identification_and_Morse_before_its_data_and_the_next_transmission_waits_for_the_other_stations_Morse()
    {
        var clock = new VirtualAir.Clock();
        await using var pinger = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock);
        await using var listener = new Side("M0XYZ-1", null, seed: 12, clock);
        JoinAir(clock, pinger.Rig, listener.Rig);
        pinger.Begin();
        listener.Begin();

        Task<PingSummary> ping = pinger.Session.PingAsync(2, 32, CancellationToken.None);
        await PumpAsync(clock, () => ping.IsCompleted, "the two pings to finish", pinger.Rig, listener.Rig);
        (await ping).Answered.Should().Be(2);

        // The rig's record of every keyup of both radios, in the order they began. Both stations count the same audio, so the counts are on one timeline.
        (string Who, Keyup Keyup)[] air = [.. pinger.Rig.Air.Keyups.Select(k => ("A", k)).Concat(listener.Rig.Air.Keyups.Select(k => ("B", k))).OrderBy(x => x.k.StartCount)];
        string Describe((string Who, Keyup Keyup) x) =>
            x.Who + (StationRig.IsIdentification(x.Keyup, x.Who == "A" ? "G4ABC-1" : "M0XYZ-1") ? " identification+morse" : " data");
        string[] order = [.. air.Select(Describe)];
        Console.WriteLine(string.Join(Environment.NewLine, air.Select(x => $"{Describe(x)}: {x.Keyup.StartCount / 48000.0:0.00} s to {x.Keyup.EndCount / 48000.0:0.00} s")));

        order.Should().Equal("A identification+morse", "A data", "B identification+morse", "B data", "A data", "B data");

        // Nothing overlaps, and the first station's second ping starts only after the other station's Morse has ended.
        for (int i = 1; i < air.Length; i++)
        {
            air[i].Keyup.StartCount.Should().BeGreaterThanOrEqualTo(air[i - 1].Keyup.EndCount, $"keyup {i} began after keyup {i - 1} ended");
        }

        air[4].Keyup.StartCount.Should().BeGreaterThanOrEqualTo(air[2].Keyup.EndCount);
    }

    [Fact]
    public async Task A_second_run_from_the_state_the_first_left_does_not_identify_again_inside_ten_minutes()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"pdn-ofdm-{Guid.NewGuid():N}");
        string file = Path.Combine(dir, IdentificationNote.FileName("G4ABC-1", "pipe:a,b"));
        try
        {
            // The first run, from nothing: one identification, and the time it started goes to the file.
            var clock = new VirtualAir.Clock();
            await using (var first = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock, note: new IdentificationNote(file, clock, _ => { })))
            await using (var listener = new Side("M0XYZ-1", null, seed: 12, clock))
            {
                JoinAir(clock, first.Rig, listener.Rig);
                first.Begin();
                listener.Begin();
                Task<PingSummary> ping = first.Session.PingAsync(2, 32, CancellationToken.None);
                await PumpAsync(clock, () => ping.IsCompleted, "the first run to finish", first.Rig, listener.Rig);
                (await ping).Answered.Should().Be(2);
                first.Rig.Air.Keyups.Count(k => StationRig.IsIdentification(k, "G4ABC-1")).Should().Be(1);
                await first.Session.DisposeAsync();
            }

            File.Exists(file).Should().BeTrue("the time of the identification was written");

            // Three minutes later by the clock the tool reads it against, a second run is built from that file.
            var later = new VirtualAir.Clock();
            DateTimeOffset epoch = later.GetUtcNow();
            var farOn = new VirtualAir.Clock();
            farOn.Elapse(TimeSpan.FromDays(1));
            DateTimeOffset written = new IdentificationNote(file, farOn, _ => { }).Read() ?? throw new InvalidOperationException("the first run left no time");
            later.Elapse(written - epoch + TimeSpan.FromMinutes(3));
            DateTimeOffset? read = new IdentificationNote(file, later, _ => { }).Read();
            read.Should().Be(written);

            var clock2 = new VirtualAir.Clock();
            var rig2 = new StationRig(seed: 11, startAt: later.GetUtcNow() - epoch, identifiedAt: read);
            await using var second = new Side("G4ABC-1", "M0XYZ-1", seed: 11, clock2, rig: rig2);
            await using var listener2 = new Side("M0XYZ-1", null, seed: 12, clock2);
            JoinAir(clock2, second.Rig, listener2.Rig);
            second.Begin();
            listener2.Begin();
            Task<PingSummary> again = second.Session.PingAsync(2, 32, CancellationToken.None);
            await PumpAsync(clock2, () => again.IsCompleted, "the second run to finish", second.Rig, listener2.Rig);

            (await again).Answered.Should().Be(2);
            second.Rig.Air.Keyups.Should().NotBeEmpty();
            second.Rig.Air.Keyups.Count(k => StationRig.IsIdentification(k, "G4ABC-1")).Should().Be(0, "it identified three minutes ago");
            second.Rig.MorseSent.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
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
        await PumpAsync(clock, () => sent.IsCompleted && listener.Rig.Received.Length >= 1, "the text to go and be heard", sender.Rig, listener.Rig);

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
        (await pinger.LinesAsync()).Should().Contain(l => l.Contains("no answer in 60 s"));
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
        lines.Count(l => l.Contains("no answer in 60 s")).Should().Be(2);
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
        foreach (Keyup keyup in sender.Rig.Air.Keyups.Where(k => !StationRig.IsIdentification(k, "G4ABC-1")))
        {
            Console.WriteLine($"data keyup {keyup.Audio.Length / 48000.0:0.000} s");
            (keyup.Audio.Length / 48000.0).Should().BeLessThanOrEqualTo(12.0);
        }
    }

    [Fact]
    public async Task The_identification_of_the_longest_callsign_is_the_longest_keyup_and_is_under_twenty_seconds()
    {
        await using var sender = new Side("VK2/M0LTE/QRP-15", "M0XYZ-1", seed: 11, new VirtualAir.Clock());
        sender.Begin();
        sender.Rig.Host.Send(new byte[10], LinkAddress.FromAscii("M0XYZ-1"), LinkAddress.FromAscii("VK2/M0LTE/QRP-15"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);

        await StationRig.PumpAsync(() => sender.Rig.Transmitted.Length >= 1, "the datagram to go after the identification", sender.Rig);

        Keyup ident = sender.Rig.Air.Keyups[0];
        StationRig.IsIdentification(ident, "VK2/M0LTE/QRP-15").Should().BeTrue();
        Console.WriteLine($"identification keyup of the longest callsign {ident.Audio.Length / 48000.0:0.000} s");
        (ident.Audio.Length / 48000.0).Should().BeLessThan(20);
    }
}
