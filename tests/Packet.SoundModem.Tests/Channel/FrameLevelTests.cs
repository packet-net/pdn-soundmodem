using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;
using DaemonStation = Packet.SoundModem.Daemon.Station;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The level a decoded frame carries: measured over the frame's own stretch of audio, and not
/// over whatever else was on the input around it.
/// </summary>
/// <remarks>
/// <para>Tom, 2026-09-06 (issue #426): "it's actually very hard to use the volume meter to check
/// the received audio level of frames when the frames are incredibly short on the fast modes".
/// The failure this guards against is the one that makes the meter useless for the job - on an FM
/// radio with the squelch open, the noise between the frames is louder than the frames, so any
/// reading that is a few tens of milliseconds out reports the hiss instead.</para>
/// <para>Driven through a real <see cref="DaemonStation"/> with a real modulator and a real
/// demodulator, in the style of the level meter's own real-path test, because the alignment is
/// the whole claim and the alignment is a property of the wiring: what feeds what, in which
/// order, and where in the input stream the demodulator says the frame was. The station reads
/// its default 100 ms blocks, which is what every station without ARDOP on it does and the case
/// the first cut of this feature got wrong.</para>
/// </remarks>
public class FrameLevelTests
{
    /// <summary>
    /// A 12 kHz station, so the card rate and the channel rate are the same one.
    /// </summary>
    /// <remarks>
    /// Deliberately not 48 kHz here. The decimating FIR between the two moves peaks by up to
    /// about 1.3 dB, and this test asserts a level to within 1 dB: at 12 kHz the audio the
    /// modulator made is the audio the history measures, so a failure is a failure of the
    /// alignment rather than of the filter. The 48 kHz path with its decimator is what
    /// <c>WaterfallLevelMeterTests</c> drives.
    /// </remarks>
    private const int SampleRate = 12000;

    /// <summary>The frame's own peak: -16.5 dBFS, inside the band the meter tells an operator to
    /// aim for, so a correct reading also earns no badge.</summary>
    private const float FramePeak = 0.15f;

    /// <summary>And the noise either side of it: -3.1 dBFS, which is louder than the frame,
    /// nearly at the top of the scale, and would be badged TOO LOUD if it were reported.</summary>
    private const float NoisePeak = 0.7f;

    /// <summary>A frame far enough under the band to earn the other badge: -34 dBFS.</summary>
    private const float QuietFramePeak = 0.02f;

    /// <summary>The tone in the alignment sweep: -0.9 dBFS, 15.6 dB over the frame and the
    /// loudest thing anything could mistake for it.</summary>
    private const float TonePeak = 0.9f;

    /// <summary>
    /// A frame between two stretches of noise louder than itself reports its own level.
    /// </summary>
    [Fact]
    public async Task A_Frames_Level_Is_Its_Own_Burst_And_Not_The_Noise_Around_It()
    {
        byte[] frame = Ax25UiFrame.Build("GB7RDG", "M0LTE", "level check"u8.ToArray());
        float[] audio = NoiseThenFrameThenNoise(frame, railTheNoise: false);

        FrameQuality quality = await DecodeAsync(audio);

        quality.PeakDbFs.Should().NotBeNull("a frame heard on a station with a card carries one");
        quality.PeakDbFs!.Value.Should().BeApproximately(
            20 * Math.Log10(FramePeak),
            1,
            "the level is measured over the frame's own audio, not over the block it decoded in "
                + "and not over the noise either side of it");
    }

    /// <summary>
    /// The clip flag is placed in time too: a card that railed on the noise and had headroom
    /// through the frame leaves the frame's row unbadged.
    /// </summary>
    /// <remarks>
    /// A block is 100 ms on a packet station and a whole qpsk3600 frame can be shorter than that,
    /// so a flag that covered its block would light on every frame heard near a burst of static.
    /// That is the same false alarm <see cref="InputLevelMeter.IsClipped"/> avoids by testing the
    /// converter's end codes exactly instead of "close to the rail".
    /// </remarks>
    [Fact]
    public async Task A_Card_That_Railed_On_The_Noise_Does_Not_Badge_The_Frame_It_Heard_Cleanly()
    {
        byte[] frame = Ax25UiFrame.Build("GB7RDG", "M0LTE", "clip check"u8.ToArray());
        float[] audio = NoiseThenFrameThenNoise(frame, railTheNoise: true);

        FrameQuality quality = await DecodeAsync(audio);

        quality.Clipped.Should().BeFalse(
            "the converter had 16 dB of headroom for every sample of this frame; the samples it "
                + "ran out of codes on were the static a second either side");
        quality.PeakDbFs!.Value.Should().BeApproximately(20 * Math.Log10(FramePeak), 1);
    }

    /// <summary>
    /// A channel nobody hands card samples to says it does not know, rather than saying no.
    /// </summary>
    /// <remarks>
    /// Which is every relayed row on a monitor, an ubersdr receiver, and any host that has not
    /// wired the tap: there is no converter of ours to have run out of codes. The level itself
    /// is still measured, because that comes off the audio the modems hear.
    /// </remarks>
    [Fact]
    public void A_Channel_With_No_Card_Tap_Reports_No_Verdict_On_Clipping()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        FrameQuality? heard = null;
        channel.FrameReceivedWithQuality += (_, _, quality) => heard ??= quality;

        byte[] frame = Ax25UiFrame.Build("GB7RDG", "M0LTE", "no card"u8.ToArray());
        float[] audio = NoiseThenFrameThenNoise(frame, railTheNoise: false);
        foreach (float[] block in Blocks(audio, SampleRate / 10))
        {
            channel.ProcessReceive(block);
        }

        heard.Should().NotBeNull("the frame decodes whatever nobody is metering");
        heard!.Value.Clipped.Should().BeNull();
        heard.Value.PeakDbFs.Should().NotBeNull("the level comes off the audio the modems hear");
    }

    /// <summary>
    /// The frame message a browser is sent carries the figure and the verdict, end to end from a
    /// real decode.
    /// </summary>
    /// <remarks>
    /// The page is tested against a hand-built event and the channel against a real one; this is
    /// the seam between them, which is where a field that is measured and then not sent would
    /// hide. The verdict is the daemon's rather than the page's, so it has to be on the wire.
    /// </remarks>
    [Fact]
    public async Task A_Frame_Message_Carries_The_Level_And_The_Daemons_Verdict()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        int port = FreePorts.Next();
        await using var server = new WaterfallWebServer(channel, port);
        server.Start();

        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), giveUp.Token);

        // The config message first, which is how this test knows the server has finished
        // registering the connection: a frame broadcast before that is a frame nobody is sent.
        using (JsonDocument config = await NextAsync(page, "config", giveUp.Token))
        {
            config.RootElement.GetProperty("type").GetString().Should().Be("config");
        }

        byte[] frame = Ax25UiFrame.Build("GB7RDG", "M0LTE", "wire check"u8.ToArray());
        float[] audio = NoiseThenFrameThenNoise(frame, railTheNoise: false, QuietFramePeak);
        foreach (float[] block in Blocks(audio, SampleRate / 10))
        {
            channel.ProcessReceive(block);
        }

        using JsonDocument message = await NextAsync(page, "frame", giveUp.Token);
        JsonElement row = message.RootElement;
        row.GetProperty("peakDbFs").GetDouble().Should().BeApproximately(
            20 * Math.Log10(QuietFramePeak), 1, "the figure the page draws is measured, not made");
        row.GetProperty("level").GetString().Should().Be(
            "quiet", "-34 dBFS is well under the band the meter tells an operator to aim for");
        row.TryGetProperty("clipped", out JsonElement clipped).Should().BeTrue();
        clipped.ValueKind.Should().Be(JsonValueKind.Null,
            "nothing handed this channel the card's own samples, so nothing can say");
    }

    /// <summary>The next text message of <paramref name="type"/>, ignoring spectrum lines.</summary>
    private static async Task<JsonDocument> NextAsync(
        ClientWebSocket page, string type, CancellationToken stopping)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            WebSocketReceiveResult received = await page.ReceiveAsync(buffer, stopping);
            if (received.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var document = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            if (document.RootElement.TryGetProperty("type", out JsonElement kind)
                && kind.GetString() == type)
            {
                return document;
            }

            document.Dispose();
        }
    }

    /// <summary>
    /// Wherever in the audio block the decode lands, the level is the frame's own.
    /// </summary>
    /// <remarks>
    /// <para>The adversarial shape, and the one that failed the first cut of this feature: a
    /// station reading 100 ms at a time (which is every station without ARDOP on it), a frame
    /// far shorter than one of those blocks, and audio 15 dB louder immediately either side of
    /// it. Nothing outside the demodulator can place the frame inside the block it was reported
    /// in, and a peak taken over a guessed window reports the tone - which is worse than the
    /// meter this feature exists to improve on, because the row presents it as a measurement of
    /// the frame.</para>
    /// <para>Swept across the block grid in eighths, because the failure was an alignment
    /// failure: the first cut was right at one phase in eight and 15.6 dB high at five of
    /// them.</para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void A_Short_Fast_Frame_Reports_Its_Own_Level_At_Every_Alignment_In_The_Block(int eighth)
    {
        // 17 bytes at 3600 bps is about 110 ms of air including its IL2P overhead, so the frame
        // is comparable to the 100 ms block and much shorter than the pair of them the old
        // window could span.
        FrameQuality quality = DecodeThroughBlocks(
            "qpsk3600", SampleRate, Ax25UiFrame.Build("GB7RDG", "M0LTE", [0x2A]), eighth);

        quality.PeakDbFs.Should().NotBeNull(
            "qpsk3600 reports the span its demodulator decoded over");
        quality.PeakDbFs!.Value.Should().BeApproximately(
            20 * Math.Log10(FramePeak),
            1,
            "the reading is the frame's own audio wherever in the block the deframer fired, and "
                + "not the tone either side of it");
    }

    /// <summary>
    /// The same for a frame far longer than a block, where the failure was the other end of the
    /// window rather than its position.
    /// </summary>
    /// <remarks>
    /// A 36-byte bpsk300 frame is nearly two seconds of air, so the first cut's window arithmetic
    /// was working as designed - and it still reported the tone on 2 of 8 alignments, because the
    /// deframer fires up to 15.8 ms after the block that held the last bit and a peak needs only
    /// one loud cell. Here the late end comes off the demodulator's own mark, less a margin that
    /// covers its front-end delay.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_Long_Slow_Frame_Reports_Its_Own_Level_At_Every_Alignment_In_The_Block(int eighth)
    {
        FrameQuality quality = DecodeThroughBlocks(
            "bpsk300",
            SampleRate,
            Ax25UiFrame.Build("GB7RDG", "M0LTE", "twenty bytes of it"u8.ToArray()),
            eighth);

        quality.PeakDbFs.Should().NotBeNull();
        quality.PeakDbFs!.Value.Should().BeApproximately(
            20 * Math.Log10(FramePeak), 1, "and the late end does not overhang into the tone");
    }

    /// <summary>
    /// Feeds a real channel in 100 ms blocks: a loud tone, the frame at a known lower level with
    /// its start pushed <paramref name="eighth"/> eighths of a block along, and the tone again.
    /// </summary>
    private static FrameQuality DecodeThroughBlocks(
        string mode, int sampleRate, byte[] frame, int eighth)
    {
        var channel = new SoundModemChannel(sampleRate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, sampleRate, sink));
        FrameQuality? heard = null;
        channel.FrameReceivedWithQuality += (_, _, quality) => heard ??= quality;

        float[] burst = Scaled(
            ModemCatalog.Create(mode, sampleRate, static _ => { })
                .Modulate(frame, txDelayMilliseconds: 300),
            FramePeak);

        int block = sampleRate / 10;
        int lead = sampleRate + (eighth * block / 8);
        var audio = new float[lead + burst.Length + sampleRate];
        for (int n = 0; n < audio.Length; n++)
        {
            // A tone rather than noise: 15 dB over the frame, well clear of every mode's own
            // band so it cannot stop the decode, and with a peak this test can name exactly.
            audio[n] = Pcm16.ToFloat(Pcm16.FromFloat(
                TonePeak * MathF.Sin(2 * MathF.PI * 5000 * n / sampleRate)));
        }

        burst.CopyTo(audio, lead);
        foreach (float[] chunk in Blocks(audio, block))
        {
            channel.ProcessReceive(chunk);
        }

        heard.Should().NotBeNull($"{mode} has to decode the frame at eighth {eighth}");
        return heard!.Value;
    }

    /// <summary>Audio scaled so its peak is exactly <paramref name="peak"/>.</summary>
    private static float[] Scaled(float[] audio, float peak)
    {
        float loudest = 0;
        foreach (float sample in audio)
        {
            loudest = Math.Max(loudest, Math.Abs(sample));
        }

        for (int n = 0; n < audio.Length; n++)
        {
            audio[n] = Pcm16.ToFloat(Pcm16.FromFloat(audio[n] / loudest * peak));
        }

        return audio;
    }

    /// <summary>
    /// Two frames of the same quiet transmission, with something far louder between them, and
    /// each reports its own level.
    /// </summary>
    /// <remarks>
    /// <para>The shape that broke the first span mechanism. A mark was never spent, so a frame
    /// that did not manage to mark a sync of its own was handed the previous frame's, and the
    /// span it got covered both frames and everything in between - which is where the tone was.
    /// The second frame decoded perfectly and reported the tone, badged TOO LOUD.</para>
    /// <para>Run over a diversity bank as well as a single modem, because three of the four
    /// banks then stored a branch's leftover span whenever they asked for one and did not get
    /// it, which is the same wrong answer arriving by the other route.</para>
    /// </remarks>
    [Theory]
    [InlineData("fsk9600-il2p")]
    [InlineData("bpsk300")]
    [InlineData("afsk1200-multi")]
    public void Two_Quiet_Frames_Astride_Something_Loud_Each_Report_Their_Own_Level(string mode)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        var channel = new SoundModemChannel(rate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, rate, sink));
        var heard = new List<FrameQuality>();
        channel.FrameReceivedWithQuality += (_, _, quality) => heard.Add(quality);

        float[] burst = Scaled(
            ModemCatalog.Create(mode, rate, static _ => { })
                .Modulate(Ax25UiFrame.Build("GB7RDG", "M0LTE", "astride"u8.ToArray()), 300),
            QuietFramePeak);

        // quiet, frame, a second of tone 33 dB louder, frame, quiet.
        int gap = rate;
        var audio = new float[(rate / 2) + burst.Length + gap + burst.Length + (rate / 2)];
        int second = (rate / 2) + burst.Length + gap;
        for (int n = 0; n < gap; n++)
        {
            audio[(rate / 2) + burst.Length + n] = Pcm16.ToFloat(Pcm16.FromFloat(
                TonePeak * MathF.Sin(2 * MathF.PI * (rate * 5 / 12) * n / rate)));
        }

        burst.CopyTo(audio, rate / 2);
        burst.CopyTo(audio, second);

        int block = rate / 10;
        foreach (float[] chunk in Blocks(audio, block))
        {
            channel.ProcessReceive(chunk);
        }

        heard.Should().HaveCountGreaterThanOrEqualTo(2, $"{mode} has to hear both frames");
        foreach (FrameQuality quality in heard)
        {
            quality.PeakDbFs.Should().NotBeNull();
            quality.PeakDbFs!.Value.Should().BeApproximately(
                20 * Math.Log10(QuietFramePeak),
                1,
                "neither frame may be measured over the tone between them");
        }
    }

    /// <summary>
    /// A link whose baseband arrives inverted decodes, and its frames still carry a level.
    /// </summary>
    /// <remarks>
    /// The IL2P deframer hunts its sync word within one bit and hunts the complemented word too,
    /// so an inverted path decodes - a property this tree relies on, and one the Dire Wolf
    /// cross-validation exercises with an inverted 9600 baseband. The watcher that stands in for
    /// that hunt matched on exact equality in the first cut, so on an inverted link every frame
    /// decoded and none of them got a level: the feature was silently absent.
    /// </remarks>
    [Theory]
    [InlineData("fsk9600-il2p")]
    [InlineData("fsk4800-il2p")]
    public void An_Inverted_Baseband_Still_Carries_A_Level(string mode)
    {
        int rate = ModemCatalog.DspRateFor(mode);
        var channel = new SoundModemChannel(rate, randomSeed: 7);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, rate, sink));
        FrameQuality? heard = null;
        channel.FrameReceivedWithQuality += (_, _, quality) => heard ??= quality;

        float[] burst = Scaled(
            ModemCatalog.Create(mode, rate, static _ => { })
                .Modulate(Ax25UiFrame.Build("GB7RDG", "M0LTE", "inverted"u8.ToArray()), 300),
            FramePeak);
        var audio = new float[(rate / 2) + burst.Length + (rate / 2)];
        for (int n = 0; n < burst.Length; n++)
        {
            audio[(rate / 2) + n] = -burst[n];
        }

        foreach (float[] chunk in Blocks(audio, rate / 10))
        {
            channel.ProcessReceive(chunk);
        }

        heard.Should().NotBeNull($"{mode} decodes an inverted baseband, which is the point");
        heard!.Value.PeakDbFs.Should().NotBeNull(
            "and the sync watcher accepts the same inversion the deframer does");
        heard.Value.PeakDbFs!.Value.Should().BeApproximately(20 * Math.Log10(FramePeak), 1);
    }

    /// <summary>
    /// Which modes can say where their frames were, and which cannot - measured by decoding one
    /// real frame per mode, because it is what CONFIG.md tells an operator to expect a level on.
    /// </summary>
    /// <remarks>
    /// Asking only whether a mode implements <see cref="IFrameSpanSource"/> pins nothing: every
    /// mode here would pass that with a <c>TryTakeFrameSpan</c> that returned false for ever,
    /// which is exactly what a sync watcher stricter than its deframer turns it into. So this
    /// modulates a frame, decodes it, and asks what came back.
    /// </remarks>
    [Fact]
    public void Every_Packet_Mode_Reports_A_Level_For_A_Real_Frame_And_The_Native_Ones_Do_Not()
    {
        var withLevel = new List<string>();
        var withoutLevel = new List<string>();
        var undecoded = new List<string>();
        foreach (string mode in ModemCatalog.KnownModes)
        {
            int rate = ModemCatalog.DspRateFor(mode);
            var channel = new SoundModemChannel(rate, randomSeed: 7);
            channel.AddModem(0, sink => ModemCatalog.Create(mode, rate, sink));
            FrameQuality? heard = null;
            channel.FrameReceivedWithQuality += (_, _, quality) => heard ??= quality;

            float[] burst = Scaled(
                ModemCatalog.Create(mode, rate, static _ => { })
                    .Modulate(Ax25UiFrame.Build("GB7RDG", "M0LTE", "one frame each"u8.ToArray()), 300),
                FramePeak);
            var audio = new float[(rate / 2) + burst.Length + (rate / 2)];
            burst.CopyTo(audio, rate / 2);
            foreach (float[] chunk in Blocks(audio, rate / 10))
            {
                channel.ProcessReceive(chunk);
            }

            (heard is null ? undecoded : heard.Value.PeakDbFs is null ? withoutLevel : withLevel)
                .Add(mode);
        }

        undecoded.Should().BeEmpty("every mode decodes its own loopback");
        withoutLevel.Should().OnlyContain(
            mode => mode.StartsWith("freedv-", StringComparison.Ordinal)
                    || mode.StartsWith("ms110d-", StringComparison.Ordinal),
            "only the two native block waveforms cannot place their own frames");
        withoutLevel.Should().NotBeEmpty("and those two are still in the catalogue");
        withLevel.Should().Contain(
            ["afsk1200", "afsk1200-fx25", "afsk1200-multi", "afsk300-il2pc", "bpsk300",
             "bpsk300-multi", "qpsk3600", "fsk9600", "fsk9600-il2p", "c4fsk19200"],
            "which is every packet family, its banks and both framings of them");
    }

    /// <summary>Runs the audio through a real station and returns the first frame's quality.</summary>
    private static async Task<FrameQuality> DecodeAsync(float[] audio)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));

        var decoded = new TaskCompletionSource<FrameQuality>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceivedWithQuality += (_, _, quality) => decoded.TrySetResult(quality);

        using var stopping = new CancellationTokenSource();
        var journal = new StationJournal("", _ => { }, _ => { });
        using var station = new DaemonStation(
            new StationOptions
            {
                Channel = channel,
                Input = new CardInput(SampleRate, audio),
                DspRate = SampleRate,
                Journal = journal,
                DeviceKind = DeadFeedDevice.Alsa,

                // The station's own wiring, as Program.cs does it: the card's own samples, before
                // any resampling, are the only place clipping is a fact.
                CardRateTap = channel.NoteCardClipping,
            },
            stopping.Token);

        Task loop = Task.Factory.StartNew(
            station.Run, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            return await decoded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await stopping.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// A second of noise, one real modulated frame at a known lower level, and a second of noise
    /// again - the shape of an FM receiver with the squelch open.
    /// </summary>
    /// <param name="railTheNoise">Drive the noise past the converter's range, as an open squelch
    /// on a badly set capture gain does, leaving the frame itself with headroom.</param>
    private static float[] NoiseThenFrameThenNoise(
        byte[] frame, bool railTheNoise, float framePeak = FramePeak)
    {
        float[] burst = Modulated(frame, framePeak);
        var audio = new float[SampleRate + burst.Length + SampleRate];
        var noise = new Random(7);
        for (int n = 0; n < audio.Length; n++)
        {
            float sample = (float)((noise.NextDouble() * 2) - 1) * (railTheNoise ? 1.6f : NoisePeak);
            // Through the conversion a card's samples arrive by, so "the converter ran out of
            // codes" means exactly what InputLevelMeter says it means.
            audio[n] = Pcm16.ToFloat(Pcm16.FromFloat(sample));
        }

        burst.CopyTo(audio, SampleRate);
        return audio;
    }

    /// <summary>The frame as audio, scaled so its peak is exactly <paramref name="framePeak"/>.</summary>
    private static float[] Modulated(byte[] frame, float framePeak)
    {
        // A transmit delay a real station would send, so the burst has a preamble in front of the
        // data exactly as an on-air one does.
        float[] burst = new Afsk1200Modem(SampleRate, _ => { })
            .Modulate(frame, txDelayMilliseconds: 300);
        float peak = 0;
        foreach (float sample in burst)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        for (int n = 0; n < burst.Length; n++)
        {
            burst[n] = Pcm16.ToFloat(Pcm16.FromFloat(burst[n] / peak * framePeak));
        }

        return burst;
    }

    private static IEnumerable<float[]> Blocks(float[] audio, int blockSamples)
    {
        for (int at = 0; at < audio.Length; at += blockSamples)
        {
            yield return audio[at..Math.Min(audio.Length, at + blockSamples)];
        }
    }

    /// <summary>A card that hands out one recording over and over, at the loop's block size.</summary>
    private sealed class CardInput(int sampleRate, float[] audio) : IAudioInput
    {
        private int _position;

        public int SampleRate { get; } = sampleRate;

        public int Read(Span<float> buffer)
        {
            // As slow as a real card, so the station's loop does not spin a core.
            Thread.Sleep(buffer.Length * 1000 / SampleRate);
            for (int n = 0; n < buffer.Length; n++)
            {
                buffer[n] = audio[_position];
                _position = (_position + 1) % audio.Length;
            }

            return buffer.Length;
        }

        public void Dispose()
        {
        }
    }
}
