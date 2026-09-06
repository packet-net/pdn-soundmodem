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
/// order, and how much audio has gone past by the time a deframer fires.</para>
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
    /// Every mode in the catalogue can be added to a channel: the airtime probe either measures
    /// the mode or declines it, and never takes a station down on the way up.
    /// </summary>
    /// <remarks>
    /// The probe modulates two throwaway frames of different lengths and takes the difference
    /// (<c>FrameLevelMonitor.AddModem</c>), which is one more thing asked of every modem at
    /// start-up - and a mode that refused the long one by throwing something the probe did not
    /// expect would stop the daemon before it opened the sound card. The sweep is cheap insurance
    /// against exactly that, and it is the same sweep <c>ModemBandProbe</c> would want.
    /// </remarks>
    [Fact]
    public void Every_Mode_In_The_Catalogue_Survives_Being_Asked_How_Long_A_Frame_Takes()
    {
        foreach (string mode in ModemCatalog.KnownModes)
        {
            int rate = ModemCatalog.DspRateFor(mode);
            var channel = new SoundModemChannel(rate, randomSeed: 7);
            Action adding = () => channel.AddModem(0, sink => ModemCatalog.Create(mode, rate, sink));
            adding.Should().NotThrow($"{mode} has to be addable to a station");
        }
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

                // 20 ms, which is what a station with ARDOP on it reads, and short enough that
                // the block the deframer fires in is a small part of a frame. See
                // FrameLevelMonitor.Measure: the reading loses the block's own length off the
                // near end of the window, because nothing knows where in a block a decode
                // happened.
                BlockMilliseconds = 20,

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
