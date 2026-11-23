using M0LTE.Radio.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The silence a device already has ahead of a transmission counts towards the silence a burst
/// spends its TXDELAY on, so a keyup's lead is the TXDELAY that was configured (issue #828).
/// </summary>
/// <remarks>
/// The ALSA output keeps its stream running on silence so that the card's clock never restarts,
/// and 14 to 15 ms of that silence is still ahead of a transmission when it starts on a CM108B
/// (45 to 60 ms where the card will not give any back). Written behind it, a burst that opens
/// with TXDELAY of silence of its own has that much carrier with nothing on it twice over. These
/// pin what may be left out: silence only, no more than TXDELAY, no more than the device says it
/// has, and only at the start of a keyup.
/// </remarks>
public class TransmitLeadCreditTests
{
    private const int SampleRate = 12000;

    [Fact]
    public void Leading_Silence_Is_Left_Out_Up_To_What_The_Device_Has_Ahead()
    {
        var device = new Device { Lead = TimeSpan.FromMilliseconds(15) };

        int credit = SoundModemChannel.LeadCredit(device, Burst(leadMilliseconds: 300), txDelayMilliseconds: 300);

        credit.Should().Be(Samples(15));
    }

    [Fact]
    public void No_More_Than_Txdelay_Is_Left_Out_However_Much_The_Device_Has_Ahead()
    {
        // A card that will not rewind has 60 ms ahead. A 10 ms TXDELAY cannot be met there, and
        // nothing beyond it is taken out of the burst to try.
        var device = new Device { Lead = TimeSpan.FromMilliseconds(60) };

        int credit = SoundModemChannel.LeadCredit(device, Burst(leadMilliseconds: 300), txDelayMilliseconds: 10);

        credit.Should().Be(Samples(10));
    }

    [Fact]
    public void No_More_Than_The_Bursts_Own_Leading_Silence_Is_Left_Out()
    {
        var device = new Device { Lead = TimeSpan.FromMilliseconds(60) };

        int credit = SoundModemChannel.LeadCredit(device, Burst(leadMilliseconds: 5), txDelayMilliseconds: 300);

        credit.Should().Be(Samples(5), "what follows the silence is the burst");
    }

    [Fact]
    public void A_Modulated_Preamble_Is_Not_Shortened_And_The_Device_Is_Not_Asked()
    {
        // Most modes spend TXDELAY on a preamble the far end synchronises on. That is not
        // silence and none of it is left out.
        var device = new Device { Lead = TimeSpan.FromMilliseconds(15) };

        int credit = SoundModemChannel.LeadCredit(device, Burst(leadMilliseconds: 0), txDelayMilliseconds: 300);

        credit.Should().Be(0);
        device.Promises.Should().Be(0, "a promise nobody uses is not asked for");
    }

    [Fact]
    public void An_Ofdm_Fm_Burst_Spends_Its_Txdelay_On_Silence_That_Can_Be_Left_Out()
    {
        // The mode this was done for. Its TXDELAY is whole silent symbols ahead of the burst, and
        // they have to reach the channel as exact zeros to be recognised as such.
        OfdmFmParameters profile = OfdmFmParameters.Synthetic;
        var modem = new OfdmFmModem("ofdm-fm:test", profile, _ => { });
        var device = new Device(profile.SampleRate) { Lead = TimeSpan.FromMilliseconds(14) };

        float[] burst = modem.Modulate(new byte[24], txDelayMilliseconds: 40);

        SoundModemChannel.LeadCredit(device, burst, txDelayMilliseconds: 40)
            .Should().Be(profile.SampleRate * 14 / 1000);
    }

    [Fact]
    public void A_Burst_Is_Never_Left_Out_Entirely()
    {
        // The first write is what starts the transmission on the device, and a keyup with
        // nothing written has nothing for the drain, and so PTT, to wait on.
        var device = new Device { Lead = TimeSpan.FromMilliseconds(60) };

        int credit = SoundModemChannel.LeadCredit(device, new float[100], txDelayMilliseconds: 300);

        credit.Should().Be(99);
        SoundModemChannel.LeadCredit(device, [], txDelayMilliseconds: 300).Should().Be(0);
    }

    [Fact]
    public void An_Output_That_Does_Not_Say_What_It_Has_Ahead_Is_Given_Everything()
    {
        var device = new PlainDevice();

        SoundModemChannel.LeadCredit(device, Burst(leadMilliseconds: 300), txDelayMilliseconds: 300)
            .Should().Be(0);
    }

    [Fact]
    public async Task The_First_Burst_Of_A_Keyup_Is_Written_Without_The_Silence_The_Device_Has_Ahead()
    {
        var device = new Device { Lead = TimeSpan.FromMilliseconds(15) };

        await TransmitAsync(device, txDelayMilliseconds: 300, bursts: 2);

        // The second burst of the keyup goes straight behind the first: there is no device
        // silence ahead of it, and its 30 ms of lead is written whole.
        device.Writes.Should().HaveCount(2);
        device.Writes[0].Length.Should().Be(Samples(300) + Tone - Samples(15));
        device.Writes[0].LeadingSilence.Should().Be(Samples(300) - Samples(15));
        device.Writes[1].Length.Should().Be(Samples(30) + Tone);
        device.Writes[1].LeadingSilence.Should().Be(Samples(30));
        device.Promises.Should().Be(1, "only the start of a keyup has device silence ahead of it");
        device.Events.Should().Equal("key", "promise", "write", "write", "drain", "unkey");
    }

    [Fact]
    public async Task A_Txdelay_Shorter_Than_What_Is_Ahead_Leaves_The_Burst_With_No_Lead_Of_Its_Own()
    {
        var device = new Device { Lead = TimeSpan.FromMilliseconds(15) };

        await TransmitAsync(device, txDelayMilliseconds: 10, bursts: 1);

        device.Writes.Should().ContainSingle();
        device.Writes[0].Length.Should().Be(Tone, "the tone itself is all that is left to write");
        device.Writes[0].LeadingSilence.Should().Be(0);
    }

    [Fact]
    public async Task An_Output_With_No_Lead_To_Offer_Is_Written_The_Whole_Burst_As_Before()
    {
        var device = new PlainDevice();

        await TransmitAsync(device, txDelayMilliseconds: 300, bursts: 1);

        device.Writes.Should().ContainSingle();
        device.Writes[0].Length.Should().Be(Samples(300) + Tone);
    }

    [Fact]
    public void The_Wrappers_Pass_The_Question_On_To_What_They_Wrap()
    {
        var device = new Device(48000) { Lead = TimeSpan.FromMilliseconds(13) };

        new UpsamplingAudioOutput(device, SampleRate).PromiseLead().Should().Be(TimeSpan.FromMilliseconds(13));
        new TransmitStallWatch(device, 128).PromiseLead().Should().Be(TimeSpan.FromMilliseconds(13));
        device.Promises.Should().Be(2);
    }

    [Fact]
    public void A_Wrapper_Around_An_Output_With_No_Lead_Has_None_To_Offer()
    {
        var device = new PlainDevice(48000);

        new UpsamplingAudioOutput(device, SampleRate).PromiseLead().Should().Be(TimeSpan.Zero);
        new TransmitStallWatch(device, 128).PromiseLead().Should().Be(TimeSpan.Zero);
    }

    private const int Tone = 600;

    private static int Samples(int milliseconds) => SampleRate * milliseconds / 1000;

    /// <summary>TXDELAY spent on silence and then a tone, as the CW ident, the test tone and an
    /// OFDM-FM burst render it.</summary>
    private static float[] Burst(int leadMilliseconds)
    {
        int lead = Samples(leadMilliseconds);
        var audio = new float[lead + Tone];
        audio.AsSpan(lead).Fill(0.5f);
        return audio;
    }

    /// <summary>One keyup of <paramref name="bursts"/> bursts from one source, each rendered with
    /// the TXDELAY the channel hands it.</summary>
    private static async Task TransmitAsync(PlainDevice device, int txDelayMilliseconds, int bursts)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk300MultiModem(SampleRate, sink, Afsk300Framing.Il2pCrc, 850, 5));
        channel.Csma.Persistence = 255; // never defer, so the keyup is the test's own
        channel.Csma.TxDelayMilliseconds = txDelayMilliseconds;
        channel.Csma.TxTailMilliseconds = 0;

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.TransmittingChanged += transmitting =>
        {
            if (!transmitting)
            {
                ended.TrySetResult();
            }
        };

        // Queued before the transmitter starts and from one source, so they share the keyup.
        object source = new();
        Task[] sent = Enumerable.Range(0, bursts)
            .Select(_ => channel.EnqueueTransmit(txDelay => Burst(txDelay), source: source))
            .ToArray();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task transmitter = channel.RunTransmitterAsync(device, device, cancellation.Token);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        await Task.WhenAll(sent).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A device and its PTT line, with nothing to say about what it has ahead.</summary>
    private class PlainDevice(int sampleRate = SampleRate) : IAudioOutput, IPttControl
    {
        public List<string> Events { get; } = [];

        public List<(int Length, int LeadingSilence)> Writes { get; } = [];

        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples)
        {
            Events.Add("write");
            int silence = samples.IndexOfAnyExcept(0f);
            Writes.Add((samples.Length, silence < 0 ? samples.Length : silence));
        }

        public void Drain() => Events.Add("drain");

        public void Key() => Events.Add("key");

        public void Unkey() => Events.Add("unkey");
    }

    /// <summary>A device that has silence ahead of a transmission, as the ALSA output does.</summary>
    private sealed class Device(int sampleRate = SampleRate) : PlainDevice(sampleRate), ITransmitLead
    {
        public TimeSpan Lead { get; init; }

        public int Promises { get; private set; }

        public TimeSpan PromiseLead()
        {
            Promises++;
            Events.Add("promise");
            return Lead;
        }
    }
}
