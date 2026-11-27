using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Ident;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The Morse identification on the station's own rule, over a real channel on a virtual clock. The only time is the count of samples the test feeds: the station's clock is that count,
/// and "ten minutes" is ten minutes of fed audio (<see cref="StationRig.FeedToCountAsync"/>), never a wait. The test feeds no block while a keyup is running (it waits for the keyup to end first),
/// so no real background work races the count, and every outcome is decided by what the station and the channel do, not by how long anything took.
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

    private static float[] ExpectedMorse(string callsign = "G4ABC") => StationRig.ExpectedMorse(callsign);

    private static bool IsMorse(Keyup keyup, string callsign = "G4ABC") => StationRig.IsMorse(keyup, callsign);

    private static int MorseKeyups(StationRig rig) => rig.Air.Keyups.Count(k => IsMorse(k));

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
    public async Task After_the_first_transmission_one_morse_ident_is_keyed_as_silence_then_the_callsign_and_the_stations_own_burst_goes_once()
    {
        RecordingClock? clock = null;
        await using var rig = new StationRig(clockInput: inner => clock = new RecordingClock(inner));
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.MorseSent.Length >= 1 && IdentificationBursts(rig) >= 1, "the Morse ident and the station's identification burst to go");
        await rig.FeedAsync(100);
        await rig.IdleAsync();

        Keyup[] keyups = [.. rig.Air.Keyups];
        keyups.Should().HaveCount(3, "the datagram's burst, the Morse ident and the identification burst");
        MorseKeyups(rig).Should().Be(1, "exactly one Morse ident, keyed as its own keyup");
        keyups[1].Audio.Should().Equal(ExpectedMorse(), "the key-up delay as silence, the callsign at the configured tone, then the tail");
        IdentificationBursts(rig).Should().Be(1);
        rig.MorseSent.Should().Equal(["G4ABC"]);

        // The station was told of its own two transmissions and not of the Morse one: two starts and two ends, each at its keyup's first and last count.
        (long Id, long Index)[] starts = clock!.Starts;
        (long Id, long Index)[] ends = clock.Ends;
        starts.Select(s => s.Index).Should().Equal(keyups[0].StartCount, keyups[2].StartCount);
        ends.Select(e => e.Index).Should().Equal(keyups[0].EndCount, keyups[2].EndCount);
        keyups[1].EndCount.Should().BeGreaterThan(keyups[1].StartCount);
        starts.Concat(ends).Should().NotContain(c => c.Index > keyups[1].StartCount && c.Index < keyups[2].StartCount, "nothing of the Morse keyup was reported as the station's");
    }

    [Fact]
    public async Task The_morse_tone_speed_and_level_are_the_options()
    {
        await using var rig = new StationRig(morse: new OfdmFmStationHostOptions { MorseToneHz = 1000, MorseWordsPerMinute = 15, MorseAmplitude = 0.5 });
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.MorseSent.Length >= 1, "the Morse ident to go");
        await rig.IdleAsync();

        float[] tone = MorseGenerator.Real("G4ABC", 1000, 0.5, 15, Rate);
        int lead = 100 * Rate / 1000;
        Keyup morse = rig.Air.Keyups[1];
        morse.Audio.AsSpan(lead, tone.Length).ToArray().Should().Equal(tone);
        morse.Audio.Length.Should().NotBe(ExpectedMorse().Length, "slower than the default speed, so longer");
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
    public async Task An_idle_station_is_never_identified_however_long_the_audio_runs_and_its_first_keyup_is_then_the_datagram()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.StartTransmitter();

        await rig.FeedToCountAsync(TenMinutes + (60L * Rate));

        rig.Air.KeyAttempts.Should().Be(0, "nothing was ever sent, so nothing is owed");
        rig.MorseSent.Should().BeEmpty();
        rig.Channel.TransmitQueued.Should().BeFalse();

        // Absence is shown by what happens next: the first keyup after all that audio is the datagram's own burst.
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go");
        Keyup first = rig.Air.Keyups[0];
        first.Audio.AsSpan(100 * Rate / 1000, rig.Requests[0].Samples.Length).ToArray().Should().Equal(rig.Requests[0].Samples, "the first keyup carries the station's own burst");
        IsMorse(first).Should().BeFalse();
    }

    [Fact]
    public async Task With_no_identity_nothing_is_keyed_and_a_send_is_refused()
    {
        await using var rig = new StationRig();
        rig.StartTransmitter();

        SendEveryone(rig, 1).Should().Be(SendResult.NoIdentity);
        await rig.FeedAsync(300);

        rig.Air.KeyAttempts.Should().Be(0);
        rig.Channel.TransmitQueued.Should().BeFalse();
        rig.MorseSent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_transmission_inside_ten_minutes_brings_no_second_ident_and_one_after_ten_minutes_does()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.MorseSent.Length >= 1 && IdentificationBursts(rig) >= 1, "the first ident to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        long identStart = rig.Air.Keyups[2].StartCount;
        MorseKeyups(rig).Should().Be(1);

        // Inside the ten minutes: another datagram goes and nothing else does.
        await rig.FeedToCountAsync(identStart + (5 * 60L * Rate));
        int keyupsBefore = rig.Air.Keyups.Count;
        SendEveryone(rig, 2).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Transmitted.Length >= 2, "the second datagram to go");
        await rig.FeedAsync(100);
        await rig.IdleAsync();
        rig.Air.Keyups.Count.Should().Be(keyupsBefore + 1, "only the datagram's own keyup: its transmission is covered by the identification of five minutes ago");
        MorseKeyups(rig).Should().Be(1);
        IdentificationBursts(rig).Should().Be(1);
        rig.MorseSent.Should().HaveCount(1);

        // After ten minutes since the identification burst started: the next transmission is followed by a second ident, Morse and burst.
        await rig.FeedToCountAsync(identStart + TenMinutes + Rate);
        SendEveryone(rig, 3).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.MorseSent.Length >= 2 && IdentificationBursts(rig) >= 2, "the second ident to go");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        MorseKeyups(rig).Should().Be(2);
        IdentificationBursts(rig).Should().Be(2);
    }

    [Fact]
    public async Task A_morse_ident_the_channel_refuses_does_not_stall_the_station_and_is_not_retried_until_the_station_asks_again()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.StartTransmitter();

        // Key-ups are numbered in the order they are asked for: the datagram's burst, the Morse ident, then the identification burst.
        rig.Air.FailKeyAttempt(2);
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => IdentificationBursts(rig) >= 1, "the station's identification burst to go though the Morse ident was refused");
        await rig.FeedAsync(50);
        await rig.IdleAsync();

        rig.Air.KeyAttempts.Should().Be(3);
        MorseKeyups(rig).Should().Be(0);
        rig.MorseSent.Should().BeEmpty("a refused ident has not gone");
        rig.Air.Keyups.Should().HaveCount(2, "the datagram's burst and the identification burst");
        long identStart = rig.Air.Keyups[1].StartCount;

        // No timer of the host's retries it: nine minutes on, with the station quiet, nothing has been asked of the radio.
        await rig.FeedToCountAsync(identStart + (9 * 60L * Rate));
        rig.Air.KeyAttempts.Should().Be(3);

        // The next occasion is the station's: a transmission after ten minutes brings it due again, and this time it goes.
        await rig.FeedToCountAsync(identStart + TenMinutes + Rate);
        SendEveryone(rig, 2).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.MorseSent.Length >= 1 && IdentificationBursts(rig) >= 2, "the Morse ident to go at the next occasion");
        await rig.FeedAsync(50);
        await rig.IdleAsync();
        MorseKeyups(rig).Should().Be(1);
    }

    [Fact]
    public async Task A_lease_held_by_the_hosts_own_sub_channel_lets_the_morse_ident_through()
    {
        await using var rig = new StationRig(subChannel: 5);
        Identify(rig);
        rig.StartTransmitter();
        rig.Channel.TransmitLease.Take(5, TimeSpan.FromMinutes(30)).Granted.Should().BeTrue();
        SendEveryone(rig, 1).Should().Be(SendResult.Accepted);

        // The host's own sub-channel holds the lease: the Morse ident is attributed to the same sub-channel and goes.
        await rig.PumpAsync(() => rig.MorseSent.Length >= 1, "the Morse ident to go under the host's own lease");
        MorseKeyups(rig).Should().Be(1);
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
}
