using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

public class TransmitOptimizationTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private readonly FakeTimeProvider _time = new();
    private readonly BusySource _busy = new();
    private readonly SoundModemChannel _channel;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly List<byte[]> _modulated = [];
    private readonly ConcurrentQueue<byte[]> _announced = new();
    private readonly ConcurrentQueue<byte[]> _trimReports = new();
    private readonly ConcurrentQueue<byte[]> _waitReports = new();
    private Task? _transmitter;

    public TransmitOptimizationTests()
    {
        _channel = new(SampleRate, _time, randomSeed: 1, channelBusySource: _busy);
        _channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _channel.Csma.SlotTimeMilliseconds = 10;
        _channel.Csma.TxDelayMilliseconds = 20;
        _channel.Csma.TxTailMilliseconds = 0;
        _channel.TransmitTrimHz = (_, frame) =>
        {
            _modulated.Add(frame);
            return 0;
        };
        _channel.FrameTransmitted += (_, frame) => _announced.Enqueue(frame);
        _channel.FrameTransmittedWithTrim += (_, frame, _) => _trimReports.Enqueue(frame);
        _channel.FrameTransmittedWithReport += (_, frame, _) => _waitReports.Enqueue(frame);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        _stop.Dispose();
    }

    private sealed class BusySource : IChannelBusySource
    {
        public bool? Busy { get; set; } = false;
    }

    private sealed class Output(Action write) : IAudioOutput
    {
        public int SampleRate => TransmitOptimizationTests.SampleRate;
        public void Write(ReadOnlySpan<float> samples) => write();
        public void Drain() { }
    }

    private sealed class Ptt(Action key) : IPttControl
    {
        public void Key() => key();
        public void Unkey() { }
    }

    private static byte[] Frame(byte control, params byte[] info) =>
        [.. Convert.FromHexString("8E846E9EB08CE48E846EA4888E65"), control, .. info];

    private Task Send(byte control, params byte[] info) => _channel.EnqueueTransmit(0, Frame(control, info));

    private void Start(IAudioOutput? output = null, IPttControl? ptt = null) =>
        _transmitter = _channel.RunTransmitterAsync(
            output ?? new FakeAudioOutput(SampleRate), ptt ?? new NullPtt(), _stop.Token);

    private static async Task Complete(params Task[] tasks) =>
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Acknowledgements_Accumulated_While_Busy_Are_Compacted_When_The_Channel_Clears()
    {
        _busy.Busy = true;
        Task first = Send(0x01);
        Start();
        Task second = Send(0x21);
        Task third = Send(0x41);
        _modulated.Should().BeEmpty();
        new[] { first, second, third }.Should().OnlyContain(task => !task.IsCompleted);

        _busy.Busy = false;
        _time.Advance(TimeSpan.FromMilliseconds(10));
        await Complete(first, second, third);

        _modulated.Should().ContainSingle().Which.Should().Equal(Frame(0x41));
        _announced.Should().ContainSingle().Which.Should().Equal(Frame(0x41));
        _trimReports.Should().ContainSingle();
        _waitReports.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x00)]
    public async Task Identical_Data_Is_Sent_Once_Without_Reordering_Other_Data(byte control)
    {
        Task first = Send(control, 0xF0, 0x41);
        Task second = Send(control, 0xF0, 0x42);
        Task duplicate = Send(control, 0xF0, 0x41);
        Start();
        await Complete(first, second, duplicate);

        _modulated.Select(frame => frame[^1]).Should().Equal(0x41, 0x42);
        _announced.Should().HaveCount(2);
    }

    [Fact]
    public async Task Suppressed_Requests_Complete_Only_After_The_Survivor_Is_Written()
    {
        Task first = Send(0x01);
        Task second = Send(0x21);
        Task third = Send(0x41);
        int writes = 0;
        Start(new Output(() =>
        {
            new[] { first, second, third }.Should().OnlyContain(task => !task.IsCompleted);
            _announced.Should().BeEmpty();
            writes++;
        }));
        await Complete(first, second, third);
        writes.Should().Be(1);
    }

    [Theory]
    [InlineData("modulation")]
    [InlineData("ptt")]
    [InlineData("device")]
    public async Task Failure_Of_The_Survivor_Fails_Every_Original_Request(string stage)
    {
        var rejected = new ConcurrentQueue<byte[]>();
        _channel.TransmitRejected += (_, frame, _) => rejected.Enqueue(frame);
        Exception failure = stage == "modulation"
            ? new ArgumentException("refused") : new IOException("device unavailable");
        if (stage == "modulation")
        {
            _channel.TransmitTrimHz = (_, _) => throw failure;
        }

        Task[] requests = [Send(0x01), Send(0x21), Send(0x41)];
        Start(new Output(() =>
        {
            if (stage == "device") throw failure;
        }), new Ptt(() =>
        {
            if (stage == "ptt") throw failure;
        }));

        foreach (Task request in requests)
        {
            Exception? actual = await Record.ExceptionAsync(
                () => request.WaitAsync(TimeSpan.FromSeconds(10)));
            actual.Should().BeSameAs(failure);
        }

        rejected.Should().HaveCount(3);
        _announced.Should().BeEmpty();
        _trimReports.Should().BeEmpty();
        _waitReports.Should().BeEmpty();
    }

    [Fact]
    public async Task Identical_Frames_On_Different_Subchannels_Are_Both_Sent()
    {
        _channel.AddModem(1, sink => new Afsk1200Modem(SampleRate, sink));
        var ptt = new RecordingPtt();
        Task first = Send(0x01);
        Task second = _channel.EnqueueTransmit(1, Frame(0x01));
        Start(ptt: ptt);
        await Complete(first, second);
        _modulated.Should().HaveCount(2);
        ptt.Events.Should().Equal("key", "unkey", "key", "unkey");
    }

    [Fact]
    public async Task A_Delegate_Transmission_Is_Untouched_And_Separates_Packet_Runs()
    {
        int calls = 0;
        Task first = Send(0x01);
        Task opaque = _channel.EnqueueTransmit(_ =>
        {
            calls++;
            return [0.1f];
        }, source: _channel.Modems[0]);
        Task second = Send(0x21);
        Start();
        await Complete(first, opaque, second);
        _modulated.Select(frame => frame[14]).Should().Equal(0x01, 0x21);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_Frame_Already_Transmitted_Does_Not_Suppress_A_Later_Request()
    {
        Task first = Send(0x01);
        Start();
        await Complete(first);
        Task later = Send(0x01);
        await Complete(later);
        _modulated.Should().HaveCount(2);
        _announced.Should().HaveCount(2);
    }

    [Fact]
    public async Task Inhibit_Timers_Do_Not_Reject_Suppressed_Requests_After_Transmission()
    {
        bool inhibited = false;
        _channel.TransmitInhibit = () => inhibited;
        _channel.TransmitInhibitTimeout = TimeSpan.FromSeconds(1);
        var rejected = new ConcurrentQueue<byte[]>();
        _channel.TransmitRejected += (_, frame, _) => rejected.Enqueue(frame);
        Task[] requests = [Send(0x01), Send(0x21), Send(0x41)];
        Start();
        await Complete(requests);
        inhibited = true;
        _time.Advance(TimeSpan.FromSeconds(2));
        rejected.Should().BeEmpty();
    }
}
