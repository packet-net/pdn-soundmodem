using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmit trim: moving a burst off the nominal centre so it lands where an off-frequency
/// station's receiver is actually listening.
/// </summary>
/// <remarks>
/// <para>Proven by decoding rather than by inspecting samples. The claim is not "the audio is
/// different" but "the burst is now centred somewhere else, by the amount asked for, in the
/// right direction" - and the only witness that cares about all three is a demodulator placed
/// there.</para>
/// <para>It lives on the channel rather than in a modem because the AFSK and PSK families carry
/// a settable centre natively and are never wrapped in a frequency shifter. Putting the trim in
/// the wrapper would have reached only the spec-fixed modes, which are not the ones talking to
/// the stations this exists for.</para>
/// </remarks>
public class TransmitTrimTests
{
    private const int SampleRate = 12000;
    private const double NativeCentre = 1500;

    private static byte[] SampleFrame()
    {
        byte[] frame = new byte[30];
        byte[] header =
            [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(1).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static async Task<float[]> TransmitWithTrimAsync(double trimHz)
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new BpskModem(SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.TransmitTrimHz = (_sub, _frame) => trimHz;

        var output = new FakeAudioOutput(SampleRate);
        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);

        await channel.EnqueueTransmit(0, SampleFrame()).WaitAsync(TimeSpan.FromSeconds(8));

        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        return output.Snapshot();
    }

    /// <summary>
    /// Energy in a 120 Hz window about <paramref name="centreHz"/>, by direct DFT.
    /// </summary>
    /// <remarks>
    /// <para>Measured rather than decoded, because a decode proves less than it looks: bpsk300 runs a
    /// carrier-offset estimator and pulled a burst in from 200 Hz away in an earlier draft of
    /// this test, which would have let a trim that went the wrong way pass. Where the energy
    /// actually sits cannot be rescued by a clever receiver.
    /// </para>
    /// <para>The window is narrow enough that two of them 200 Hz apart do not overlap. At
    /// +/-125 Hz they did, and a burst that had plainly moved still only showed twice the energy
    /// at its new home as at its old one.</para>
    /// </remarks>
    private static double BandEnergy(float[] audio, double centreHz)
    {
        // A bounded window over the loudest part of the burst, not the whole buffer: the buffer
        // is seconds of mostly silence, and a Goertzel run over that many samples loses its
        // footing numerically long before it says anything about where the energy is.
        const int window = 8192;
        int peak = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            if (Math.Abs(audio[i]) > Math.Abs(audio[peak]))
            {
                peak = i;
            }
        }

        int start = Math.Clamp(peak - (window / 2), 0, Math.Max(0, audio.Length - window));
        int count = Math.Min(window, audio.Length - start);

        double total = 0;
        for (double f = centreHz - 60; f <= centreHz + 60; f += 20)
        {
            double w = 2 * Math.PI * f / SampleRate;
            double re = 0, im = 0;
            for (int i = 0; i < count; i++)
            {
                double phase = w * i;
                re += audio[start + i] * Math.Cos(phase);
                im += audio[start + i] * Math.Sin(phase);
            }

            total += ((re * re) + (im * im)) / count;
        }

        return total;
    }

    private static int DecodeCount(float[] audio, double receiverCentreHz)
    {
        var received = new List<byte[]>();
        IModem receiver = ModemCatalog.Create(
            "bpsk300", SampleRate, received.Add,
            new ModemOptions(CentreFrequencyHz: receiverCentreHz));

        receiver.Process(new float[SampleRate / 10]);
        receiver.Process(audio);
        receiver.Process(new float[SampleRate]);
        return received.Count;
    }

    [Fact]
    public async Task An_untrimmed_burst_decodes_on_the_nominal_centre()
    {
        float[] audio = await TransmitWithTrimAsync(0);
        DecodeCount(audio, NativeCentre).Should().Be(1);
    }

    [Fact]
    public async Task A_trimmed_burst_decodes_where_it_was_aimed_and_not_where_it_was_not()
    {
        // 300 Hz: more than the burst's own occupied width, so its new position and its old one
        // do not share skirts and the comparison below is unambiguous. At 200 Hz the burst had
        // plainly moved (3.7x the energy at its new home) but the two still overlapped enough to
        // make any threshold a judgement call.
        const double trim = 300;
        float[] audio = await TransmitWithTrimAsync(trim);

        // Still decodable, because the receiver's own offset estimator is generous - which is
        // exactly why the energy, not the decode, is the thing to assert on.
        DecodeCount(audio, NativeCentre + trim).Should()
            .Be(1, "the burst was aimed {0} Hz high and that is where it should be", trim);

        BandEnergy(audio, NativeCentre + trim).Should()
            .BeGreaterThan(
                BandEnergy(audio, NativeCentre) * 4,
                "the burst has moved off the nominal centre, not merely become decodable elsewhere");
    }

    [Fact]
    public async Task The_trim_goes_the_way_it_is_told()
    {
        // Sign matters: correcting for a station heard high means transmitting high. Getting
        // this backwards would double the error instead of removing it, and would look fine in
        // any test that only measured magnitude.
        float[] audio = await TransmitWithTrimAsync(-200);

        BandEnergy(audio, NativeCentre - 200).Should().BeGreaterThan(
            BandEnergy(audio, NativeCentre + 200) * 4,
            "a negative trim must move the burst down, not up");
    }

    [Fact]
    public async Task The_applied_trim_is_announced_with_the_frame()
    {
        // The panel and the frame log both need this, and both need it to be what actually went
        // out rather than what was asked for - otherwise a clamped trim would be recorded as a
        // shift that never happened.
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new BpskModem(SampleRate, sink));
        channel.Csma.Persistence = 255;
        channel.TransmitTrimHz = (_sub, _frame) => 12.5;

        var announced = new List<double>();
        channel.FrameTransmittedWithTrim += (_sub, _frame, trim) => announced.Add(trim);

        var output = new FakeAudioOutput(SampleRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task transmitter = channel.RunTransmitterAsync(output, new RecordingPtt(), cancellation.Token);
        await channel.EnqueueTransmit(0, SampleFrame()).WaitAsync(TimeSpan.FromSeconds(8));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        announced.Should().ContainSingle().Which.Should().Be(12.5);
    }

    [Fact]
    public async Task An_untrimmed_frame_announces_zero_rather_than_nothing()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 42);
        channel.AddModem(0, sink => new BpskModem(SampleRate, sink));
        channel.Csma.Persistence = 255;

        var announced = new List<double>();
        channel.FrameTransmittedWithTrim += (_sub, _frame, trim) => announced.Add(trim);

        var output = new FakeAudioOutput(SampleRate);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task transmitter = channel.RunTransmitterAsync(output, new RecordingPtt(), cancellation.Token);
        await channel.EnqueueTransmit(0, SampleFrame()).WaitAsync(TimeSpan.FromSeconds(8));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        announced.Should().ContainSingle().Which.Should().Be(0);
    }

    [Fact]
    public async Task An_absurd_trim_is_clamped_rather_than_transmitted()
    {
        // A backstop on the caller. Without it a runaway estimate would walk the burst off the
        // end of the passband, and a transmitter is the wrong place to discover that.
        float[] audio = await TransmitWithTrimAsync(50_000);
        audio.Should().NotBeEmpty();
        DecodeCount(audio, NativeCentre + SoundModemChannel.MaxTransmitTrimHz).Should().Be(1);
    }
}
