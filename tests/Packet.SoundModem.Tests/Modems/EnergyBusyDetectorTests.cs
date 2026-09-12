using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The energy busy detector against noise and against a real burst. Everything here is driven
/// by sample counts from a seeded generator, never by the clock: one "second" is 12000 samples
/// fed as fast as the CPU can manage, so the run length is a property of the input rather than
/// of the machine the test happens to be on.
/// </summary>
public class EnergyBusyDetectorTests
{
    private const int SampleRate = 12000;

    /// <summary>The narrowest receive filter any shipped modem puts in front of this detector:
    /// <see cref="Afsk300MultiModem"/>'s per-branch band-pass, 250 Hz either side of the branch
    /// centre, 256 taps at 12 kHz. Narrow is the hard case - a block of band-limited noise has
    /// roughly 2*B*T degrees of freedom, so the narrower the filter the wider the block-power
    /// scatter and the closer noise comes to the assert threshold.</summary>
    private const double BranchHalfWidth = 250;

    private const double Centre = 1500;

    private static float[] Noise(int seed, int sampleCount)
    {
        var random = new Random(seed);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            samples[i] = 0.05f * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return samples;
    }

    private static float[] BandPass(float[] input, double halfWidth)
    {
        var filter = new FirFilter(FilterDesign.BandPass(
            Centre - halfWidth, Centre + halfWidth, SampleRate, 256));
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = filter.Next(input[i]);
        }

        return output;
    }

    private static double MeanPower(float[] samples, int from)
    {
        double sum = 0;
        for (int i = from; i < samples.Length; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        return sum / (samples.Length - from);
    }

    /// <summary>Busy duty cycle: the fraction of fed samples for which Busy was asserted. That
    /// is what the channel's p-persistent CSMA sees, because it reads the flag rather than
    /// counting blocks.</summary>
    private static double BusyDutyCycle(float[] bandLimited, EnergyBusyDetector detector)
    {
        long busy = 0;
        for (int i = 0; i < bandLimited.Length; i++)
        {
            detector.Process(bandLimited[i]);
            if (detector.Busy)
            {
                busy++;
            }
        }

        return (double)busy / bandLimited.Length;
    }

    /// <summary>
    /// Pure noise is not a busy channel. Two minutes of it per case, through the narrowest
    /// filter that feeds this detector in production and through a narrower one again as a
    /// stress case, must not raise busy at all.
    /// </summary>
    /// <remarks>
    /// This is the regression for the false-busy defect: the floor estimator adapts down about
    /// 100x faster than it adapts up, so it settles below the mean of the noise it tracks, and
    /// with a short enough integration block ordinary noise excursions clear the 6 dB assert.
    /// At the 20 ms block this shipped with, these same inputs read busy for 0.9 to 1.6 % of the
    /// run at 250 Hz half-width and 5.9 to 7.3 % at 200 Hz, which is spurious transmit deferral
    /// on every station running it. The inputs are seeded, so a cell that reads zero here reads
    /// zero every time; there is nothing statistical about the assertion.
    /// </remarks>
    [Theory]
    [InlineData(1, BranchHalfWidth)]
    [InlineData(2, BranchHalfWidth)]
    [InlineData(3, BranchHalfWidth)]
    [InlineData(1, 200)]
    [InlineData(2, 200)]
    public void Pure_Noise_Never_Reads_Busy(int seed, double halfWidth)
    {
        float[] band = BandPass(Noise(seed, SampleRate * 120), halfWidth);

        double duty = BusyDutyCycle(band, new EnergyBusyDetector(SampleRate));

        duty.Should().Be(0);
    }

    /// <summary>
    /// The other half of the same fix: it must not have been bought by making the detector deaf.
    /// A real afsk300 burst from the shipped modulator, buried in the same noise, still asserts
    /// busy promptly and holds it for essentially the whole burst.
    /// </summary>
    /// <remarks>
    /// The SNRs are in-band, measured through the same 500 Hz filter. 3 dB is the bottom of the
    /// detector's designed range rather than a taste threshold: a signal equal to the noise adds
    /// only 3 dB to the band power, and the detector asserts on 6 dB, so an in-band SNR of 0 dB
    /// is below the threshold by construction and always was. Measured on this burst, assert
    /// happens 80 ms in at 3 dB and 40 ms in from 8 dB up.
    /// </remarks>
    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void Asserts_On_A_Real_Burst_In_Noise(double snrDb)
    {
        var modem = new Afsk300Modem(
            SampleRate, static _ => { }, centerFrequency: Centre, bandPassHalfWidth: BranchHalfWidth);
        var frame = new byte[48];
        new Random(4).NextBytes(frame);
        float[] burst = modem.Modulate(frame, txDelayMilliseconds: 300);

        float[] noise = Noise(21, SampleRate * 20);
        double noisePower = MeanPower(BandPass(noise, BranchHalfWidth), SampleRate);
        double burstPower = MeanPower(BandPass(burst, BranchHalfWidth), 0);
        double scale = Math.Sqrt(noisePower * Math.Pow(10, snrDb / 10) / burstPower);

        int start = SampleRate * 5;
        for (int i = 0; i < burst.Length && start + i < noise.Length; i++)
        {
            noise[start + i] += (float)(burst[i] * scale);
        }

        float[] band = BandPass(noise, BranchHalfWidth);
        var detector = new EnergyBusyDetector(SampleRate);
        int assertedAt = -1;
        long busyDuringBurst = 0;
        long busyBeforeBurst = 0;
        for (int i = 0; i < band.Length; i++)
        {
            detector.Process(band[i]);
            if (i < start)
            {
                busyBeforeBurst += detector.Busy ? 1 : 0;
            }
            else if (i < start + burst.Length)
            {
                busyDuringBurst += detector.Busy ? 1 : 0;
                if (detector.Busy && assertedAt < 0)
                {
                    assertedAt = i;
                }
            }
        }

        busyBeforeBurst.Should().Be(0, "the five seconds of noise ahead of the burst are not a busy channel");
        assertedAt.Should().BeGreaterThanOrEqualTo(0, "the burst must raise busy");
        double latencyMs = (assertedAt - start) * 1000.0 / SampleRate;
        latencyMs.Should().BeLessThanOrEqualTo(100, "carrier sense has to see the far end inside a 100 ms CSMA slot");
        ((double)busyDuringBurst / burst.Length).Should().BeGreaterThanOrEqualTo(0.95);
    }

    /// <summary>
    /// c4fsk is the one modem that keeps the 20 ms block, because Busy gates its bit path and
    /// the gate has to be open before a NinoTNC peer's sync word. It can afford to: its receive
    /// filter passes 1.5x the symbol rate, so a 20 ms block there holds far more time-bandwidth
    /// product than a 500 Hz branch filter does at 40 ms, and pure noise stays quiet anyway.
    /// </summary>
    [Fact]
    public void C4fsk_Keeps_The_Short_Block_And_Still_Reads_Quiet_On_Noise()
    {
        const int rate = 48000;
        var modem = new C4fskModem(rate, static _ => { }, symbolRate: 4800, crc: false);
        float[] noise = Noise(5, rate * 30);

        long busy = 0;
        for (int i = 0; i < noise.Length; i += 4800)
        {
            int length = Math.Min(4800, noise.Length - i);
            modem.Process(noise.AsSpan(i, length));
            busy += modem.ChannelBusy ? length : 0;
        }

        busy.Should().Be(0);
    }

    /// <summary>
    /// The hold is a minimum, so a block length that does not divide it rounds up rather than
    /// down. At the 40 ms default a requested 100 ms hold is three blocks, 120 ms; truncating
    /// division would have made it two, and quietly shortened the hold that rides through PSK
    /// envelope dips and flutter.
    /// </summary>
    [Fact]
    public void Busy_Holds_For_At_Least_The_Requested_Hold_After_A_Burst_Stops()
    {
        float[] noise = Noise(31, SampleRate * 10);
        double noisePower = MeanPower(BandPass(noise, BranchHalfWidth), SampleRate);
        double amplitude = Math.Sqrt(2 * noisePower * Math.Pow(10, 20.0 / 10));
        int start = SampleRate * 3;
        int end = start + SampleRate;
        for (int i = start; i < end; i++)
        {
            noise[i] += (float)(amplitude * Math.Sin(2 * Math.PI * Centre * i / SampleRate));
        }

        float[] band = BandPass(noise, BranchHalfWidth);
        var detector = new EnergyBusyDetector(SampleRate);
        int releasedAt = -1;
        for (int i = 0; i < band.Length; i++)
        {
            bool was = detector.Busy;
            detector.Process(band[i]);
            if (was && !detector.Busy && i > end && releasedAt < 0)
            {
                releasedAt = i;
            }
        }

        releasedAt.Should().BeGreaterThanOrEqualTo(0, "busy must release once the burst has gone");
        double heldMs = (releasedAt - end) * 1000.0 / SampleRate;
        heldMs.Should().BeGreaterThanOrEqualTo(100);
    }
}
