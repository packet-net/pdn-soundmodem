using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// The relay seam: where a station's display stream goes when it is not going to a browser, and
/// the two things a monitor needs in order to draw somebody else's station out of it.
/// </summary>
/// <remarks>
/// <para>Phase 1 of <c>docs/uplink-plan.md</c>. The whole design rests on one claim - that a
/// station with no <c>publish</c> block is exactly the station it was - so the first test here
/// runs two servers side by side off one channel, one with a relay and one without, and holds
/// their browsers to the same bytes in the same order.</para>
/// <para>The rest is what the far end is owed: every received block, our own transmission at the
/// rate it is painted and not in one lump, frames whether or not anybody is watching, the bytes
/// a link is folded from, and nothing at all when nobody is watching. Plus the monitor's side,
/// which is a frame pushed in from outside and a line marked as ours because the station it came
/// from was transmitting.</para>
/// </remarks>
public class WaterfallRelayTests : IDisposable
{
    private const int SampleRate = 12000;
    private const int LinesPerSecond = 30;

    /// <summary>Samples per waterfall line: the spectrum source's hop.</summary>
    private const int HopSamples = SampleRate / LinesPerSecond;

    /// <summary>The transmit pacer's period, computed as the server computes it.</summary>
    private static readonly TimeSpan PacerPeriod = TimeSpan.FromMilliseconds(1000.0 / LinesPerSecond);

    /// <summary>Queued samples one pacer tick releases - the server's own truncation, mirrored.</summary>
    private static readonly int SamplesPerTick = (int)(PacerPeriod.TotalSeconds * SampleRate);

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(60));

    public void Dispose()
    {
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A relay that writes down what it was offered, and can be told not to want any of it or to
    /// throw at every call.
    /// </summary>
    private sealed class RecordingRelay : IWaterfallRelay
    {
        private readonly object _lock = new();
        private readonly List<(float[] Samples, bool Transmitted)> _audio = [];
        private readonly List<RelayedFrame> _frames = [];
        private readonly List<string?> _radio = [];

        public bool Wanted { get; set; } = true;

        /// <summary>Throw out of every method, as a relay whose socket has just died would.</summary>
        public bool Throws { get; set; }

        /// <summary>Calls attempted, thrown or not - so "it was still asked" is measurable.</summary>
        public int Calls { get; private set; }

        public IReadOnlyList<(float[] Samples, bool Transmitted)> AudioBlocks
        {
            get { lock (_lock) { return [.. _audio]; } }
        }

        public IReadOnlyList<RelayedFrame> Frames
        {
            get { lock (_lock) { return [.. _frames]; } }
        }

        public IReadOnlyList<string?> RadioStatuses
        {
            get { lock (_lock) { return [.. _radio]; } }
        }

        public void Audio(ReadOnlySpan<float> samples, bool transmitted)
        {
            // Copied, because the span is the caller's buffer and is a segment of a queued array
            // that the pacer goes on to hand out the rest of.
            float[] copy = samples.ToArray();
            lock (_lock)
            {
                Calls++;
                if (Throws)
                {
                    throw new InvalidOperationException("the uplink is down");
                }

                _audio.Add((copy, transmitted));
            }
        }

        public void Frame(RelayedFrame frame)
        {
            lock (_lock)
            {
                Calls++;
                if (Throws)
                {
                    throw new InvalidOperationException("the uplink is down");
                }

                _frames.Add(frame);
            }
        }

        public void Radio(string? status)
        {
            lock (_lock)
            {
                Calls++;
                if (Throws)
                {
                    throw new InvalidOperationException("the uplink is down");
                }

                _radio.Add(status);
            }
        }

        /// <summary>Every audio sample offered, in order, for one direction.</summary>
        public float[] Samples(bool transmitted) =>
            [.. AudioBlocks.Where(b => b.Transmitted == transmitted).SelectMany(b => b.Samples)];
    }

    /// <summary>
    /// A relay that goes to sleep inside <see cref="IWaterfallRelay.Audio"/>, but only for the
    /// station's own transmitted audio, so that what a stalled transmit offer does to the receive
    /// path can be measured on its own.
    /// </summary>
    private sealed class BlockingRelay : IWaterfallRelay
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly List<bool> _kinds = [];

        public bool Wanted => true;

        /// <summary>Set while a call is parked inside the transmit offer.</summary>
        public bool Inside { get; private set; }

        public IReadOnlyList<bool> Kinds
        {
            get { lock (_kinds) { return [.. _kinds]; } }
        }

        public void WaitUntilInside(CancellationToken token) => _entered.Wait(token);

        public void Release() => _release.Set();

        public void Audio(ReadOnlySpan<float> samples, bool transmitted)
        {
            lock (_kinds)
            {
                _kinds.Add(transmitted);
            }

            if (!transmitted)
            {
                return;
            }

            Inside = true;
            _entered.Set();
            _release.Wait();
            Inside = false;
        }

        public void Frame(RelayedFrame frame)
        {
        }

        public void Radio(string? status)
        {
        }
    }

    /// <summary>
    /// Attaching a relay changes nothing a browser is sent: same messages, same bytes, same order.
    /// </summary>
    /// <remarks>
    /// One channel and two servers, so the input is identical by construction rather than by
    /// repetition - the same audio, the same decode, the same status sentence reach both, and one
    /// of them is publishing. This is the phase's whole promise in one assertion, and the reason
    /// the seam is additive: nothing is diverted, nothing is reordered, and the relay is offered
    /// what the browser has already been sent rather than instead of it.
    /// </remarks>
    [Fact]
    public async Task A_Server_With_No_Relay_Sends_Exactly_What_It_Sent_Before()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));

        // One clock for both, because a link card carries the times its frames arrived and two
        // servers reading the system clock a microsecond apart would differ for a reason that has
        // nothing to do with what this test is about.
        var time = new FakeTimeProvider();
        int plainPort = FreePorts.Next();
        int relayedPort = FreePorts.Next();
        await using var plain = new WaterfallWebServer(
            channel, plainPort, new WaterfallOptions { TimeProvider = time });
        await using var relayed = new WaterfallWebServer(
            channel, relayedPort, new WaterfallOptions { TimeProvider = time });
        var relay = new RecordingRelay();
        relayed.Relay = relay;
        plain.Start();
        relayed.Start();

        // Raw, without taking the config message first: the handshake is part of what has to be
        // identical, and it is the message that carries the band overlays and the page version.
        using ClientWebSocket plainSocket = await OpenAsync(plainPort);
        using ClientWebSocket relayedSocket = await OpenAsync(relayedPort);

        // A real decode, so the frame message and the link message are the real ones, and enough
        // audio behind it to paint a few lines.
        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
        channel.ProcessReceive(new float[SampleRate / 4]);

        const string marker = "the-end";
        plain.SetRadioStatus(marker);
        relayed.SetRadioStatus(marker);

        List<(WebSocketMessageType Kind, byte[] Payload)> withoutRelay =
            await DrainAsync(plainSocket, marker);
        List<(WebSocketMessageType Kind, byte[] Payload)> withRelay =
            await DrainAsync(relayedSocket, marker);

        // Non-trivial, or the comparison below proves nothing: the handshake, the waterfall, the
        // frame, its link and the status sentence.
        withoutRelay.Select(m => Describe(m)).Should()
            .Contain("config").And.Contain("line").And.Contain("frame")
            .And.Contain("link").And.Contain("radio");

        withRelay.Should().HaveCount(withoutRelay.Count, "a relay must not add or drop a message");
        for (int i = 0; i < withoutRelay.Count; i++)
        {
            withRelay[i].Kind.Should().Be(withoutRelay[i].Kind, "message {0} changed kind", i);
            withRelay[i].Payload.Should().Equal(
                withoutRelay[i].Payload,
                "message {0} ({1}) must be byte-identical whether or not the station publishes",
                i, Describe(withoutRelay[i]));
        }

        // And the relay was genuinely attached and working throughout, or the above is a
        // comparison of two servers that both did nothing.
        relay.Frames.Should().NotBeEmpty();
        relay.AudioBlocks.Should().NotBeEmpty();
        relay.RadioStatuses.Should().Equal(marker);
    }

    /// <summary>
    /// Every block of received audio the channel hands the display is handed to the relay too,
    /// unaltered and in order.
    /// </summary>
    /// <remarks>
    /// The hook is the receive tap, beside the browser audio feed and not inside it:
    /// <c>BroadcastAudio</c> returns early when nobody has pressed Listen, and a relayed station's
    /// audio must not depend on anybody local having done so. These are also the float samples as
    /// the modems read them, before the 40 ms blocking and the s16 conversion a browser gets.
    /// </remarks>
    [Fact]
    public async Task A_Relay_Is_Offered_Every_Received_Block_The_Channel_Delivers()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(channel);
        var relay = new RecordingRelay();
        server.Relay = relay;
        server.Start();

        // Five blocks, each with its own recognisable content, so "in order and unaltered" is a
        // statement about the samples and not just about the count.
        var blocks = new List<float[]>();
        for (int block = 0; block < 5; block++)
        {
            var samples = new float[HopSamples];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (block + 1) * 0.001f * (i + 1);
            }

            blocks.Add(samples);
            channel.ProcessReceive(samples);
        }

        relay.AudioBlocks.Should().HaveCount(5, "one offer per block the channel delivered");
        relay.AudioBlocks.Should().AllSatisfy(
            b => b.Transmitted.Should().BeFalse("this is audio the station heard"));
        for (int block = 0; block < blocks.Count; block++)
        {
            relay.AudioBlocks[block].Samples.Should().Equal(
                blocks[block], "block {0} must arrive as it was received", block);
        }
    }

    /// <summary>
    /// Our own transmission reaches the relay from the display pacer, so it arrives at the rate
    /// real time passes rather than as one lump per keyup.
    /// </summary>
    /// <remarks>
    /// <para>The whole keyup exists in one array long before the sound card has played a sample
    /// of it. Hooked at <c>TransmittedAudio</c> the uplink would send a two-second burst in a few
    /// milliseconds and the monitor would paint it in one lurch, which is the bug
    /// <c>WaterfallTransmitPacingTests</c> exists to keep fixed on the station's own display.
    /// Hooked at the paced loop instead, the pacing comes free and the relayed picture trails the
    /// modulator by exactly what the sound card does.</para>
    /// <para>Time is a <see cref="FakeTimeProvider"/>, so every figure here is exact: a tick
    /// releases <see cref="SamplesPerTick"/> samples, which is 399 rather than 400 because the
    /// period truncates to whole ticks and the budget to whole samples, exactly as the server
    /// computes it.</para>
    /// </remarks>
    [Fact]
    public async Task A_Relay_Is_Offered_Transmitted_Audio_At_The_Rate_It_Is_Painted()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        long transmitted = 0;
        channel.TransmittedAudio += samples => transmitted += samples.Length;

        await using WaterfallWebServer server = WaterfallWebServer.Routed(
            channel, new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = time });
        var relay = new RecordingRelay();
        server.Relay = relay;
        server.Start();

        await TransmitAsync(channel, Payload(60));
        transmitted.Should().BeGreaterThan(
            2L * SamplesPerTick, "the burst must outlast the ticks this test paces through");

        // The bug, as an assertion: the whole keyup handed over in the instant the modulator
        // finished. No display time has passed, so nothing may have been relayed.
        relay.AudioBlocks.Should().BeEmpty(
            "a keyup must cross the wire across its own duration, not in one instant chunk");

        // One tick at a time, and each releases exactly what the display paints.
        for (int tick = 1; tick <= 3; tick++)
        {
            time.Advance(PacerPeriod);
            relay.Samples(transmitted: true).Should().HaveCount(
                tick * SamplesPerTick, "tick {0} must relay exactly the samples it painted", tick);
        }

        // And the rest of it arrives, once, by the time its own duration has passed.
        for (int tick = 3; tick * (long)SamplesPerTick < transmitted + SamplesPerTick; tick++)
        {
            time.Advance(PacerPeriod);
        }

        relay.Samples(transmitted: true).Should().HaveCount(
            (int)transmitted, "the whole keyup, once, and no silence after it");
        relay.AudioBlocks.Should().AllSatisfy(
            b => b.Transmitted.Should().BeTrue("nothing was received during a keyup"));
    }

    /// <summary>
    /// A relay that is slow in the transmit offer does not stop the station reading its sound
    /// card.
    /// </summary>
    /// <remarks>
    /// <para>The offer used to be made inside <c>_sourceLock</c>, which is the lock the receive
    /// tap takes to paint, on the station's own audio read thread. A relay that was slow there did
    /// not merely lose its own block: it parked that thread, and the station stopped consuming
    /// from the sound card for as long as the relay took. A website being unreachable must not do
    /// that to a node passing traffic (uplink plan 4.3), so the offer is made below the lock over
    /// the same list, in the same order.</para>
    /// <para>The relay here blocks on transmitted audio only. A relay that blocked on everything
    /// would stall the receive path through the documented "must return promptly" contract on
    /// <see cref="IWaterfallRelay.Audio"/> instead, which is a different fault with a different
    /// answer; this test is about the lock.</para>
    /// </remarks>
    [Fact]
    public async Task A_Relay_Slow_In_The_Transmit_Offer_Does_Not_Hold_Up_The_Receive_Path()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(
            channel, new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = time });
        var relay = new BlockingRelay();
        server.Relay = relay;
        server.Start();

        await TransmitAsync(channel, Payload(60));

        // One advance big enough to release the whole queue, so the pacer is inside the tick that
        // empties it: the transmit pending count is already zero and the station is unkeyed, which
        // is precisely the window in which the receive tap wants the source lock.
        Task painting = Task.Run(() => time.Advance(TimeSpan.FromSeconds(30)), _cancellation.Token);
        relay.WaitUntilInside(_cancellation.Token);

        Task receiving = Task.Run(() => channel.ProcessReceive(new float[HopSamples]), _cancellation.Token);
        // Awaited against a deadline rather than blocked on, so that the bug this pins shows up as
        // a failed assertion with a sentence rather than as a suite that never finishes.
        Task first = await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(5)));
        bool returned = ReferenceEquals(first, receiving);
        bool stillInside = relay.Inside;
        relay.Release();
        await receiving;
        await painting;

        stillInside.Should().BeTrue(
            "the relay must still have been parked in the transmit offer, or this proves nothing");
        returned.Should().BeTrue(
            "the station's audio read thread must not wait on a relay that is painting a keyup");

        // And it went through the gate rather than round it, which is what says it took the lock
        // the relay would have been holding.
        relay.Kinds.Should().Contain(false, "the received block was drawn as well as delivered");
    }

    /// <summary>
    /// Once a keyup is over, the audio the station is still painting and the audio it has started
    /// hearing again do not go up interleaved.
    /// </summary>
    /// <remarks>
    /// <para>The pacer keeps painting for as long as the sound card is still playing, which
    /// outlives the unkey whenever the device's Drain returns early - the normal case. In that
    /// window the input has already resumed delivering blocks, and the station drops them from its
    /// own picture on purpose, because one transform accumulator holding part of a burst and part
    /// of the band noise comes out broadband.</para>
    /// <para>The relay gets what the picture is drawn from, for the same reason: the monitor draws
    /// its picture from these blocks, so an interleaved stream would reproduce that haze in
    /// somebody else's browser and a listener would hear the keyup and the band at once. It also
    /// makes the wire format possible at all - fixed-length audio messages (4.2) and never two
    /// kinds in one block (4.3) cannot both be met by a client fed alternating blocks.</para>
    /// </remarks>
    [Fact]
    public async Task The_Drain_After_A_Keyup_Relays_One_Kind_Of_Audio_At_A_Time()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        long transmitted = 0;
        channel.TransmittedAudio += samples => transmitted += samples.Length;

        await using WaterfallWebServer server = WaterfallWebServer.Routed(
            channel, new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = time });
        var relay = new RecordingRelay();
        server.Relay = relay;
        server.Start();

        await TransmitAsync(channel, Payload(60));

        // What a sound card does across a drain: a display tick's worth of time passes, and a
        // block of received audio turns up, over and over until the queue is empty and past it.
        int ticks = (int)((transmitted + SamplesPerTick - 1) / SamplesPerTick) + 4;
        for (int tick = 0; tick < ticks; tick++)
        {
            time.Advance(PacerPeriod);
            channel.ProcessReceive(new float[HopSamples]);
        }

        string sequence = string.Concat(relay.AudioBlocks.Select(b => b.Transmitted ? "T" : "r"));
        sequence.Should().Contain("T").And.Contain("r", "both halves of the drain must be covered");
        sequence.Should().MatchRegex(
            "^T+r+$",
            "the keyup goes up, then the band does: one switch, and never a received block in the "
            + "middle of a transmission the monitor is still painting");
    }

    /// <summary>
    /// A relay that says nobody is watching is offered no audio at all, in either direction.
    /// </summary>
    /// <remarks>
    /// The etiquette of the whole project in one assertion: nothing runs on somebody's station,
    /// and nothing leaves their line, without a viewer. Read before anything is done with the
    /// samples, so an idle uplink costs a station one property read per block.
    /// </remarks>
    [Fact]
    public async Task A_Relay_That_Is_Not_Wanted_Is_Offered_No_Audio_At_All()
    {
        var time = new FakeTimeProvider();
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(
            channel, new WaterfallOptions { LinesPerSecond = LinesPerSecond, TimeProvider = time });
        var relay = new RecordingRelay { Wanted = false };
        server.Relay = relay;
        server.Start();

        channel.ProcessReceive(new float[HopSamples * 5]);
        await TransmitAsync(channel, Payload(60));
        for (int tick = 0; tick < LinesPerSecond; tick++)
        {
            time.Advance(PacerPeriod);
        }

        relay.AudioBlocks.Should().BeEmpty("nobody is watching, so no audio leaves the station");

        // But it is still an attached relay rather than a detached one, which is what makes the
        // assertion above about Wanted and not about the wiring. (It already holds the frame the
        // keyup above listed: frames are not gated on Wanted, which is the next test.)
        int before = relay.Frames.Count;
        server.ReportIdBeacon(0, "afsk300-multi11", "KK4HEJ", "IDENT", 17);
        relay.Frames.Should().HaveCount(before + 1);
        relay.Frames[^1].From.Should().Be("KK4HEJ");
    }

    /// <summary>
    /// Frames go up whether or not anybody is watching, unlike the audio.
    /// </summary>
    /// <remarks>
    /// They are a few hundred bytes each and they are what makes a quiet band look alive to
    /// somebody arriving an hour later, so an uplink carrying nothing else still carries these.
    /// <see cref="IWaterfallRelay.Wanted"/> gates the audio and nothing else.
    /// </remarks>
    [Fact]
    public async Task A_Relay_Is_Offered_Frames_Whether_Or_Not_It_Wants_Audio()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(channel);
        var relay = new RecordingRelay { Wanted = false };
        server.Relay = relay;
        server.Start();

        server.ReportFrame(2, "ConReq500M", "M0LTE", "GB7RDG", 22, snrDb: 14.0, decodedOk: true);
        relay.Frames.Should().ContainSingle("a frame is worth sending to nobody in particular");

        relay.Wanted = true;
        server.ReportFrame(2, "ConReq500M", "GB7RDG", "M0LTE", 18, snrDb: 12.0, decodedOk: true);
        relay.Frames.Should().HaveCount(2, "and it does not stop being worth it once somebody looks");

        RelayedFrame first = relay.Frames[0];
        first.SubChannel.Should().Be(2);
        first.Mode.Should().Be("ConReq500M");
        first.From.Should().Be("M0LTE");
        first.To.Should().Be("GB7RDG");
        first.LengthBytes.Should().Be(22);
        first.SnrDb.Should().Be(14.0);
        first.CrcValid.Should().BeTrue();
        first.Transmitted.Should().BeFalse();
        first.At.Should().NotBe(default, "a relayed frame carries when it happened");
    }

    /// <summary>
    /// A decoded AX.25 frame reaches the relay with the bytes it was read from.
    /// </summary>
    /// <remarks>
    /// The bytes are what the far end folds its own links panel out of. A link card is a fold
    /// over frames, so sending one would be sending a summary of something already on the wire,
    /// and two implementations that can disagree; sending the bytes means one observer, one set
    /// of cards, and a fold that survives the station going off the air.
    /// </remarks>
    [Fact]
    public async Task A_Relay_Is_Offered_The_Raw_Bytes_Of_An_Ax25_Frame()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(channel);
        var relay = new RecordingRelay();
        server.Relay = relay;
        server.Start();

        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
        channel.ProcessReceive(new float[SampleRate / 4]);

        RelayedFrame frame = relay.Frames.Should().ContainSingle().Subject;
        frame.From.Should().Be("M0LTE-9");
        frame.To.Should().Be("GB7RDG");
        frame.Raw.Should().Equal(TestFrame(), "the far end reads its own links out of these");
        frame.LengthBytes.Should().Be(TestFrame().Length);
        frame.IdBeacon.Should().BeFalse();
        frame.Transmitted.Should().BeFalse();
    }

    /// <summary>
    /// An ident heard by a beacon ghost reaches the relay, and has no bytes to carry.
    /// </summary>
    /// <remarks>
    /// The two public <c>Report*</c> entry points are for decoders outside the channel - an
    /// ident ghost and ARDOP's own demodulator - and neither hands its bytes over. Listed, badged
    /// and relayed all the same; it simply makes no link, which is what it did on the station.
    /// </remarks>
    [Fact]
    public async Task An_Id_Beacon_Reaches_A_Relay_Without_Raw_Bytes()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(channel);
        var relay = new RecordingRelay();
        server.Relay = relay;
        server.Start();

        server.ReportIdBeacon(0, "afsk300-multi11", "KK4HEJ", "IDENT", 17, offsetHz: -35.4);

        RelayedFrame frame = relay.Frames.Should().ContainSingle().Subject;
        frame.IdBeacon.Should().BeTrue("the badge is the whole point of an ident row");
        frame.From.Should().Be("KK4HEJ");
        frame.To.Should().Be("IDENT");
        frame.OffsetHz.Should().Be(-35.4);
        frame.Raw.Should().BeNull("a ghost hands over no bytes, and inventing some would be worse");
    }

    /// <summary>
    /// A relay that throws loses its own message and costs the station nothing else.
    /// </summary>
    /// <remarks>
    /// The uplink is a courtesy. A node passing traffic at three in the morning does not stop
    /// hearing, decoding or serving its own page because a website is unreachable, and the relay
    /// is called from the receive loop, the display pacer and the decoder - three places where an
    /// escaping exception would be exactly that. It is not detached either: it is asked again
    /// next time, because a socket that has just died is one that is about to be reconnected.
    /// </remarks>
    [Fact]
    public async Task A_Relay_That_Throws_Costs_Its_Own_Message_And_Nothing_Else()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        var relay = new RecordingRelay { Throws = true };
        server.Relay = relay;
        server.Start();

        using ClientWebSocket socket = await ConnectAsync(port);

        var transmitter = new Afsk1200Modem(SampleRate, _ => { });
        Action feeding = () =>
        {
            channel.ProcessReceive(transmitter.Modulate(TestFrame(), txDelayMilliseconds: 100));
            channel.ProcessReceive(new float[SampleRate / 4]);
        };

        feeding.Should().NotThrow("the receive loop must not carry a website's exception");

        const string marker = "still-here";
        Action status = () => server.SetRadioStatus(marker);
        status.Should().NotThrow();

        relay.Calls.Should().BeGreaterThan(2, "every offer was still made, and each one threw");

        // The browser got everything it would have got from a station that publishes nothing.
        List<(WebSocketMessageType Kind, byte[] Payload)> messages = await DrainAsync(socket, marker);
        messages.Select(m => Describe(m)).Should()
            .Contain("line").And.Contain("frame").And.Contain("link").And.Contain("radio");

        // And the relay is still attached: the next thing that happens is offered to it too.
        relay.Throws = false;
        server.ReportIdBeacon(0, "afsk300-multi11", "KK4HEJ", "IDENT", 17);
        relay.Frames.Should().ContainSingle(
            "a relay is not struck off for one failure; the next message is offered as usual");
    }

    /// <summary>
    /// A frame pushed in from outside is listed in the panel and read into the links pane, exactly
    /// as one this process decoded for itself.
    /// </summary>
    /// <remarks>
    /// The monitor's side of the seam. A relayed station's decodes are its own - its modems, its
    /// diversity settings, its antenna - and a monitor runs no modem for it, so there is no
    /// channel event to carry them and <see cref="SoundModemChannel"/> has no injection point.
    /// This is the one entry point instead, and it does both halves: the flat row, then the link.
    /// </remarks>
    [Fact]
    public async Task A_Pushed_Frame_Is_Listed_And_Read_Into_The_Links_Panel()
    {
        // A channel with no modems at all: what a relayed station is on the monitor.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using ClientWebSocket socket = await ConnectAsync(port);

        server.PushFrame(new RelayedFrame
        {
            SubChannel = 0,
            Mode = "afsk300-il2pc",
            From = "M0LTE-9",
            To = "GB7RDG",
            LengthBytes = 24,
            SnrDb = 11.5,
            BurstLines = 9,
            OffsetHz = -3.5,
            CorrectedBytes = 0,
            CrcValid = true,
            At = DateTimeOffset.UnixEpoch,
            Raw = TestFrame(),
        });

        const string marker = "pushed";
        server.SetRadioStatus(marker);
        List<(WebSocketMessageType Kind, byte[] Payload)> messages = await DrainAsync(socket, marker);

        // The flat row the panel lists, then the link it turned out to be part of, in that order:
        // the same pair, in the same order, a heard frame produces on a station.
        messages.Select(m => Describe(m)).Where(t => t is "frame" or "link").Should()
            .Equal("frame", "link");

        using JsonDocument frame = JsonDocument.Parse(
            messages.First(m => Describe(m) == "frame").Payload);
        frame.RootElement.GetProperty("sub").GetInt32().Should().Be(0);
        frame.RootElement.GetProperty("mode").GetString().Should().Be("afsk300-il2pc");
        frame.RootElement.GetProperty("from").GetString().Should().Be("M0LTE-9");
        frame.RootElement.GetProperty("to").GetString().Should().Be("GB7RDG");
        frame.RootElement.GetProperty("lenBytes").GetInt32().Should().Be(24);
        frame.RootElement.GetProperty("snrDb").GetDouble().Should().Be(11.5);
        frame.RootElement.GetProperty("offsetHz").GetDouble().Should().Be(-3.5);

        // And the monitor now holds the card itself, so it survives the station going off the air
        // and is there for a browser that arrives afterwards.
        server.Links.Snapshot().Should().ContainSingle()
            .Which.Id.Should().Be("0|GB7RDG<>M0LTE-9");
    }

    /// <summary>
    /// A pushed frame that Reed-Solomon alone stood behind is listed and opens no link, and
    /// neither does one that arrived with no bytes.
    /// </summary>
    /// <remarks>
    /// The rule <c>OnFrame</c> applies to a local decode, applied to a relayed one for the same
    /// reason (PR #394): a reading that only Reed-Solomon vouched for is not evidence that the
    /// pair of callsigns in it were ever talking, so a corrupt or forged pair read out of one must
    /// not open a card or name a station as heard. It stays in the frames panel badged RS ONLY.
    /// The no-bytes half is the ident ghost's case arriving from the other end.
    /// </remarks>
    [Fact]
    public async Task A_Pushed_Frame_Reed_Solomon_Alone_Stood_Behind_Makes_No_Link()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using ClientWebSocket socket = await ConnectAsync(port);

        RelayedFrame Pushed() => new()
        {
            SubChannel = 0, Mode = "bpsk300-il2pc", From = "M0LTE-9", To = "GB7RDG",
            LengthBytes = 24, At = DateTimeOffset.UnixEpoch, Raw = TestFrame(),
        };

        server.PushFrame(Pushed() with { MonitorOnly = true, PlainIl2p = true });
        server.Links.Snapshot().Should().BeEmpty(
            "Reed-Solomon alone is not evidence that these two stations were talking");

        server.PushFrame(Pushed() with { Raw = null });
        server.Links.Snapshot().Should().BeEmpty("there are no bytes to read a link out of");

        // The ordinary one, so the two refusals above are refusals rather than a panel that never
        // works at all.
        server.PushFrame(Pushed());
        server.Links.Snapshot().Should().ContainSingle()
            .Which.Id.Should().Be("0|GB7RDG<>M0LTE-9");

        // And all three are listed: withheld from the links pane is not withheld from the panel.
        const string marker = "three-pushed";
        server.SetRadioStatus(marker);
        List<(WebSocketMessageType Kind, byte[] Payload)> messages = await DrainAsync(socket, marker);
        messages.Count(m => Describe(m) == "frame").Should().Be(3, "every one of them is listed");
        messages.Count(m => Describe(m) == "link").Should().Be(1, "only one of them made a card");
    }

    /// <summary>
    /// Nothing is offered to a relay once the server has been disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="SoundModemChannel"/> has no way to remove a receive tap, so the tap this server
    /// registered goes on being called for as long as the channel lives. That costs a disposed
    /// server nothing, having no browsers left to disappoint, but a relay is an object with a
    /// socket and a lifetime of its own, and "the station stopped, so the uplink stopped" should
    /// be a fact rather than a coincidence.
    /// </remarks>
    [Fact]
    public async Task Nothing_Is_Offered_To_A_Relay_After_The_Server_Is_Disposed()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        WaterfallWebServer server = WaterfallWebServer.Routed(channel);
        var relay = new RecordingRelay();
        server.Relay = relay;
        server.Start();

        channel.ProcessReceive(new float[HopSamples]);
        server.ReportIdBeacon(0, "afsk300-multi11", "KK4HEJ", "IDENT", 17);
        server.SetRadioStatus("before");
        int offered = relay.Calls;
        offered.Should().BeGreaterThan(2, "the relay was working before the stop");

        await server.DisposeAsync();

        // Everything the station could still do to it: the tap is still registered on the channel,
        // and both public entry points still exist.
        channel.ProcessReceive(new float[HopSamples * 4]);
        server.ReportIdBeacon(0, "afsk300-multi11", "KK4HEJ", "IDENT", 17);
        server.SetRadioStatus("after");

        relay.Calls.Should().Be(offered, "a disposed server has stopped publishing");
    }

    /// <summary>
    /// A pushed frame is tagged onto the line the display has actually reached, which on a monitor
    /// is its own count over the audio it has been given.
    /// </summary>
    /// <remarks>
    /// The tag is what makes a burst on screen read as "who", so the line index has to be the
    /// monitor's and not the station's - the two count different audio and are minutes apart on a
    /// station that has been up for a while.
    /// </remarks>
    [Fact]
    public async Task A_Pushed_Frame_Is_Tagged_Onto_The_Current_Line()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using ClientWebSocket socket = await ConnectAsync(port);

        // Exactly ten hops of audio, so exactly ten lines: the next one carries index 10.
        channel.ProcessReceive(new float[HopSamples * 10]);

        server.PushFrame(new RelayedFrame
        {
            SubChannel = 0, Mode = "afsk300-il2pc", From = "M0LTE-9", To = "GB7RDG",
            LengthBytes = 24, At = DateTimeOffset.UnixEpoch,
        });

        const string marker = "tagged";
        server.SetRadioStatus(marker);
        List<(WebSocketMessageType Kind, byte[] Payload)> messages = await DrainAsync(socket, marker);

        messages.Count(m => Describe(m) == "line").Should().Be(10, "ten hops paint ten lines");

        using JsonDocument frame = JsonDocument.Parse(
            messages.First(m => Describe(m) == "frame").Payload);
        frame.RootElement.GetProperty("line").GetInt64().Should().Be(
            10, "the tag lands on the line the display had reached, not on one the station counted");
    }

    /// <summary>
    /// Audio a relayed station was transmitting paints a line marked as ours, not as a strong
    /// station somebody heard.
    /// </summary>
    /// <remarks>
    /// On a station the flag is set by the display pacer, which never runs on a monitor: relayed
    /// audio arrives as ordinary receive audio through an <c>IAudioInput</c> and there is nothing
    /// about it here to tell the two apart. The uplink's audio message says which kind each block
    /// is, and the input sets this immediately before it returns the block. Reading and processing
    /// are the same thread, so it is exact.
    /// </remarks>
    [Fact]
    public async Task Incoming_Transmit_Marks_A_Line_As_Ours()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using ClientWebSocket socket = await ConnectAsync(port);

        server.IncomingIsTransmit = false;
        channel.ProcessReceive(new float[HopSamples]);
        server.IncomingIsTransmit = true;
        channel.ProcessReceive(new float[HopSamples]);
        server.IncomingIsTransmit = false;
        channel.ProcessReceive(new float[HopSamples]);

        const string marker = "three-lines";
        server.SetRadioStatus(marker);
        List<(WebSocketMessageType Kind, byte[] Payload)> messages = await DrainAsync(socket, marker);

        messages.Where(m => Describe(m) == "line").Select(m => m.Payload[0]).Should().Equal(
            new byte[] { 0x01, 0x03, 0x01 },
            "a relayed station's own keyup is drawn in its own style, and the block either side "
            + "of it is not");
    }

    /// <summary>
    /// A channel with no modems at all still draws the bands it was told about.
    /// </summary>
    /// <remarks>
    /// What a relayed station is on the monitor: a channel that demodulates nothing, whose band
    /// overlays therefore cannot be probed and have to come off the wire. The mechanism is the one
    /// ARDOP has used since it was added - a band nothing enumerable carries, declared instead -
    /// so this is a check that it holds when there is nothing enumerable at all rather than one
    /// entry missing from a list.
    /// </remarks>
    [Fact]
    public async Task A_Channel_With_No_Modems_Still_Draws_Its_Declared_Bands()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port, new WaterfallOptions
        {
            DeclaredBands =
            [
                new DeclaredBand(0, "afsk300-il2pc", 850, 500),
                new DeclaredBand(2, "bpsk300", 2150, 300),
            ],
        });
        server.Start();

        channel.Modems.Should().BeEmpty("a relayed station demodulates nothing on the monitor");
        server.Bands.Select(b => b.SubChannel).Should().Equal(0, 2);
        server.Bands.Select(b => b.Mode).Should().Equal("afsk300-il2pc", "bpsk300");
        server.Bands[0].LowHz.Should().Be(600);
        server.Bands[0].HighHz.Should().Be(1100);

        // And the browser is told, which is what actually draws them.
        (ClientWebSocket socket, byte[] config) = await ConnectWithConfigAsync(port);
        using ClientWebSocket _ = socket;
        using JsonDocument opening = JsonDocument.Parse(config);
        JsonElement modems = opening.RootElement.GetProperty("modems");
        modems.GetArrayLength().Should().Be(2);
        modems[0].GetProperty("mode").GetString().Should().Be("afsk300-il2pc");
        modems[0].GetProperty("centreHz").GetDouble().Should().Be(850);
        modems[1].GetProperty("mode").GetString().Should().Be("bpsk300");
        modems[1].GetProperty("centreHz").GetDouble().Should().Be(2150);
    }

    /// <summary>
    /// The status sentence reaches the relay, including the one that says there is nothing to say.
    /// </summary>
    [Fact]
    public async Task A_Radio_Status_Sentence_Reaches_A_Relay()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        await using WaterfallWebServer server = WaterfallWebServer.Routed(channel);
        var relay = new RecordingRelay { Wanted = false };
        server.Relay = relay;
        server.Start();

        server.SetRadioStatus("External ref locked");
        server.SetRadioStatus("External ref locked");   // unchanged: not said twice
        server.SetRadioStatus(null);

        relay.RadioStatuses.Should().Equal(
            new string?[] { "External ref locked", null },
            "the chip going blank is a change like any other, and not the absence of one");
    }

    /// <summary>GB7RDG heard calling M0LTE-9: the frame every test here decodes.</summary>
    private static byte[] TestFrame()
    {
        var frame = new byte[24];
        WriteAddress(frame, 0, "GB7RDG", 0, last: false);
        WriteAddress(frame, 7, "M0LTE", 9, last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        Encoding.ASCII.GetBytes("hi there").CopyTo(frame, 16);
        return frame;
    }

    private static void WriteAddress(byte[] frame, int at, string call, int ssid, bool last)
    {
        for (int n = 0; n < 6; n++)
        {
            frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
        }

        frame[at + 6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
    }

    private static byte[] Payload(int length)
    {
        var frame = new byte[length];
        for (int i = 0; i < length; i++)
        {
            frame[i] = (byte)(i * 7);
        }

        return frame;
    }

    /// <summary>
    /// Runs a real transmit through the transmitter loop the daemon uses, and returns once the
    /// keyup is completely over - so the whole burst is sitting in the display pacer's queue and
    /// nothing more will be added to it.
    /// </summary>
    private async Task TransmitAsync(SoundModemChannel channel, byte[] frame)
    {
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;
        var unkeyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.TransmittingChanged += keyed =>
        {
            if (!keyed)
            {
                unkeyed.TrySetResult();
            }
        };

        using var stop = new CancellationTokenSource();
        var output = new Channel.FakeAudioOutput(channel.SampleRate);
        Task transmitter = channel.RunTransmitterAsync(output, new NullPtt(), stop.Token);
        await channel.EnqueueTransmit(0, frame).WaitAsync(_cancellation.Token);
        await unkeyed.Task.WaitAsync(_cancellation.Token);
        await stop.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>What a message is, for an assertion that reads like the wire.</summary>
    private static string Describe((WebSocketMessageType Kind, byte[] Payload) message)
    {
        if (message.Kind == WebSocketMessageType.Binary)
        {
            return message.Payload[0] switch
            {
                0x01 or 0x03 => "line",
                0x02 => "audio",
                _ => "binary",
            };
        }

        using JsonDocument json = JsonDocument.Parse(message.Payload);
        return json.RootElement.GetProperty("type").GetString() ?? "?";
    }

    /// <summary>Connects a browser and takes its config message, which always comes first.</summary>
    private async Task<ClientWebSocket> ConnectAsync(int port) =>
        (await ConnectWithConfigAsync(port)).Socket;

    private async Task<(ClientWebSocket Socket, byte[] Config)> ConnectWithConfigAsync(int port)
    {
        ClientWebSocket socket = await OpenAsync(port);
        (_, byte[] config) = await ReceiveAsync(socket);
        return (socket, config);
    }

    /// <summary>Connects a browser and leaves every message it is sent on the socket.</summary>
    private async Task<ClientWebSocket> OpenAsync(int port)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), _cancellation.Token);
        return socket;
    }

    /// <summary>
    /// Every message already broadcast, in order, bounded by a marker rather than by waiting:
    /// <see cref="WaterfallWebServer.SetRadioStatus"/> goes through the same FIFO client queue as
    /// everything else, so once its text arrives, everything sent before it has been counted.
    /// </summary>
    private async Task<List<(WebSocketMessageType Kind, byte[] Payload)>> DrainAsync(
        ClientWebSocket socket, string marker)
    {
        var messages = new List<(WebSocketMessageType, byte[])>();
        while (true)
        {
            (WebSocketMessageType kind, byte[] payload) = await ReceiveAsync(socket);
            messages.Add((kind, payload));
            if (kind == WebSocketMessageType.Text
                && Encoding.UTF8.GetString(payload).Contains($"\"{marker}\"", StringComparison.Ordinal))
            {
                return messages;
            }
        }
    }

    private async Task<(WebSocketMessageType Kind, byte[] Payload)> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        int filled = 0;
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer, filled, buffer.Length - filled), _cancellation.Token);
            filled += result.Count;
            if (result.EndOfMessage)
            {
                return (result.MessageType, buffer[..filled]);
            }
        }
    }
}
