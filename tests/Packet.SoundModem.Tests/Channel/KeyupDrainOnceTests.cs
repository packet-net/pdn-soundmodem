using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The frames of one keyup are one contiguous sample stream on the card: written back to back,
/// drained once, after the tail, with PTT released after that.
/// </summary>
/// <remarks>
/// The transmitter used to drain the device after every frame. A drain waits for everything
/// written to play, stops the stream and re-arms it, and ALSA pads the last period with silence
/// on the way, so the next frame was modulated and written into a stopped card: 30 to 35 ms of
/// carrier-up silence between every frame of a keyup and the next, whatever the modem, measured
/// on a raw receive capture (radio2, 2026-09-19 17:36 UTC). The one drain a keyup needs is the
/// one PTT release waits on.
/// </remarks>
public class KeyupDrainOnceTests
{
    private const int SampleRate = 12000;

    /// <summary>The device and the PTT line in one ordered record, so a test can say what
    /// happened before what.</summary>
    private sealed class KeyupRecorder(int sampleRate) : IAudioOutput, IPttControl
    {
        public List<string> Events { get; } = [];

        public List<int> WriteLengths { get; } = [];

        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            Events.Add("write");
            WriteLengths.Add(samples.Length);
        }

        public void Drain() => Events.Add("drain");

        public void Key() => Events.Add("key");

        public void Unkey() => Events.Add("unkey");
    }

    private static byte[] Frame(byte marker)
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        frame.AsSpan(16).Fill(marker);
        return frame;
    }

    private static SoundModemChannel Station(int txTailMilliseconds)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850, 5));
        channel.Csma.Persistence = 255; // never defer, so the keyup is the test's own
        channel.Csma.TxTailMilliseconds = txTailMilliseconds;
        return channel;
    }

    /// <summary>
    /// Queues every frame before the transmitter starts, so they are certainly all waiting when
    /// it keys and share the one keyup. Waits for the channel to say the transmission is over
    /// rather than for the frames' own tasks: those complete when the audio is handed to the
    /// card, which is before the tail, the drain and the unkey this is here to see.
    /// </summary>
    private static async Task<KeyupRecorder> TransmitAsync(SoundModemChannel channel, params byte[][] frames)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.TransmittingChanged += transmitting =>
        {
            if (!transmitting)
            {
                ended.TrySetResult();
            }
        };

        Task[] completions = frames.Select(frame => channel.EnqueueTransmit(0, frame)).ToArray();
        var recorder = new KeyupRecorder(SampleRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(recorder, recorder, cancellation.Token);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        return recorder;
    }

    [Fact]
    public async Task Three_Frames_In_One_Keyup_Are_Written_Back_To_Back_And_Drained_Once_After_The_Tail()
    {
        KeyupRecorder recorder = await TransmitAsync(Station(txTailMilliseconds: 20), Frame(0x41), Frame(0x42), Frame(0x43));

        recorder.Events.Should().Equal(
            ["key", "write", "write", "write", "write", "drain", "unkey"],
            "three frames and the tail are four writes with nothing drained between them; the "
            + "keyup's one drain follows the tail and is what the unkey waits on");
        recorder.WriteLengths[^1].Should().Be(
            SampleRate * 20 / 1000, "the last write is the tail, still sent and still the length configured");
    }

    [Fact]
    public async Task One_Frame_Still_Drains_Once_After_The_Tail()
    {
        KeyupRecorder recorder = await TransmitAsync(Station(txTailMilliseconds: 20), Frame(0x41));

        recorder.Events.Should().Equal(
            ["key", "write", "write", "drain", "unkey"],
            "a single-frame keyup is the frame, the tail, one drain, and the unkey after it");
    }

    [Fact]
    public async Task With_No_Tail_The_Drain_Is_Still_The_Last_Thing_Before_The_Unkey()
    {
        KeyupRecorder recorder = await TransmitAsync(Station(txTailMilliseconds: 0), Frame(0x41), Frame(0x42));

        recorder.Events.Should().Equal(
            ["key", "write", "write", "drain", "unkey"],
            "PTT is released only once the card has played everything, tail or no tail");
    }
}
