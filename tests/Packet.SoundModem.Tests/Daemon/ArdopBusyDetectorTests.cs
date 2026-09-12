using AwesomeAssertions;
using M0LTE.Ardop;
using M0LTE.Dsp;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Telling the ARDOP TNC whether somebody else is using its slot. The package implements no
/// detector and takes a seam instead, so the question here is whether this daemon answers that
/// seam about the right band: the ARDOP modem's own slot and not the neighbours either side of
/// it, because a detector that heard the packet slots would hold ARDOP off the air whenever the
/// station it shares a radio with was working.
/// </summary>
/// <remarks>
/// These test the band selection, which is what this class decides. Whether the meter behind it
/// reads a given power as busy is <see cref="Modems.EnergyBusyDetector"/>'s own business and is
/// tested there; asserting it here too would couple these to its thresholds and break them the
/// next time those move.
/// </remarks>
public class ArdopBusyDetectorTests
{
    private const int Rate = ArdopModulator.SampleRate;
    private const double ArdopCentre = 1500;

    /// <summary>GB7RDG's permanent neighbours, in audio Hz at its dial.</summary>
    private const double Afsk300Centre = 850;
    private const double Bpsk300Centre = 2150;

    private static float[] Tone(double hz, double seconds, double amplitude = 0.25)
    {
        var audio = new float[(int)(Rate * seconds)];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        }

        return audio;
    }

    /// <summary>A quiet channel: seeded so a failure is reproducible, not a different run.</summary>
    private static float[] Noise(double seconds, int seed = 20260912, double amplitude = 0.01)
    {
        var rng = new Random(seed);
        var audio = new float[(int)(Rate * seconds)];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = (float)(amplitude * ((rng.NextDouble() * 2) - 1));
        }

        return audio;
    }

    private static bool EverBusy(ArdopBusyDetector detector, ReadOnlySpan<float> audio)
    {
        const int Block = 240;
        for (int i = 0; i < audio.Length; i += Block)
        {
            detector.Process(audio.Slice(i, Math.Min(Block, audio.Length - i)));
            if (detector.Busy)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Power in dB of one tone after the detector's own band, relative to unfiltered.</summary>
    private static double AttenuationDb(ArdopBusyDetector detector, double toneHz)
    {
        var filter = new FirFilter(FilterDesign.BandPass(detector.LowHz, detector.HighHz, Rate, 257));
        float[] tone = Tone(toneHz, 1.0);

        double passed = 0;
        double raw = 0;

        // Skip the filter's warm-up: its history starts zeroed, so the first taps' worth of
        // output is artificially quiet and would flatter the attenuation.
        for (int i = 0; i < tone.Length; i++)
        {
            float y = filter.Next(tone[i]);
            if (i < 2048)
            {
                continue;
            }

            passed += (double)y * y;
            raw += (double)tone[i] * tone[i];
        }

        return 10 * Math.Log10(passed / raw);
    }

    [Theory]
    [InlineData(200, 1297.5, 1702.5)]
    [InlineData(500, 1211.0, 1789.0)]
    [InlineData(1000, 1009.0, 1991.0)]
    [InlineData(2000, 470.0, 2530.0)]
    public void The_watched_band_is_the_measured_emission_padded_by_the_tuning_range(
        double bandwidthHz, double expectedLow, double expectedHigh)
    {
        // The half-widths are measured (99 % OBW over the widest frame each class emits), not
        // taken from the class name, because the two disagree: every ConReq measures 190 to
        // 199 Hz whether it announces 200 or 2000. The padding is ardopcf's 100 Hz TuningRange,
        // because a station up to that far off frequency is still one we would work, and a
        // detector watching only our own emission would transmit over them.
        var detector = new ArdopBusyDetector(ArdopCentre, bandwidthHz, Rate);

        detector.LowHz.Should().BeApproximately(expectedLow, 0.5);
        detector.HighHz.Should().BeApproximately(expectedHigh, 0.5);
    }

    [Fact]
    public void A_real_ardop_frame_in_the_slot_reads_busy()
    {
        var detector = new ArdopBusyDetector(ArdopCentre, 500, Rate);

        // Settle the floor on a quiet channel first, as it would be on air. Digital silence
        // would not do: the floor clamps at 1e-12 and then anything at all looks infinitely
        // loud, which is a property of the meter and not of the band.
        detector.Process(Noise(2));

        short[] frame = new ArdopModulator().Modulate(
            ArdopFrameCodec.EncodeControl(ArdopFrameType.ConReq500M, sessionId: 0xFF));
        var audio = new float[frame.Length];
        for (int i = 0; i < frame.Length; i++)
        {
            audio[i] = frame[i] / 32768f;
        }

        EverBusy(detector, audio).Should().BeTrue();
    }

    [Theory]
    [InlineData(Afsk300Centre)]
    [InlineData(Bpsk300Centre)]
    public void A_neighbouring_packet_slot_is_rejected_by_the_band(double neighbourCentreHz)
    {
        var detector = new ArdopBusyDetector(ArdopCentre, 500, Rate);

        // 40 dB is the floor this asserts, not the figure measured: on the real waveforms
        // (2026-09-12) afsk300-il2pc at 850 Hz lands 84 dB down in this band and bpsk300 at
        // 2150 Hz lands 50 dB down, so both would need more SNR than an HF path offers to raise
        // it by the 6 dB the meter needs. A pure tone is the conservative stand-in, since all of
        // its power sits on one frequency where a real 340 Hz-wide signal spreads.
        AttenuationDb(detector, neighbourCentreHz).Should().BeLessThan(-40);
    }

    [Fact]
    public void The_ardop_slot_itself_passes_the_band()
    {
        var detector = new ArdopBusyDetector(ArdopCentre, 500, Rate);

        AttenuationDb(detector, ArdopCentre).Should().BeGreaterThan(-1);
    }
}
