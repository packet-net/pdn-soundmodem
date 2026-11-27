using AwesomeAssertions;
using M0LTE.OfdmFm;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The host's transmit half over a real <see cref="SoundModemChannel"/> on a virtual clock, one station. The only time is the count of samples the test feeds (the clock moves only as the sound card plays),
/// and the only waits are hang guards through <see cref="Eventually"/> and loops that feed audio until something the station decides happens: no outcome depends on how long a wait took.
/// Nothing here advances the clock by hand. The sound card moves it by exactly the length of each write, on the transmitter's own thread, and the test feeds no block while a keyup is running (it waits for the keyup to end first),
/// so no real background work is racing the clock and no two threads feed the channel.
/// </summary>
public class OfdmFmStationHostTransmitTests
{
    private const int Rate = 48000;
    private static readonly LinkAddress Me = LinkAddress.FromAscii("G4ABC-1");

    private static byte[] Datagram(int seed, int length)
    {
        var d = new byte[length];
        new Random(seed).NextBytes(d);
        return d;
    }

    private static void Identify(StationRig rig) => rig.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);

    private static SendResult SendEveryone(StationRig rig, int tag = 1) =>
        rig.Host.Send(Datagram(tag, 40), LinkAddress.Everyone, Me, LinkProtocol.NoLayer3, new HostTag(tag));

    private static void AssertEveryKeyupWasSilenceThenTheStationsSamplesThenTail(StationRig rig, int delayMilliseconds, int tailMilliseconds, IReadOnlyList<(long Id, float[] Samples)> played)
    {
        int lead = delayMilliseconds * Rate / 1000;
        int tail = tailMilliseconds * Rate / 1000;
        Keyup[] keyups = [.. rig.Air.Keyups];
        keyups.Should().HaveCount(played.Count, "each keyup carried one request");
        for (int i = 0; i < keyups.Length; i++)
        {
            float[] audio = keyups[i].Audio;
            float[] samples = played[i].Samples;
            audio.Length.Should().Be(lead + samples.Length + tail, "key-up delay, then the burst, then the tail");
            audio.AsSpan(0, lead).ToArray().Should().OnlyContain(x => x == 0f, "the key-up delay is silence");
            audio.AsSpan(lead, samples.Length).ToArray().Should().Equal(samples, "exactly the station's samples");
            audio.AsSpan(lead + samples.Length).ToArray().Should().OnlyContain(x => x == 0f, "the tail is silence");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_datagram_sent_is_keyed_with_the_delay_then_exactly_the_stations_samples_and_the_indices_are_the_gate(bool blockAtKey)
    {
        // With the block at key, the first keyed block comes before the transmitter says the request is on air, so the on-air mark arrives mid-run; without it, the mark comes first.
        await using var rig = new StationRig(blockAtKey);
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram's burst to leave");
        await rig.FeedAsync(100);
        await rig.IdleAsync();

        TransmittedEvent sent = rig.Transmitted.Should().ContainSingle("Transmitted is raised once for the datagram").Subject;
        (long Id, float[] Samples)[] requests = rig.Requests;
        AssertEveryKeyupWasSilenceThenTheStationsSamplesThenTail(rig, 100, 20, requests);

        // The station's own figures are the indices it was given: the time out is the end, and the airtime the end less the start.
        long end = (long)Math.Round(sent.TimeOutSeconds * Rate);
        long start = end - (long)Math.Round(sent.AirtimeSeconds * Rate);
        rig.Air.Keyups.Should().Contain(
            k => k.StartCount == start && k.EndCount == end,
            "the start is the count at the first keyed block and the end the count at the first unkeyed block after the run");
        foreach (Keyup keyup in rig.Air.Keyups)
        {
            long run = keyup.Audio.Length + (blockAtKey ? StationAir.Block : 0);
            (keyup.EndCount - keyup.StartCount).Should().Be(run, "the run of keyed blocks is the keyup, to the sample");
        }
    }

    [Fact]
    public async Task A_burst_the_pressed_key_refuses_is_offered_again_and_goes_the_next_time_unchanged()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.StartTransmitter();
        rig.Air.FailNextKeys(1);
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go after the first key-up failed");
        await rig.FeedAsync(100);
        await rig.IdleAsync();

        rig.Air.KeyAttempts.Should().BeGreaterThanOrEqualTo(2, "the first key-up threw and the burst was asked for again");
        (long Id, float[] Samples)[] requests = rig.Requests;
        requests.Length.Should().BeGreaterThanOrEqualTo(2);
        requests[1].Id.Should().NotBe(requests[0].Id);
        requests[1].Samples.Should().Equal(requests[0].Samples, "the burst is handed over again as it was built");
        rig.Transmitted.Should().ContainSingle();
        // The first request never reached the air, so the keyups pair with the requests after it.
        AssertEveryKeyupWasSilenceThenTheStationsSamplesThenTail(rig, 100, 20, requests.Skip(1).ToArray());
    }

    [Fact]
    public async Task A_receive_only_channel_never_keys_says_so_once_and_the_station_keeps_offering()
    {
        await using var rig = new StationRig();
        rig.Channel.ReceiveOnlyReason = "this station is receive-only";
        Identify(rig);
        rig.StartTransmitter();
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Requests.Length >= 4, "the station to offer the burst again and again");

        rig.Air.KeyAttempts.Should().Be(0);
        rig.Notices.Should().Equal(["this station is receive-only"]);
        rig.Requests.Select(r => r.Id).Distinct().Should().HaveCount(rig.Requests.Length, "each offer is a new request");
        rig.Requests.Select(r => r.Samples.Length).Distinct().Should().ContainSingle("the same burst each time");
        rig.Transmitted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lease_held_by_another_sub_channel_defers_the_station_and_releasing_it_lets_it_go()
    {
        await using var rig = new StationRig(subChannel: 2);
        Identify(rig);
        rig.StartTransmitter();
        rig.Channel.TransmitLease.Take(5, TimeSpan.FromMinutes(1)).Granted.Should().BeTrue();
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Requests.Length >= 3, "the station to offer the burst while the lease is held");
        rig.Air.KeyAttempts.Should().Be(0, "the lease excludes this sub-channel");
        rig.Channel.TransmitQueued.Should().BeFalse("a request the lease excludes is refused to the station, not queued in the channel");
        rig.Notices.Should().BeEmpty("a lease is nobody's news");

        rig.Channel.TransmitLease.Release().Should().BeTrue();
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go once the lease was released");
        rig.Air.KeyAttempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_lease_taken_after_the_hand_over_and_before_the_keyup_refuses_the_request_and_the_station_offers_it_again()
    {
        await using var rig = new StationRig(subChannel: 2);
        Identify(rig);
        rig.WaitForTransmitter = false;
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        // The transmitter is not running yet, so the request waits in the channel.
        await rig.PumpAsync(() => rig.Channel.TransmitQueued, "the request to be handed to the channel");
        rig.Requests.Should().ContainSingle();

        rig.Channel.TransmitLease.Take(5, TimeSpan.FromMinutes(1)).Granted.Should().BeTrue();
        rig.Channel.TransmitQueued.Should().BeFalse("taking the lease swept the queue");
        await rig.PumpAsync(() => rig.Requests.Length >= 3, "the station to be told it was refused and offer the burst again");
        rig.Air.KeyAttempts.Should().Be(0);

        rig.Channel.TransmitLease.Release();
        rig.StartTransmitter();
        rig.WaitForTransmitter = true;
        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the datagram to go once the lease was released");
    }

    [Fact]
    public async Task A_lease_held_by_the_hosts_own_sub_channel_does_not_exclude_it()
    {
        await using var rig = new StationRig(subChannel: 5);
        Identify(rig);
        rig.StartTransmitter();
        rig.Channel.TransmitLease.Take(5, TimeSpan.FromMinutes(1)).Granted.Should().BeTrue();
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        await rig.PumpAsync(() => rig.Transmitted.Length >= 1, "the holder's own station to transmit");
    }

    [Fact]
    public async Task A_request_the_station_withdraws_before_it_is_keyed_never_keys_the_radio()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.WaitForTransmitter = false;
        SendEveryone(rig).Should().Be(SendResult.Accepted);

        // Granted, handed to the channel, and waiting there (no transmitter yet). Then another station's burst starts, which the station cannot have noticed when it granted:
        // it notices it a little under a second in and takes the request back.
        await rig.PumpAsync(() => rig.Channel.TransmitQueued, "the request to be handed to the channel");
        float[] foreign = ForeignBurst(Datagram(9, 40));
        for (int at = 0; at < foreign.Length && rig.Withdrawn.Length == 0; at += StationAir.Block)
        {
            rig.Channel.ProcessReceive(foreign.AsSpan(at, Math.Min(StationAir.Block, foreign.Length - at)));
        }

        rig.Withdrawn.Should().ContainSingle("the station noticed the other burst and withdrew its request");
        rig.Channel.TransmitQueued.Should().BeFalse("the withdrawal took the request out of the channel's queue");

        // Absence is shown by what happens next: a bystander's transmission is the only keyup, and the only audio.
        rig.StartTransmitter();
        rig.WaitForTransmitter = true;
        float[] marker = Enumerable.Range(0, 2400).Select(i => 0.25f * MathF.Sin(i * 0.1f)).ToArray();
        Task bystander = rig.Channel.EnqueueTransmit(_ => marker);
        await Eventually.SignalAsync(bystander, "the bystander's transmission to be written");
        await rig.IdleAsync();

        rig.Air.Keyups.Should().ContainSingle("the withdrawn request never keyed");
        rig.Air.Keyups[0].Audio.AsSpan(0, marker.Length).ToArray().Should().Equal(marker);
        rig.Air.Keyups[0].Audio.Length.Should().Be(marker.Length + (20 * Rate / 1000), "only the bystander's audio and the tail went out");
        rig.Transmitted.Should().BeEmpty();
    }

    [Fact]
    public async Task Disposing_the_host_takes_back_a_request_still_waiting_in_the_channel()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.WaitForTransmitter = false;
        SendEveryone(rig).Should().Be(SendResult.Accepted);
        await rig.PumpAsync(() => rig.Channel.TransmitQueued, "the request to be handed to the channel");

        rig.Host.Dispose();

        rig.Channel.TransmitQueued.Should().BeFalse("a request must not key the radio after its host has gone");
        rig.StartTransmitter();
        rig.WaitForTransmitter = true;
        float[] marker = new float[1200];
        await Eventually.SignalAsync(rig.Channel.EnqueueTransmit(_ => marker), "the bystander's transmission to be written");
        await rig.IdleAsync();
        rig.Air.Keyups.Should().ContainSingle("only the bystander keyed");
    }

    [Fact]
    public async Task Another_transmitters_keyup_is_skipped_and_never_reported_to_the_station()
    {
        await using var rig = new StationRig();
        Identify(rig);
        rig.StartTransmitter();
        float[] marker = new float[4800];
        await Eventually.SignalAsync(rig.Channel.EnqueueTransmit(_ => marker), "the bystander's transmission to be written");
        await rig.IdleAsync();
        await rig.FeedAsync(5);

        rig.Air.Keyups.Should().ContainSingle();
        rig.Host.SampleCount.Should().Be(rig.Air.Keyups[0].EndCount + (5 * StationAir.Block), "the keyed run counted as samples");
        rig.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_with_nothing_to_send_and_a_radio_source_allocates_nothing_per_block()
    {
        var radio = new FakeRadio();
        radio.Set(false);
        await using var rig = new StationRig(radio: radio);
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

    /// <summary>A data burst from another station, with a second of quiet in front of it and two after.</summary>
    private static float[] ForeignBurst(byte[] datagram)
    {
        int format = StandardFormats.FirstContact;
        var from = new byte[PayloadCodec.RunBytes];
        PayloadCodec.WriteRun(0x01020304, 0, 1, from);
        ExtensionItem[] items = [new ExtensionItem(PayloadCodec.FromTag, from)];
        int symbols = PayloadCodec.SymbolsFor(format, 2 + datagram.Length, PayloadCodec.AddressBlockSize(1, false))
            ?? throw new InvalidOperationException("the datagram does not fit a burst");
        var payload = new byte[PayloadCodec.Capacity(format, symbols)];
        int length = PayloadCodec.Pack(null, items, [datagram], payload);
        length.Should().BeGreaterThan(0);

        var renderer = new BurstRenderer();
        var burst = new float[renderer.SamplesFor(format, symbols)];
        renderer.RenderData(BurstHeader.Data(5, 0, format, symbols, 0), payload.AsSpan(0, length), burst);

        var all = new float[Rate + burst.Length + (2 * Rate)];
        burst.CopyTo(all, Rate);
        return all;
    }
}
