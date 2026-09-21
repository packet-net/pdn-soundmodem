using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The two defects issue #518 found in the C4FSK receive path on a real FM bench, neither of
/// which a clean loopback or a simulated channel can reach.
/// </summary>
public class C4fskOnAirTests
{
    private const int Rate = 48000;

    /// <summary>
    /// The envelope tracker's runaway. <c>TrackEnvelope</c> moves whichever outer peak the
    /// decision names toward the reading it was decided from, and had nothing stopping that
    /// reading being on the far side of the midpoint: once <c>_peakHigh</c> crosses
    /// <c>_peakLow</c> the half-swing clamps to its floor, every normalised value rails, every
    /// decision goes outer, and the burst is dead from there on. Measured on a real off-air
    /// burst with the gate forced open, the reported peak went +0.23 -> +0.08 -> -3.1 -> -43 ->
    /// -33173 about four seconds in. A baseline that wanders far enough reproduces it without a
    /// recording, which is what this drives.
    /// </summary>
    [Fact]
    public void The_Outer_Envelope_Never_Inverts_However_Far_The_Baseline_Wanders()
    {
        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
        {
            foreach (double step in new double[] { 6, 12, 20 })
            {
                (float smallestSwing, float largestSwing) = SwingExtremes(mode, step);

                smallestSwing.Should().BeGreaterThan(
                    0,
                    $"{mode} across a {step:0} dB step in receiver noise must not let the upper "
                    + "outer peak cross the lower one, whatever the decisions say");
                largestSwing.Should().BeLessThan(
                    10,
                    $"{mode} across a {step:0} dB step in receiver noise must not let the envelope "
                    + "run away upward either; the audio itself never exceeds full scale");
            }
        }
    }

    /// <summary>
    /// A transmission that makes the receiver QUIETER still reaches the bit path.
    /// </summary>
    /// <remarks>
    /// <para>Issue #518's first fault, and the one that accounted for every symptom. This modem
    /// gates its bit path on a signal-present test, for a good reason: on silence the slicer
    /// saturates to the outer levels and the Mode-2 sync word is 18 ones in 24 bits, so the
    /// deframer false-locks continuously between bursts. That test used to be an in-band ENERGY
    /// detector, which asserts when the level RISES - and an FM receiver with the squelch open
    /// goes quiet when a carrier arrives. Over the NinoTNC reference capture, 45 s holding 15 real
    /// C4FSK 19k2 transmissions, the gate opened <b>zero</b> times.</para>
    /// <para>These drive the polarity the old gate could not survive: an idle channel from 10 dB
    /// below the burst to 20 dB above it, which is the measured range on the bench as the far
    /// end's transmit level is wound down. Delivery is asserted down to -10 dB; the two -20 dB
    /// cases still fail on payload CRC rather than on the gate and stay on the aspiration
    /// scoreboard with the measurement that says why.</para>
    /// </remarks>
    [Theory]
    [InlineData("c4fsk9600", 10)]
    [InlineData("c4fsk9600", 0)]
    [InlineData("c4fsk9600", -1)]
    [InlineData("c4fsk9600", -10)]
    [InlineData("c4fsk19200", 10)]
    [InlineData("c4fsk19200", 0)]
    [InlineData("c4fsk19200", -1)]
    [InlineData("c4fsk19200", -10)]
    public void A_Burst_That_Quiets_The_Receiver_Is_Still_Decoded(string mode, double relativeDb)
    {
        int delivered = 0;
        for (int seed = 1; seed <= 4; seed++)
        {
            delivered += C4fskFmReceiverAspirationTests.Delivered(mode, relativeDb, seed) ? 1 : 0;
        }

        delivered.Should().Be(
            4,
            "{0} must hear a burst that sits {1:0} dB against the idle channel's own level, in "
            + "either direction: a gate's job is to say whether there is a signal, not whether "
            + "the channel got louder",
            mode,
            relativeDb);
    }

    /// <summary>The smallest and largest outer swing the tracker holds at any phase-0 decision    /// <summary>The smallest and largest outer swing the tracker holds at any phase-0 decision
    /// over audio built to reproduce the bench case: a stretch of receiver noise at one level
    /// followed by a big step up in that noise, which is exactly what the end of an FM
    /// transmission looks like to this gate and is where it opens on the real recording.</summary>
    private static (float Smallest, float Largest) SwingExtremes(string mode, double step)
    {
        float[] audio = NoiseStep(step, 11);
        float smallest = float.MaxValue;
        float largest = 0;
        C4fskModem modem = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(Rate, _ => { })
            : C4fskModem.C4fsk19200(Rate, _ => { });
        modem.DecisionObserver = d =>
        {
            if (d.Phase != 0)
            {
                return;
            }

            float swing = d.PeakHigh - d.PeakLow;
            smallest = Math.Min(smallest, swing);
            largest = Math.Max(largest, Math.Abs(swing));
        };
        WanderRig.Feed(audio, modem.Process);
        return (smallest, largest);
    }

    /// <summary>Half a second of noise, then three seconds of the same noise
    /// <paramref name="stepDb"/> louder.</summary>
    private static float[] NoiseStep(double stepDb, int seed)
    {
        var random = new Random(seed);
        var audio = new float[Rate * 7 / 2];
        int step = Rate / 2;
        float quiet = 0.02f;
        float loud = (float)(quiet * Math.Pow(10, stepDb / 20));
        for (int i = 0; i < audio.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double gaussian = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            audio[i] = (float)(gaussian * (i < step ? quiet : loud));
        }

        return audio;
    }
    /// <summary>
    /// And the property the gate exists for, which the fix must not spend: an idle channel
    /// produces no frames. On silence the slicer saturates to the outer levels and the Mode-2
    /// sync is 18 ones in 24, so an ungated deframer false-locks continuously (about 12k
    /// near-sync hits in one recording's silence). Both kinds of idle are driven: digital
    /// silence, and the open-squelch hiss that the gate now has to sit quietly through for
    /// minutes at a time.
    /// </summary>
    [Theory]
    [InlineData("c4fsk9600")]
    [InlineData("c4fsk19200")]
    public void An_Idle_Channel_Produces_No_Frames(string mode)
    {
        int symbolRate = mode == "c4fsk19200" ? 9600 : 4800;
        foreach ((string what, float[] audio) in new (string, float[])[]
                 {
                     ("digital silence", new float[Rate * 30]),
                     ("open-squelch hiss", Hiss(symbolRate, 0.08f, Rate * 30, 5)),
                     ("broadband noise", Noise(0.08f, Rate * 30, 6)),
                 })
        {
            var frames = new List<byte[]>();
            C4fskModem modem = mode == "c4fsk9600"
                ? C4fskModem.C4fsk9600(Rate, frames.Add)
                : C4fskModem.C4fsk19200(Rate, frames.Add);
            WanderRig.Feed(audio, modem.Process);
            frames.Should().BeEmpty($"{mode} must read nothing out of 30 s of {what}");
        }
    }

    /// <summary>Receiver noise with its power rising toward the top of the modem's own receive
    /// band: white noise through four first-order high-pass sections at the symbol rate. An FM
    /// discriminator's noise really does rise with frequency, which is why a transmission can be
    /// quieter in total than the idle channel and still perfectly readable, and it is why the
    /// level the gate measures says nothing on its own about whether a signal is there.</summary>
    private static float[] Hiss(int symbolRate, float level, int samples, int seed)
    {
        float[] audio = Noise(level, samples, seed);
        for (int section = 0; section < 4; section++)
        {
            var pole = new OnePole(symbolRate, Rate);
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] = pole.Next(audio[i]);
            }
        }

        return audio;
    }

    private static float[] Noise(float level, int samples, int seed)
    {
        var random = new Random(seed);
        var audio = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            audio[i] = (float)(level * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        return audio;
    }
}

/// <summary>
/// What this receiver has to do on an FM path and does not do yet. Category
/// <c>Aspiration</c>: excluded from the blocking run and executed in a non-blocking CI step, so
/// it is a visible scoreboard rather than a broken build (the discipline is in
/// <see cref="NinoTncAspirationTests"/>: when one of these passes it graduates into
/// <see cref="C4fskOnAirTests"/> as a regression guard, and it is never weakened to make it
/// pass).
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> C4fskModem is the only modem where <c>EnergyBusyDetector.Busy</c> is
/// a hard gate on the bit path, and that detector asserts 6 dB above a tracked noise floor. An FM
/// receiver with the squelch open, which is what a data station runs, is LOUDER when idle than
/// when a carrier arrives: the carrier captures the discriminator and replaces open-squelch noise
/// with the modulation. Measured through the modem's own receive filter, a full-level c4fsk9600
/// transmission reads -18.1 dBFS against -17.0 dBFS of idle hiss, falling to -36.1 dBFS as the
/// transmit level is wound down, and on a 45 s capture holding 15 real NinoTNC transmissions the
/// gate opens zero times. That is the whole of "zero frames at every size in both directions" on
/// the Tait bench, and it is why fsk9600 delivers over the same path: it has no such gate.</para>
/// <para><b>Why it is not fixed here.</b> Level cannot do the job in either direction. On that
/// capture the transmissions sit 2.9 dB below the idle median at c4fsk19200 and 1.9 dB below it
/// at c4fsk9600, and the idle channel's own block-to-block scatter covers that completely: the
/// quietest idle block is below the loudest keyed block. The replacement has to be a ratio, or
/// come from the radio, and the tree already holds most of one -
/// <c>OfdmFm/FmQuietingBusyDetector</c>, <c>IChannelBusySource</c>, <c>TaitCarrierSense</c> and
/// <c>docs/dev/ofdm-fm/carrier-sense.md</c>. Promoting that stack to a station-wide service is
/// issue #522 and is being taken first, in its own context; C4FSK's use of it is #518 and comes
/// after. These tests are here because #522 will want them: nothing in the suite modelled an
/// open-squelch FM receiver before, which is why a defect this complete lived this long.</para>
/// </remarks>
[Trait("Category", "Aspiration")]
public class C4fskFmReceiverAspirationTests
{
    private const int Rate = 48000;

    /// <summary>
    /// The energy gate's polarity assumption. <c>Busy</c> asserts 6 dB above a tracked noise
    /// floor, and until issue #518 it was a hard gate on the bit path, so the modem could only
    /// hear a burst that made the channel LOUDER. An FM receiver with the squelch open does the
    /// opposite: the carrier quiets the receiver, so the transmission arrives 1 to 20 dB below
    /// the idle hiss (measured on the bench at -17.0 dBFS idle against -18.1 dBFS for a
    /// full-level transmission, falling to -36.1 dBFS as the transmit level was wound down).
    /// On a 100 s recording holding 29 s of transmissions the gate opened for 5.8 % of the file,
    /// and at the wrong moments: it asserted when a transmission STOPPED and the hiss came back.
    /// Both polarities have to work, so both are driven here.
    /// </summary>
    /// <remarks>
    /// <b>What is left of this, and it is no longer the gate.</b> Every level from +10 down to
    /// -10 dB now delivers and has moved to <see cref="C4fskOnAirTests"/>, which blocks. At -20 dB
    /// the gate still opens on time, the packet carrier detect still asserts, and the sync word
    /// still matches in 6 of the 7 timing phases - the probe shows the two cases as very nearly
    /// the same file - and the payload still fails its CRC. So what fails here is the eye, which
    /// is issue #518's faults 2 and 3 and not its fault 1. The decisions give it away: 38 % outer
    /// low and 39 % outer high against 11 % and 12 % inner, which is a two-level eye wearing a
    /// four-level mode's clothes.
    /// </remarks>
    [Theory]
    [InlineData("c4fsk9600", -20)]
    [InlineData("c4fsk19200", -20)]
    public void A_Burst_Is_Heard_Whether_It_Raises_Or_Lowers_The_Channel_Level(string mode, double relativeDb)
    {
        int delivered = 0;
        for (int seed = 1; seed <= 4; seed++)
        {
            delivered += Delivers(mode, relativeDb, seed) ? 1 : 0;
        }

        delivered.Should().Be(
            4,
            $"{mode} must hear a burst that sits {relativeDb:0} dB against the idle channel's own "
            + "level, in either direction: the gate's job is to say whether there is a signal, "
            + "not whether the channel got louder");
    }

    /// <summary>
    /// Acceptance against a real recording, which is what a synthetic model cannot stand in for.
    /// Point <c>C4FSK_ONAIR_WAV</c> at an off-air capture and <c>C4FSK_ONAIR_BURSTS</c> at how
    /// many transmissions it holds, with <c>C4FSK_ONAIR_MODE</c> and an optional
    /// <c>C4FSK_ONAIR_MAX_OPEN</c> ceiling on the open fraction for a file whose idle stretches
    /// are known. Run against <c>ninorx.wav</c> (45 s, 15 NinoTNC c4fsk19200 transmissions of
    /// about 140 ms every 1.2 s) the gate opens 15 times and is open 8.2 % of the file, against a
    /// keyed fraction of 4.7 % plus the detector's own 100 ms hold on each burst, and the energy
    /// detector contributes nothing at all - which is the defect this covers. Against
    /// <c>fresh0db.wav</c> (8.2 s of our own transmitter, essentially one continuous burst) it
    /// opens and stays open for 94.9 % of the file, so that recording carries no ceiling.
    /// </summary>
    [Fact]
    public void The_Gate_Opens_On_Every_Transmission_In_A_Recording()
    {
        string? path = Environment.GetEnvironmentVariable("C4FSK_ONAIR_WAV");
        Assert.SkipWhen(
            path is null || !File.Exists(path)
            || !int.TryParse(Environment.GetEnvironmentVariable("C4FSK_ONAIR_BURSTS"), out _),
            "set C4FSK_ONAIR_WAV and C4FSK_ONAIR_BURSTS to a recording and its transmission count");

        int expected = int.Parse(Environment.GetEnvironmentVariable("C4FSK_ONAIR_BURSTS")!);
        (float[] audio, int rate) = Packet.SoundModem.Audio.WavFile.ReadMono(path!);
        string mode = Environment.GetEnvironmentVariable("C4FSK_ONAIR_MODE") ?? "c4fsk19200";
        C4fskModem modem = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(rate, _ => { })
            : C4fskModem.C4fsk19200(rate, _ => { });

        int openings = 0;
        int openBlocks = 0;
        int blocks = 0;
        bool last = false;
        int block = rate / 500;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            modem.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
            blocks++;
            bool open = modem.ChannelBusy;
            openings += open && !last ? 1 : 0;
            openBlocks += open ? 1 : 0;
            last = open;
        }

        double openFraction = openBlocks / (double)blocks;
        double ceiling = double.TryParse(
            Environment.GetEnvironmentVariable("C4FSK_ONAIR_MAX_OPEN"), out double max) ? max : 1.0;
        openings.Should().BeGreaterThanOrEqualTo(
            expected,
            $"{path} holds {expected} transmissions and the bit path has to run for each of them "
            + $"(it opened {openings} time(s) and was open {100 * openFraction:0.0} % of the file)");
        openFraction.Should().BeLessThanOrEqualTo(
            ceiling,
            "the gate must close again between transmissions rather than sit open across the "
            + "file; set C4FSK_ONAIR_MAX_OPEN for a recording whose idle stretches are known");
    }

    /// <summary>One burst against an idle channel of its own, at a stated level difference as
    /// the modem's own front end sees it.</summary>
    /// <summary>The audio a case is built from, so a bench probe can be pointed at it.</summary>
    internal static float[] AudioFor(string mode, double relativeDb, int seed)
    {
        Audio(mode, relativeDb, seed, out float[] made);
        return made;
    }

    /// <summary>Whether one case delivers its frame; shared with the blocking half of this
    /// pair in <see cref="C4fskOnAirTests"/>.</summary>
    internal static bool Delivered(string mode, double relativeDb, int seed) =>
        Delivers(mode, relativeDb, seed);

    private static bool Delivers(string mode, double relativeDb, int seed)
    {
        int symbolRate = mode == "c4fsk19200" ? 9600 : 4800;
        byte[] frame = WanderRig.Frame(80, seed);
        bool decoded = false;
        C4fskModem modem = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(Rate, f => decoded |= f.AsSpan().SequenceEqual(frame))
            : C4fskModem.C4fsk19200(Rate, f => decoded |= f.AsSpan().SequenceEqual(frame));

        float[] burst = WanderRig.Make(mode, _ => { }).Modulate(frame, 300);
        int lead = Rate;
        var audio = new float[lead + burst.Length + (Rate / 2)];
        float[] hiss = Hiss(symbolRate, 1f, audio.Length, seed + 900);

        // Levels are measured through the modem's own receive filter, because that is what the
        // gate compares. The idle channel sits relativeDb under the burst; while the burst is
        // on the air the receiver quiets, and a solidly quieting FM receiver leaves the hiss far
        // under the signal (the bench measured the band above the signal falling 31 dB when a
        // transmission started). Fixing that at 30 dB is what keeps this a test of the gate: the
        // only thing the parameter moves is how loud the channel is when nothing is being sent.
        double burstPower = Power(burst, 1.5 * symbolRate);
        double hissPower = Power(hiss, 1.5 * symbolRate);
        float idleScale = (float)Math.Sqrt(burstPower / hissPower / Math.Pow(10, relativeDb / 10));
        float quietedScale = Math.Min(idleScale, (float)Math.Sqrt(burstPower / hissPower / 1000.0));
        for (int i = 0; i < audio.Length; i++)
        {
            bool inBurst = i >= lead && i - lead < burst.Length;
            audio[i] = (inBurst ? burst[i - lead] : 0f)
                + (hiss[i] * (inBurst ? quietedScale : idleScale));
        }

        WanderRig.Feed(audio, modem.Process);
        return decoded;
    }

    /// <summary>The channel model above, without a modem attached.</summary>
    private static void Audio(string mode, double relativeDb, int seed, out float[] made)
    {
        int symbolRate = mode == "c4fsk19200" ? 9600 : 4800;
        byte[] frame = WanderRig.Frame(80, seed);
        float[] burst = WanderRig.Make(mode, _ => { }).Modulate(frame, 300);
        int lead = Rate;
        var audio = new float[lead + burst.Length + (Rate / 2)];
        float[] hiss = Hiss(symbolRate, 1f, audio.Length, seed + 900);
        double burstPower = Power(burst, 1.5 * symbolRate);
        double hissPower = Power(hiss, 1.5 * symbolRate);
        float idleScale = (float)Math.Sqrt(burstPower / hissPower / Math.Pow(10, relativeDb / 10));
        float quietedScale = Math.Min(idleScale, (float)Math.Sqrt(burstPower / hissPower / 1000.0));
        for (int i = 0; i < audio.Length; i++)
        {
            bool inBurst = i >= lead && i - lead < burst.Length;
            audio[i] = (inBurst ? burst[i - lead] : 0f)
                + (hiss[i] * (inBurst ? quietedScale : idleScale));
        }

        made = audio;
    }

    /// <summary>Mean power of the audio below <paramref name="cutoffHz"/>.</summary>
    private static double Power(float[] audio, double cutoffHz)
    {
        var filter = new M0LTE.Dsp.FirFilter(M0LTE.Dsp.FilterDesign.LowPass(cutoffHz, Rate, 96));
        double total = 0;
        foreach (float sample in audio)
        {
            float filtered = filter.Next(sample);
            total += filtered * (double)filtered;
        }

        return total / audio.Length;
    }

    /// <summary>Receiver noise with its power rising toward the top of the modem's own receive
    /// band: white noise through four first-order high-pass sections at the symbol rate. An FM
    /// discriminator's noise really does rise with frequency, which is why a transmission can be
    /// quieter in total than the idle channel and still perfectly readable, and it is why the
    /// level the gate measures says nothing on its own about whether a signal is there.</summary>
    private static float[] Hiss(int symbolRate, float level, int samples, int seed)
    {
        float[] audio = Noise(level, samples, seed);
        for (int section = 0; section < 4; section++)
        {
            var pole = new OnePole(symbolRate, Rate);
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] = pole.Next(audio[i]);
            }
        }

        return audio;
    }

    private static float[] Noise(float level, int samples, int seed)
    {
        var random = new Random(seed);
        var audio = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            audio[i] = (float)(level * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        return audio;
    }
}

/// <summary>
/// Bench probe for issue #518, gated on a recording this repository does not carry: point
/// <c>C4FSK_ONAIR_WAV</c> at an off-air capture (radio1's own 48 kHz <c>rawCapture</c> will do)
/// and this reports what the receive path makes of it - how much of the file the energy gate
/// calls busy, where the block levels sit against the tracked floor, and what the envelope
/// tracker does once the gate is open. The gate and the envelope are the two things that were
/// wrong on the real bench and that no simulated channel reaches, so they are what it reads.
/// </summary>
public class C4fskOnAirRecordingProbe(ITestOutputHelper output)
{
    [Fact]
    public void What_The_Receive_Path_Makes_Of_A_Real_Recording()
    {
        string? path = Environment.GetEnvironmentVariable("C4FSK_ONAIR_WAV");
        Assert.SkipWhen(path is null || !File.Exists(path), "set C4FSK_ONAIR_WAV to a recording");

        (float[] audio, int rate) = Packet.SoundModem.Audio.WavFile.ReadMono(path!);
        output.WriteLine($"{path}: {audio.Length} samples at {rate} Hz, {audio.Length / (double)rate:0.0} s");

        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
        {
            var frames = new List<int>();
            C4fskModem modem = mode == "c4fsk9600"
                ? C4fskModem.C4fsk9600(rate, f => frames.Add(f.Length))
                : C4fskModem.C4fsk19200(rate, f => frames.Add(f.Length));
            float smallest = float.MaxValue;
            float largest = 0;
            long decisions = 0;
            long inverted = 0;
            modem.DecisionObserver = d =>
            {
                if (d.Phase != 0)
                {
                    return;
                }

                decisions++;
                float swing = d.PeakHigh - d.PeakLow;
                inverted += swing <= 0 ? 1 : 0;
                smallest = Math.Min(smallest, swing);
                largest = Math.Max(largest, Math.Abs(swing));
            };

            int block = rate / 50;
            int busyBlocks = 0;
            int blocks = 0;
            for (int pos = 0; pos < audio.Length; pos += block)
            {
                modem.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
                blocks++;
                busyBlocks += modem.ChannelBusy ? 1 : 0;
            }

            output.WriteLine(
                $"{mode}: frames {frames.Count}, channel busy {busyBlocks}/{blocks} blocks "
                + $"({100.0 * busyBlocks / blocks:0.0} %), decisions {decisions}, "
                + $"envelope swing smallest {smallest:0.0000} largest {largest:0.0000}, "
                + $"inverted at {inverted} decisions");
        }
    }
}
