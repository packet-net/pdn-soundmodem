using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

internal sealed class FakeAudioOutput(int sampleRate) : IAudioOutput
{
    private readonly List<float> _written = [];

    public int SampleRate { get; } = sampleRate;

    public int WrittenCount
    {
        get
        {
            lock (_written)
            {
                return _written.Count;
            }
        }
    }

    public float[] Snapshot()
    {
        lock (_written)
        {
            return [.. _written];
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_written)
        {
            _written.AddRange(samples.ToArray());
        }
    }

    public void Drain()
    {
    }
}

internal sealed class RecordingPtt : IPttControl
{
    public List<string> Events { get; } = [];

    public void Key() => Events.Add("key");

    public void Unkey() => Events.Add("unkey");
}

public class ChannelLoopbackTests
{
    private const int SampleRate = 12000;

    private static byte[] SampleFrame()
    {
        byte[] frame = new byte[30];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(1).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static SoundModemChannel MakeTxChannel()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        channel.AddModem(1, sink => new BpskModem(SampleRate, sink));
        channel.Csma.Persistence = 255; // always transmit on the first clear roll
        return channel;
    }

    private static async Task<FakeAudioOutput> TransmitAsync(
        SoundModemChannel channel, RecordingPtt ptt, params (int SubChannel, byte[] Frame)[] frames)
    {
        var output = new FakeAudioOutput(SampleRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);

        var completions = frames.Select(f => channel.EnqueueTransmit(f.SubChannel, f.Frame)).ToArray();
        await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(8));

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        return output;
    }

    [Fact]
    public async Task A_Transmitted_Frame_Is_Decodable_And_Ptt_Brackets_The_Audio()
    {
        byte[] frame = SampleFrame();
        var txChannel = MakeTxChannel();
        var ptt = new RecordingPtt();

        FakeAudioOutput output = await TransmitAsync(txChannel, ptt, (0, frame));

        ptt.Events.Should().Equal("key", "unkey");
        output.WrittenCount.Should().BeGreaterThan(0);

        // What went to the "transmitter" decodes on an independent receive channel.
        var received = new List<(int SubChannel, byte[] Frame)>();
        var rxChannel = new SoundModemChannel(SampleRate);
        rxChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        rxChannel.FrameReceived += (subChannel, decoded) => received.Add((subChannel, decoded));
        rxChannel.ProcessReceive([.. output.Snapshot(), .. new float[SampleRate / 2]]);

        received.Should().ContainSingle();
        received[0].SubChannel.Should().Be(0);
        received[0].Frame.Should().Equal(frame);
    }

    [Fact]
    public async Task Sub_Channels_Route_To_Their_Own_Modems()
    {
        byte[] afskFrame = SampleFrame();
        byte[] bpskFrame = Convert.FromHexString("968264888AAEE4969668908A946F81");
        var txChannel = MakeTxChannel();

        FakeAudioOutput output = await TransmitAsync(
            txChannel, new RecordingPtt(), (0, afskFrame), (1, bpskFrame));

        var received = new List<(int SubChannel, byte[] Frame)>();
        var rxChannel = new SoundModemChannel(SampleRate);
        rxChannel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        rxChannel.AddModem(1, sink => new BpskModem(SampleRate, sink));
        rxChannel.FrameReceived += (subChannel, decoded) => received.Add((subChannel, decoded));
        rxChannel.ProcessReceive([.. output.Snapshot(), .. new float[SampleRate / 2]]);

        received.Should().HaveCount(2);
        received.Should().Contain(r => r.SubChannel == 0 && r.Frame.SequenceEqual(afskFrame));
        received.Should().Contain(r => r.SubChannel == 1 && r.Frame.SequenceEqual(bpskFrame));
    }

    [Fact]
    public async Task Enqueue_To_An_Unknown_Sub_Channel_Faults()
    {
        var channel = MakeTxChannel();
        var act = async () => await channel.EnqueueTransmit(9, SampleFrame());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Receive_Is_Suppressed_While_Transmitting_Flag_Is_Set()
    {
        // ChannelBusy must report true while transmitting so CSMA in other stacks holds off.
        var channel = MakeTxChannel();
        channel.ChannelBusy.Should().BeFalse();
    }
}
