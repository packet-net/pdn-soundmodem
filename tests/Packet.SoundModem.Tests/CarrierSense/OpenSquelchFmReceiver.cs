using M0LTE.Dsp;

namespace Packet.SoundModem.Tests.CarrierSense;

/// <summary>
/// Audio as it comes out of an FM receiver with the squelch open, which is how every packet FM
/// station runs: <b>louder when nobody is transmitting than when somebody is</b>.
/// </summary>
/// <remarks>
/// <para><b>Why this fixture exists at all.</b> Every other audio fixture in this repository puts
/// the noise BELOW the signal. The loopback tests have digital silence between bursts; the AWGN
/// and Watterson ladders inject their lead-in noise at the burst's own signal-to-noise ratio, so
/// the burst is always the loud part. That is right for SSB and for a wired loop and it is
/// backwards for FM, and it is the reason an inverted assumption about carrier sense survived
/// every test in the suite and was only found on air (packet-net/pdn-soundmodem#522). A detector
/// cannot be scored against a channel model that cannot express the failure.</para>
/// <para><b>The physics.</b> With no carrier, an FM discriminator's output is noise filling the
/// whole detection bandwidth. An arriving carrier captures the discriminator and replaces that
/// noise with the modulation, so the received level FALLS, and it falls hardest well above the
/// signal's own band where the modulation puts nothing back.</para>
/// <para><b>The numbers are measured, on radio1, 2026-09-21</b>, from a 48 kHz capture of a
/// NinoTNC transmitting C4FSK 19k2 against a real idle channel
/// (<c>/home/tf/fm-carrier-sense-evidence/README.md</c>). In the energy detector's own 20 ms
/// blocks, through the c4fsk19200 receive filter:</para>
/// <list type="table">
/// <item><term>idle, in band</term><description>-16.0 dBFS</description></item>
/// <item><term>keyed, in band</term><description>-18.9 dBFS, so <b>2.9 dB below idle</b></description></item>
/// </list>
/// <para>and the hiss above the signal's band collapses much harder than the signal band moves,
/// which is the one thing the audio has going for it: the in-band-to-hiss ratio reads 21.5 dB idle
/// against 36.7 dB keyed.</para>
/// <para><b>The hiss figures here are a ratio, not an absolute.</b> The in-band levels were
/// measured directly and are trustworthy as levels; the hiss band's own level is set from that
/// measured RATIO, because an absolute figure for it was never taken. That distinction matters:
/// two nominally identical stations on this bench measured 22 dB apart in absolute terms, which is
/// precisely why no detector may key a decision off an absolute dBFS threshold.</para>
/// <para><b>The spectral shape above the band is this rig's, not FM theory's.</b> Textbook FM
/// detection noise RISES with frequency; what this station measures is a receive path that rolls
/// off above about 10 kHz, so the hiss band sits well below the signal band even when idle.
/// Anything relying on the shape rather than on the collapse should be measured on a second
/// station before it is believed.</para>
/// </remarks>
internal static class OpenSquelchFmReceiver
{
    /// <summary>The rate a station's sound card captures at, and what the recordings are.</summary>
    public const int SampleRate = 48000;

    /// <summary>Top of the signal's own band: 1.5 x the 9600 symbols a second of c4fsk19200,
    /// which is the widest thing this bench transmits.</summary>
    public const double SignalTopHz = 14400;

    /// <summary>The hiss band, above anything the signal occupies.</summary>
    public const double HissFromHz = 16000;

    /// <summary>Upper edge of the hiss band, short of Nyquist and of the capture path's own
    /// anti-alias corner.</summary>
    public const double HissToHz = 23500;

    /// <summary>In-band level with nobody transmitting.</summary>
    public const double IdleInBandDbfs = -16.0;

    /// <summary>In-band level with a far end transmitting. Below <see cref="IdleInBandDbfs"/>,
    /// which is the whole point.</summary>
    public const double KeyedInBandDbfs = -18.9;

    /// <summary>How far the hiss band sits below the signal band with nobody transmitting.</summary>
    public const double IdleInBandOverHissDb = 21.5;

    /// <summary>The same ratio with a far end transmitting: the hiss has collapsed and the ratio
    /// has opened up by 15.2 dB.</summary>
    public const double KeyedInBandOverHissDb = 36.7;

    private const int Taps = 257;

    /// <summary>Open-squelch idle hiss: the LOUD state.</summary>
    public static float[] Idle(double seconds, int seed) =>
        Render(seconds, seed, IdleInBandDbfs, IdleInBandDbfs - IdleInBandOverHissDb);

    /// <summary>A far end transmitting: quieter in band, and much quieter above it.</summary>
    public static float[] Keyed(double seconds, int seed) =>
        Render(seconds, seed, KeyedInBandDbfs, KeyedInBandDbfs - KeyedInBandOverHissDb);

    /// <summary>
    /// Idle, then a transmission, then idle again: what a station hears across somebody else's
    /// whole over.
    /// </summary>
    /// <remarks>
    /// The three stretches are rendered independently and concatenated, so the keyup and the
    /// unkey are instantaneous. A real one is not, and a detector's assert latency has to be
    /// measured against a real recording rather than against this; what this is for is the
    /// STEADY-STATE question of which way round the levels are, which is what every other fixture
    /// in the suite gets wrong.
    /// </remarks>
    public static float[] IdleThenKeyedThenIdle(
        double idleSeconds, double keyedSeconds, int seed)
    {
        float[] before = Idle(idleSeconds, seed);
        float[] during = Keyed(keyedSeconds, seed + 1);
        float[] after = Idle(idleSeconds, seed + 2);
        var all = new float[before.Length + during.Length + after.Length];
        before.CopyTo(all, 0);
        during.CopyTo(all, before.Length);
        after.CopyTo(all, before.Length + during.Length);
        return all;
    }

    /// <summary>The sample index a <see cref="IdleThenKeyedThenIdle"/> transmission starts at.</summary>
    public static int KeyupSample(double idleSeconds) => (int)(idleSeconds * SampleRate);

    /// <summary>Band power in dBFS over a span, for a test to check its own fixture with.</summary>
    public static double BandPowerDbfs(
        ReadOnlySpan<float> samples, double fromHz, double toHz, int from, int to)
    {
        var filter = new FirFilter(FilterDesign.BandPass(fromHz, toHz, SampleRate, Taps));
        double sum = 0;
        int counted = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float y = filter.Next(samples[i]);
            if (i < from || i >= to)
            {
                continue;
            }

            sum += (double)y * y;
            counted++;
        }

        return 10 * Math.Log10(Math.Max(sum / Math.Max(counted, 1), 1e-20));
    }

    private static float[] Render(double seconds, int seed, double inBandDbfs, double hissDbfs)
    {
        int count = (int)(seconds * SampleRate);
        // Two independent noises rather than one shaped one: the signal band and the hiss band
        // move independently here, which is the fixture's whole subject.
        float[] band = Filtered(White(count, seed), FilterDesign.LowPass(SignalTopHz, SampleRate, Taps));
        float[] hiss = Filtered(
            White(count, seed + 7919), FilterDesign.BandPass(HissFromHz, HissToHz, SampleRate, Taps));

        // Scaled after filtering, from what the filtered noise actually measured, so a change of
        // filter order cannot quietly move the level the fixture claims to produce.
        Scale(band, inBandDbfs);
        Scale(hiss, hissDbfs);

        var mixed = new float[count];
        for (int i = 0; i < count; i++)
        {
            mixed[i] = band[i] + hiss[i];
        }

        return mixed;
    }

    private static float[] White(int count, int seed)
    {
        var random = new Random(seed);
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            samples[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return samples;
    }

    private static float[] Filtered(float[] input, float[] taps)
    {
        var filter = new FirFilter(taps);
        var output = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = filter.Next(input[i]);
        }

        return output;
    }

    private static void Scale(float[] samples, double targetDbfs)
    {
        // The filter's own transient is excluded from the measurement but not from the output:
        // a station's audio does not start at a filter's cold history, and leaving the ramp in
        // and measuring past it is the closer model of both.
        double sum = 0;
        for (int i = Taps; i < samples.Length; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        double rms = Math.Sqrt(sum / Math.Max(samples.Length - Taps, 1));
        float gain = (float)(Math.Pow(10, targetDbfs / 20.0) / Math.Max(rms, 1e-12));
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }
}
