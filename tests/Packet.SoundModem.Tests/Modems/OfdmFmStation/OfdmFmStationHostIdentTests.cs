using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Ident;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The identification on the station's own rule, over a real channel on a virtual clock: before the transmission that made it due, as one key-up of the identification burst, a gap and the callsign in Morse.
/// The only time is the count of samples the test feeds: the station's clock is that count, and "ten minutes" is ten minutes of fed audio (<see cref="StationRig.FeedToCountAsync"/>), never a wait.
/// The test feeds no block while a keyup is running, so every outcome is decided by what the station and the channel do, not by how long anything took.
/// </summary>
public class OfdmFmStationHostIdentTests
{
    private const int Rate = 48000;
    private const long TenMinutes = 600L * Rate;
    private static readonly LinkAddress Me = LinkAddress.FromAscii("G4ABC-1");

    private static void Identify(StationRig rig) => rig.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);

    private static SendResult SendEveryone(StationRig rig, int tag) =>
        rig.Host.Send(Datagram(tag, 40), LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(tag));

    private static byte[] Datagram(int seed, int length)
    {
        var d = new byte[length];
        new Random(seed).NextBytes(d);
        return d;
    }

    private static int IdentificationKeyups(StationRig rig) => rig.Air.Keyups.Count(k => StationRig.IsIdentification(k));

    private static int IdentificationBursts(StationRig rig) => rig.ModemSent.Count(m => m.Kind == ModemSentKind.Identification);

    /// <summary>Everything the station's clock was told, in order.</summary>
    private sealed class RecordingClock(IStationClockInput inner) : IStationClockInput
    {
        private readonly Lock _gate = new();
        private readonly List<(long Id, long Index)> _starts = [];
        private readonly List<(long Id, long Index)> _ends = [];

        public (long Id, long Index)[] Starts { get { lock (_gate) { return [.. _starts]; } } }

        public (long Id, long Index)[] Ends { get { lock (_gate) { return [.. _ends]; } } }

        public void Push(ReadOnlySpan<float> samples) => inner.Push(samples);

        public void Skip(int count) => inner.Skip(count);

        public void TransmitStarted(long id, long sampleIndex)
        {
            lock (_gate)
            {
                _starts.Add((id, sampleIndex));
            }

            inner.TransmitStarted(id, sampleIndex);
        }

        public void TransmitEnded(long id, long sampleIndex)
        {
            lock (_gate)
            {
                _ends.Add((id, sampleIndex));
            }

            inner.TransmitEnded(id, sampleIndex);
        }
    }

    [Fact]
    public async Task From_cold_the_first_keyup_is_the_identification_burst_then_the_morse_and_the_datagram_follows()
    {
        RecordingClock? clock = null;
        await using var rig = new StationRig(clockInput: inner => clock = new RecordingClock(inner), cold: true);
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go, after the identification");
        await rig.FeedAsync(100);
        await rig.IdleAsync();

        Keyup[] keyups = [.. rig.Air.Keyups];
        keyups.Should().HaveCount(2, "the identification, then the datagram");
        (long Id, float[] Samples)[] requests = rig.Requests;
        requests.Should().HaveCount(2);

        // The first keyup: the key-up delay, the station's identification burst, a gap, the callsign in Morse, the tail. One transmission.
        int lead = 100 * Rate / 1000;
        int tail = 20 * Rate / 1000;
        float[] burst = requests[0].Samples;
        float[] morse = StationRig.ExpectedMorse();
        float[] ident = keyups[0].Audio;
        ident.Length.Should().Be(lead + burst.Length + (Rate / 4) + morse.Length + tail);
        ident.AsSpan(0, lead).ToArray().Should().OnlyContain(x => x == 0f);
        ident.AsSpan(lead, burst.Length).ToArray().Should().Equal(burst, "the station's identification burst, as it made it");
        ident.AsSpan(lead + burst.Length, Rate / 4).ToArray().Should().OnlyContain(x => x == 0f, "a gap between the burst and the Morse");
        ident.AsSpan(lead + burst.Length + (Rate / 4), morse.Length).ToArray().Should().Equal(morse);
        StationRig.IsIdentification(keyups[0]).Should().BeTrue();

        // The second: the datagram, as before.
        StationRig.IsIdentification(keyups[1]).Should().BeFalse();
        keyups[1].Audio.AsSpan(lead, requests[1].Samples.Length).ToArray().Should().Equal(requests[1].Samples);

        IdentificationBursts(rig).Should().Be(1);
        rig.MorseSent.Should().Equal(["G4ABC"]);

        // The station was told of each transmission once, and the identification ended where the Morse did: the end of its keyup.
        (long Id, long Index)[] starts = clock!.Starts;
        (long Id, long Index)[] ends = clock.Ends;
        starts.Select(s => s.Index).Should().Equal(keyups[0].StartCount, keyups[1].StartCount);
        ends.Select(e => e.Index).Should().Equal(keyups[0].EndCount, keyups[1].EndCount);
    }

    [Fact]
    public async Task The_morse_tone_speed_and_level_are_the_options_and_the_station_is_told_how_long_it_lasts()
    {
        await using var rig = new StationRig(morse: new OfdmFmStationHostOptions { MorseToneHz = 1000, MorseWordsPerMinute = 15, MorseAmplitude = 0.5 }, cold: true);
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.MorseSent.Length >= 1, "the identification to go");
        await rig.IdleAsync();

        float[] tone = MorseGenerator.Real("G4ABC", 1000, 0.5, 15, Rate);
        int tail = 20 * Rate / 1000;
        Keyup ident = rig.Air.Keyups[0];
        ident.Audio.AsSpan(ident.Audio.Length - tail - tone.Length, tone.Length).ToArray().Should().Equal(tone);
        tone.Length.Should().BeGreaterThan(StationRig.ExpectedMorse().Length, "slower than the default speed, so longer");
    }

    [Fact]
    public async Task A_tone_that_cannot_be_keyed_or_a_level_of_nothing_is_refused_when_the_host_is_made()
    {
        await using var rig = new StationRig();
        Action tooHigh = () => new OfdmFmStationHost(rig.Channel, new Random(1), new OfdmFmStationHostOptions { MorseToneHz = 24000 });
        tooHigh.Should().Throw<ArgumentOutOfRangeException>();
        Action silent = () => new OfdmFmStationHost(rig.Channel, new Random(1), new OfdmFmStationHostOptions { MorseAmplitude = 0 });
        silent.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_morse_too_long_for_the_station_to_announce_is_refused_when_the_identity_is_set()
    {
        await using var rig = new StationRig(morse: new OfdmFmStationHostOptions { MorseWordsPerMinute = 5 });

        Action set = () => rig.Host.SetIdentity("VK2/M0LTE/QRP-15");

        set.Should().Throw<InvalidOperationException>().WithMessage("*faster speed*");
    }

    [Fact]
    public async Task An_idle_station_is_never_identified_however_long_the_audio_runs_and_its_first_keyup_is_then_the_identification()
    {
        await using var rig = new StationRig(cold: true);
        Identify(rig);
        rig.StartTransmitter();

        await rig.FeedToCountAsync(TenMinutes + (60L * Rate));

        rig.Air.KeyAttempts.Should().Be(0, "nothing was ever sent, so nothing is owed");
        rig.MorseSent.Should().BeEmpty();
        rig.Channel.TransmitQueued.Should().BeFalse();

        // Absence is shown by what happens next: the first keyup after all that audio is the identification, ahead of the datagram.
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");
        StationRig.IsIdentification(rig.Air.Keyups[0]).Should().BeTrue();
    }

    [Fact]
    public async Task With_no_identity_nothing_is_keyed_and_a_send_is_refused()
    {
        await using var rig = new StationRig(cold: true);
        rig.StartTransmitter();

        SendEveryone(rig, 1).Should().Be(SendResult.NoIdentity);
        await rig.FeedAsync(300);

        rig.Air.KeyAttempts.Should().Be(0);
        rig.Channel.TransmitQueued.Should().BeFalse();
        rig.MorseSent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_transmission_inside_ten_minutes_brings_no_second_identification_and_one_after_ten_minutes_does()
    {
        await using var rig = new StationRig(cold: true);
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the first datagram to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        long identStart = rig.Air.Keyups[0].StartCount;
        IdentificationKeyups(rig).Should().Be(1);

        // Inside the ten minutes: another datagram goes and nothing else does.
        await rig.FeedToCountAsync(identStart + (5 * 60L * Rate));
        int keyupsBefore = rig.Air.Keyups.Count;
        SendEveryone(rig, 2).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 2, "the second datagram to go");
        await rig.FeedAsync(100);
        await rig.IdleAsync();
        rig.Air.Keyups.Count.Should().Be(keyupsBefore + 1, "only the datagram's own keyup: it is covered by the identification of five minutes ago");
        IdentificationKeyups(rig).Should().Be(1);
        rig.MorseSent.Should().HaveCount(1);

        // After ten minutes since the identification burst started: the next transmission is preceded by a second identification.
        await rig.FeedToCountAsync(identStart + TenMinutes + Rate);
        int before = rig.Air.Keyups.Count;
        SendEveryone(rig, 3).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 3, "the third datagram to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        IdentificationKeyups(rig).Should().Be(2);
        rig.Air.Keyups.Count.Should().Be(before + 2);
        StationRig.IsIdentification(rig.Air.Keyups[before]).Should().BeTrue("the identification goes first");
        rig.MorseSent.Should().Equal("G4ABC", "G4ABC");
    }

    [Fact]
    public async Task An_identification_the_channel_refuses_is_offered_again_by_the_station_and_still_carries_the_morse()
    {
        await using var rig = new StationRig(cold: true);
        Identify(rig);
        rig.StartTransmitter();

        // The first key-up asked for is the identification: it is refused, the station offers it again, then the datagram goes.
        rig.Air.FailKeyAttempt(1);
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go after the identification was offered again");
        await rig.FeedAsync(50);
        await rig.IdleAsync();

        rig.Air.KeyAttempts.Should().Be(3);
        rig.Air.Keyups.Should().HaveCount(2);
        StationRig.IsIdentification(rig.Air.Keyups[0]).Should().BeTrue("the second offer is the identification again, with its Morse");
        rig.MorseSent.Should().Equal(["G4ABC"]);
    }

    [Fact]
    public async Task A_lease_held_by_the_hosts_own_sub_channel_lets_the_identification_through()
    {
        await using var rig = new StationRig(subChannel: 5, cold: true);
        Identify(rig);
        rig.StartTransmitter();
        rig.Channel.TransmitLease.Take(5, TimeSpan.FromMinutes(30)).Granted.Should().BeTrue();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.MorseSent.Length >= 1, "the identification to go under the host's own lease");
        IdentificationKeyups(rig).Should().Be(1);
    }

    [Fact]
    public async Task A_host_with_an_identity_and_nothing_to_send_allocates_nothing_per_block()
    {
        var radio = new FakeRadio();
        radio.Set(false);
        await using var rig = new StationRig(radio: radio);
        Identify(rig);
        await rig.FeedAsync(50);
        float[] quiet = new float[StationAir.Block];

        long allocated = AllocationMeasure.Smallest(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                rig.Channel.ProcessReceive(quiet);
            }
        });

        allocated.Should().Be(0);
    }

    [Fact]
    public async Task A_host_told_it_identified_three_minutes_ago_does_not_identify_on_its_first_transmission_and_does_once_ten_minutes_have_passed_since()
    {
        await using var rig = new StationRig(identifiedAgo: TimeSpan.FromMinutes(3));
        Identify(rig);
        rig.StartTransmitter();

        // Seven minutes of the station's own clock remain of the ten: a transmission at six is covered.
        await rig.FeedToCountAsync(6 * 60L * Rate);
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the first datagram to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        rig.Air.Keyups.Should().ContainSingle("the datagram alone");
        rig.MorseSent.Should().BeEmpty();
        IdentificationBursts(rig).Should().Be(0);

        // Past seven minutes of the station's clock the ten minutes since that identification have run out.
        await rig.FeedToCountAsync((7 * 60L * Rate) + (5L * Rate));
        SendEveryone(rig, 2).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 2, "the second datagram to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        IdentificationKeyups(rig).Should().Be(1);
        StationRig.IsIdentification(rig.Air.Keyups[1]).Should().BeTrue("the identification goes ahead of the second datagram");
        rig.MorseSent.Should().Equal(["G4ABC"]);
    }

    [Theory]
    [InlineData(11 * 60)]
    [InlineData(-5 * 60)]
    public async Task A_host_told_a_time_more_than_ten_minutes_ago_or_in_the_future_identifies_first(int agoSeconds)
    {
        await using var rig = new StationRig(identifiedAgo: TimeSpan.FromSeconds(agoSeconds));
        Identify(rig);
        rig.StartTransmitter();

        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();

        rig.Air.Keyups.Should().HaveCount(2);
        StationRig.IsIdentification(rig.Air.Keyups[0]).Should().BeTrue();
    }

    [Fact]
    public async Task A_host_with_no_record_identifies_first()
    {
        await using var rig = new StationRig(cold: true);
        Identify(rig);
        rig.StartTransmitter();

        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();

        StationRig.IsIdentification(rig.Air.Keyups[0]).Should().BeTrue();
    }

    [Fact]
    public async Task The_host_says_when_an_identification_starts_on_its_own_clock()
    {
        await using var rig = new StationRig(cold: true);
        var starts = new List<DateTimeOffset>();
        rig.Host.IdentificationStarted += starts.Add;
        Identify(rig);
        rig.StartTransmitter();
        DateTimeOffset before = rig.Clock.GetUtcNow();

        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");
        await rig.IdleAsync();

        DateTimeOffset when = starts.Should().ContainSingle().Subject;
        when.Should().BeOnOrAfter(before).And.BeOnOrBefore(rig.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Only_the_request_the_station_flags_as_its_identification_carries_the_morse_whatever_order_the_requests_come_in()
    {
        // The library never lets a lone report wait behind an identification, so after IdentificationDue a report can be requested first. It cannot be made to happen on the rig without choosing the moment
        // a burst arrives to the sample (a race between the identification waiting for a busy channel and the report that is then owed), so the host is fed the two requests directly, in that order.
        await using var rig = new StationRig(cold: true);
        Identify(rig);
        float[] report = new float[4000];
        float[] burst = new float[30000];
        Array.Fill(report, 0.25f);
        Array.Fill(burst, 0.5f);
        int lead = 100 * Rate / 1000;

        float[] reported = rig.Host.RenderRequestForTests(new TransmitRequestEvent(101, report, 0) { Identification = false }, 100);
        float[] identified = rig.Host.RenderRequestForTests(new TransmitRequestEvent(102, burst, 0) { Identification = true }, 100);

        reported.Length.Should().Be(lead + report.Length, "the report is the report and nothing more: no gap and no Morse");
        reported.AsSpan(lead).ToArray().Should().Equal(report);
        float[] morse = StationRig.ExpectedMorse();
        identified.Length.Should().Be(lead + burst.Length + (Rate / 4) + morse.Length);
        identified.AsSpan(lead + burst.Length + (Rate / 4)).ToArray().Should().Equal(morse, "the Morse lands on the flagged request");
    }
}
