using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Kiss;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Kiss;

/// <summary>
/// A polyglot port end to end (#450): two modems overlaid on one channel behind one nibble-0
/// port, with what the host sends leaving in the mode its next hop was heard in.
///
/// As in <see cref="KissDedicatedPortTests"/>, the two modems are different framings at the same
/// baud - AX.25 HDLC on sub-channel 0, IL2P on sub-channel 1 - so which one sent a frame is
/// decided by decoding the audio, not assumed from the code path.
/// </summary>
public class KissPolyglotPortTests : IAsyncLifetime
{
    private const int SampleRate = 12000;
    private const int Hdlc = 0;   // afsk1200, the default
    private const int Il2p = 1;   // afsk1200-il2p

    private readonly SoundModemChannel _channel;
    private readonly FakeAudioOutput _output = new(SampleRate);
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _transmitter;
    private KissTcpServer _polyglot = null!;

    public KissPolyglotPortTests()
    {
        _channel = new SoundModemChannel(SampleRate, randomSeed: 7);
        _channel.AddModem(Hdlc, sink => ModemCatalog.Create("afsk1200", SampleRate, sink));
        _channel.AddModem(Il2p, sink => ModemCatalog.Create("afsk1200-il2p", SampleRate, sink));
        _channel.Csma.Persistence = 255;
        _channel.Csma.TxDelayMilliseconds = 20;
    }

    public ValueTask InitializeAsync()
    {
        var router = new PolyglotRouter([Hdlc, Il2p], Hdlc, TimeSpan.FromMinutes(60));
        _polyglot = new KissTcpServer(_channel, router, port: 0);
        _polyglot.Start();
        _transmitter = _channel.RunTransmitterAsync(_output, new NullPtt(), _cancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        try
        {
            await (_transmitter ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        await _polyglot.DisposeAsync();
        _cancellation.Dispose();
    }

    /// <summary>Audio carrying <paramref name="frame"/> as sent by <paramref name="mode"/>.</summary>
    private static async Task<float[]> ModulateAsync(string mode, byte[] frame)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 3);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.Csma.TxDelayMilliseconds = 20;
        var output = new FakeAudioOutput(SampleRate);
        using var cancellation = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(output, new NullPtt(), cancellation.Token);

        // Complete once the frame's audio has been written: the trailing silence added below
        // stands in for whatever tail the transmitter is still writing.
        await channel.EnqueueTransmit(0, frame).Within("the reference burst to be modulated");
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        return [.. output.Snapshot(), .. new float[SampleRate / 2]];
    }

    [Fact]
    public async Task Every_Overlaid_Modems_Frames_Reach_The_Host_As_Nibble_Zero()
    {
        byte[] legacy = PolyglotRouterTests.Frame("M0LTE", "G4OLD");
        byte[] fancy = PolyglotRouterTests.Frame("M0LTE", "G4NEW");
        float[] audio = [.. await ModulateAsync("afsk1200", legacy), .. await ModulateAsync("afsk1200-il2p", fancy)];
        using KissTestClient host = await KissTestClient.ConnectAsync(_polyglot);

        // Both bursts are decoded and queued to the session before this returns, so the fence
        // collects both. The session had to be registered first: one that the server accepted
        // part-way through this call was offered the second frame and not the first.
        _channel.ProcessReceive(audio);

        List<KissFrame> received = await host.FenceAsync();
        received.Should().HaveCount(2);
        received.Should().OnlyContain(f => f.Port == 0, "the host sees one port and one channel");
        received.Select(f => f.Payload).Should().BeEquivalentTo([legacy, fancy], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_Quality_Frame_Follows_Its_Data_Frame_As_Nibble_Zero()
    {
        _polyglot.EmitQualityFrames = true;
        byte[] frame = PolyglotRouterTests.Frame("M0LTE", "G4NEW");
        float[] audio = await ModulateAsync("afsk1200-il2p", frame);
        using KissTestClient host = await KissTestClient.ConnectAsync(_polyglot);

        _channel.ProcessReceive(audio);

        List<KissFrame> received = await host.FenceAsync();
        received.Select(f => (f.Port, f.Command)).Should().Equal(
            (0, KissCommand.Data), (0, KissCommand.RxQuality));
        System.Text.Encoding.UTF8.GetString(received[1].Payload).Should().Contain("afsk1200-il2p");
    }

    [Fact]
    public async Task A_Reply_To_A_Station_Heard_In_Il2p_Goes_Out_In_Il2p()
    {
        _channel.ProcessReceive(await ModulateAsync("afsk1200-il2p", PolyglotRouterTests.Frame("M0LTE", "G4NEW")));
        byte[] reply = PolyglotRouterTests.Frame("G4NEW", "M0LTE");
        using KissTestClient host = await KissTestClient.ConnectAsync(_polyglot);
        Task<byte[]> transmitted = KissTestWait.NextTransmissionAsync(_channel);

        await host.SendAsync(new KissFrame(0, KissCommand.Data, reply));
        await transmitted.Within("the reply to be transmitted");

        float[] audio = [.. _output.Snapshot(), .. new float[SampleRate / 2]];
        Decode("afsk1200-il2p", audio).Should().ContainSingle().Which.Should().Equal(reply);
        Decode("afsk1200", audio).Should().BeEmpty("G4NEW was heard in IL2P, so it gets IL2P");
    }

    [Fact]
    public async Task A_Frame_For_A_Station_Never_Heard_Goes_Out_In_The_Default()
    {
        _channel.ProcessReceive(await ModulateAsync("afsk1200-il2p", PolyglotRouterTests.Frame("M0LTE", "G4NEW")));
        byte[] frame = PolyglotRouterTests.Frame("G4OLD", "M0LTE");
        using KissTestClient host = await KissTestClient.ConnectAsync(_polyglot);
        Task<byte[]> transmitted = KissTestWait.NextTransmissionAsync(_channel);

        await host.SendAsync(new KissFrame(0, KissCommand.Data, frame));
        await transmitted.Within("the frame to be transmitted");

        float[] audio = [.. _output.Snapshot(), .. new float[SampleRate / 2]];
        Decode("afsk1200", audio).Should().ContainSingle().Which.Should().Equal(frame);
        Decode("afsk1200-il2p", audio).Should().BeEmpty();
    }

    [Fact]
    public async Task An_Ackmode_Frame_Is_Routed_And_Acked_As_Nibble_Zero()
    {
        _channel.ProcessReceive(await ModulateAsync("afsk1200-il2p", PolyglotRouterTests.Frame("M0LTE", "G4NEW")));
        byte[] reply = PolyglotRouterTests.Frame("G4NEW", "M0LTE");
        using KissTestClient host = await KissTestClient.ConnectAsync(_polyglot);

        await host.SendAsync(new KissFrame(0, KissCommand.AckModeData, [0x12, 0x34, .. reply]));
        await host.ReadUntilAsync(frames => frames.Count > 0, "the ack");

        // The host is attached after the burst above, so the only thing it can read is the ack;
        // the fence shows nothing else was queued behind it.
        (await host.FenceAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new KissFrame(0, KissCommand.AckModeData, [0x12, 0x34]));
        float[] audio = [.. _output.Snapshot(), .. new float[SampleRate / 2]];
        Decode("afsk1200-il2p", audio).Should().ContainSingle().Which.Should().Equal(reply);
    }

    private static List<byte[]> Decode(string mode, float[] audio)
    {
        var received = new List<byte[]>();
        var channel = new SoundModemChannel(SampleRate);
        channel.AddModem(0, sink => ModemCatalog.Create(mode, SampleRate, sink));
        channel.FrameReceived += (_, decoded) => received.Add(decoded);
        channel.ProcessReceive(audio);
        return received;
    }
}
