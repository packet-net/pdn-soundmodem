using System.Diagnostics;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Iq;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Iq;

/// <summary>
/// What the front end costs at 2.4 MS/s in, with one channel out and with four.
/// </summary>
/// <remarks>
/// <para>The check is on the work per input sample the receiver reports for its coarse stage
/// (<see cref="HalfbandDecimator"/> design, the same figure on every machine): a front end that
/// needs more arithmetic per sample than that is slower on every box. That runs always.</para>
/// <para>The speed is a measurement and not a check, so a separate benchmark runs only when asked
/// (<c>IQ_BENCH=1</c>) and nothing asserts on it. Run on the box in question with
/// <c>Packet.SoundModem.Tests -class Packet.SoundModem.Tests.Iq.IqReceiverCpuTests</c>; it
/// prints how many times faster than real time one core gets through the stream.</para>
/// </remarks>
public sealed class IqReceiverCpuTests(ITestOutputHelper output)
{
    private const int Rate = 2_400_000;
    private const int Seconds = 20;

    public static TheoryData<string, int, IqChannelSpec[], double> Configurations => new()
    {
        { "1 x FM fsk9600, 48 kHz DSP", 48_000, [IqChannelSpec.ForFmMode("fsk9600", 25_000)], 9.625 },
        { "1 x FM afsk1200, 12 kHz DSP", 12_000, [IqChannelSpec.ForFmMode("afsk1200", 25_000)], 9.5 },
        { "1 x USB, 12 kHz DSP", 12_000, [new IqChannelSpec { OffsetHz = 25_000, Demodulation = IqDemodulation.Usb }], 9.5 },
        {
            "4 x FM fsk9600, 48 kHz DSP, within +-50 kHz", 48_000,
            [
                IqChannelSpec.ForFmMode("fsk9600", 25_000),
                IqChannelSpec.ForFmMode("fsk9600", -25_000),
                IqChannelSpec.ForFmMode("fsk9600", 40_000),
                IqChannelSpec.ForFmMode("fsk9600", -40_000),
            ],
            9.0
        },
        {
            "4 x FM afsk1200, 12 kHz DSP, within +-50 kHz", 12_000,
            [
                IqChannelSpec.ForFmMode("afsk1200", 25_000),
                IqChannelSpec.ForFmMode("afsk1200", -25_000),
                IqChannelSpec.ForFmMode("afsk1200", 45_000),
                IqChannelSpec.ForFmMode("afsk1200", -45_000),
            ],
            9.0
        },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void The_Coarse_Stage_Does_The_Designed_Work_Per_Input_Sample(
        string name, int dspRate, IqChannelSpec[] channels, double macsPerInputSample)
    {
        var source = new LoopingIqSource(new float[2], Rate, Rate);
        using var receiver = new IqReceiver(source, channels, new IqReceiverOptions { DspRate = dspRate });

        // Fixed by the filter design, so the same on every machine: a change here is a change to
        // the front end's arithmetic, which is what this pins.
        receiver.Coarse.MacsPerInputSample.Should().BeApproximately(
            macsPerInputSample, 1e-9, $"{name}: the coarse stage's work per input sample");
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Front_End_Speed_At_2_4_Msps(
        string name, int dspRate, IqChannelSpec[] channels, double macsPerInputSample)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("IQ_BENCH") != "1",
            "set IQ_BENCH=1 to measure the front end's CPU cost");
        _ = macsPerInputSample;

        // One second of noise with a carrier in it, played round and round: the arithmetic does
        // not care what the samples are, and a fresh 20 s capture would be 384 MB.
        var second = new float[2 * Rate];
        IqSynth.AddNoise(second, 0.01, seed: 1);
        var carrier = new float[Rate];
        Array.Fill(carrier, 0.1f);
        IqSynth.AddDsb(second, carrier, Rate, 25_000);
        var source = new LoopingIqSource(second, Rate, (long)Seconds * Rate);
        using var receiver = new IqReceiver(source, channels, new IqReceiverOptions { DspRate = dspRate });

        var buffer = new float[16384];

        // Warm the JIT and the tiered compiler before the clock starts.
        for (int b = 0; b < 200; b++)
        {
            receiver.Pump();
            DrainAvailable(receiver, buffer);
        }

        source.Rewind();
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        while (receiver.Pump())
        {
            DrainAvailable(receiver, buffer);
        }

        clock.Stop();
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        double realTime = Seconds / clock.Elapsed.TotalSeconds;
        output.WriteLine(
            $"{name}: coarse {receiver.CoarseRate} S/s ({receiver.Coarse.Stages.Count} stages, "
            + $"{receiver.Coarse.MacsPerInputSample:F1} MAC/sample), {Seconds} s of IQ in "
            + $"{clock.Elapsed.TotalSeconds:F2} s wall, {cpu.TotalSeconds:F2} s CPU: "
            + $"{realTime:F1}x real time, {100.0 * clock.Elapsed.TotalSeconds / Seconds:F1} % of one core");
    }

    private static void DrainAvailable(IqReceiver receiver, float[] buffer)
    {
        for (int c = 0; c < receiver.Channels.Count; c++)
        {
            while (receiver.Channels[c].Available > 0)
            {
                receiver.Channels[c].Read(buffer);
            }
        }
    }

    /// <summary>A buffer played round and round until a total is reached.</summary>
    private sealed class LoopingIqSource(float[] loop, int rate, long totalFrames) : IIqSource
    {
        private long _served;
        private int _offset;

        public int SampleRate => rate;

        public double CentreFrequencyHz => 0;

        public long TuningGeneration => 0;

        public long SamplesLost => 0;

        public void Rewind()
        {
            _served = 0;
            _offset = 0;
        }

        public int Read(Span<float> interleaved)
        {
            long left = (totalFrames - _served) * 2;
            if (left <= 0)
            {
                return 0;
            }

            int take = (int)Math.Min(Math.Min(interleaved.Length & ~1, loop.Length - _offset), left);
            loop.AsSpan(_offset, take).CopyTo(interleaved);
            _offset = (_offset + take) % loop.Length;
            _served += take / 2;
            return take;
        }
    }
}
