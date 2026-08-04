using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Ota;

/// <summary>One point of a §E2 ladder: a burst to transmit through a stated channel at a stated SNR.</summary>
/// <param name="WaveformNumber">MS110D waveform.</param>
/// <param name="Interleaver">Interleaver option.</param>
/// <param name="SnrDb">SNR the rig is asked to deliver, in the 3 kHz reference bandwidth.</param>
/// <param name="Channel">Channel geometry - see <see cref="LadderChannel"/>.</param>
/// <param name="Seed">Payload seed; also derives the channel realisation, so a point is
/// reproducible from the manifest alone.</param>
/// <param name="Blocks">Interleaver blocks in the burst.</param>
public sealed record LadderPoint(
    int WaveformNumber,
    Ms110dInterleaverKind Interleaver,
    double SnrDb,
    LadderChannel Channel,
    int Seed,
    int Blocks);

/// <summary>Channel geometries the ladder can inject - the two the mask suite gates against.</summary>
public enum LadderChannel
{
    /// <summary>D-LXIV AWGN: the ideal path plus calibrated noise.</summary>
    Awgn,

    /// <summary>D.6.1 "Poor" (ITU-R F.1487 Mid-Latitude Disturbed): two equal-power Rayleigh
    /// paths 2 ms apart, 1 Hz two-sigma fade.</summary>
    Poor,
}

/// <summary>One rendered point: the bits that went out, and the two forms of the signal that
/// carried them - the complex IQ the waveform route transmits and the real audio the DAX route
/// transmits.</summary>
/// <param name="Point">The point this came from.</param>
/// <param name="Reference">Bits, for the scorer.</param>
/// <param name="Iq">Interleaved complex baseband at the pass's output rate, <b>pre-scaled</b> by
/// the pass IQ gain (<see cref="LadderPass.Gain"/>).</param>
/// <param name="Audio">The real post-channel audio at <see cref="LadderPass.Ms110dAudioRate"/> -
/// the same signal before software up-conversion, what the DAX route transmits. <b>Natural
/// scale</b>, unlike <paramref name="Iq"/>: the DAX route resamples it and applies
/// <see cref="LadderPass.AudioGain"/> at transmit time (see <see cref="LadderCommand"/>), so the
/// one-gain-per-pass level policy is applied to the audio exactly as it is to the IQ, just at the
/// last moment rather than baked in here.</param>
/// <param name="LeadInSeconds">Noise transmitted ahead of the burst - the scorer's guard.</param>
/// <param name="BurstSeconds">Length of the modulated burst itself.</param>
public sealed record RenderedPoint(
    LadderPoint Point,
    Ms110dReferenceBits Reference,
    float[] Iq,
    float[] Audio,
    double LeadInSeconds,
    double BurstSeconds);

/// <summary>Knobs for <see cref="LadderPass"/>.</summary>
public sealed record LadderPassOptions
{
    /// <summary>Complex output rate. 24 kHz for the Flex waveform path; 48 kHz to render a pass
    /// that can be scored offline against a simulated capture.</summary>
    public int OutputRate { get; init; } = FlexIqTransmitter.SampleRate;

    /// <summary>Where the suppressed carrier sits relative to the waveform centre.</summary>
    public double OffsetHz { get; init; } = 2000;

    /// <summary>Peak IQ magnitude of the <em>worst</em> point, which sets the gain for all of them.</summary>
    public double Amplitude { get; init; } = 0.9;

    /// <summary>
    /// Peak real-audio amplitude of the <em>worst</em> point, which sets the DAX audio gain for
    /// all of them - the audio-route counterpart of <see cref="Amplitude"/>.
    /// </summary>
    /// <remarks>A separate constant because the audio and the up-converted IQ have different
    /// natural scales (the complex bandpass and the resample change the peak), so one target
    /// cannot serve both. 0.9 leaves headroom for the 9600→24000 resample overshoot and the DAX
    /// s16 clamp, the same way <see cref="Amplitude"/> does for the waveform's dynamic range.</remarks>
    public double AudioAmplitude { get; init; } = 0.9;

    /// <summary>
    /// Noise-only audio transmitted before each burst.
    /// </summary>
    /// <remarks><b>This is what makes the ladder self-calibrating, and it needs to be generous.</b>
    /// The scorer measures each burst's SNR from the quiet immediately before it; if that quiet
    /// is the band's own noise rather than the noise we injected, every point is scored at the
    /// path's SNR instead of the one under test. Transmitting the rig's own lead-in means the
    /// receiver measures the SNR actually delivered - nominal figures never have to be trusted.
    /// It must cover the scorer's noise window plus its lead (4 s by default) with margin for
    /// the key-up ramp, and it costs nothing into a dummy load.</remarks>
    public double LeadInSeconds { get; init; } = 6.0;

    /// <summary>Noise-only audio transmitted after each burst.</summary>
    public double LeadOutSeconds { get; init; } = 1.0;

    /// <summary>Preamble super-frames. 3 for a bench pass; 20 is what the mask suite gates with.</summary>
    public int PreambleSuperframes { get; init; } = 3;

    /// <summary>Constraint length, 7 or 9.</summary>
    public int ConstraintLength { get; init; } = 7;

    /// <summary>
    /// Transmit SSB passband edges about the suppressed carrier. The default clears the whole
    /// MS110D occupied band, which is the reference case: no truncation anywhere.
    /// </summary>
    /// <remarks>Narrow these to model a transceiver whose IF filter is narrower than the
    /// waveform - a conventional SSB radio filters on transmit as well as receive, and the two
    /// truncations compound. Emulating only the receive side understates the cost.</remarks>
    public double TxSsbLowHz { get; init; } = 150;

    /// <summary>Upper transmit passband edge.</summary>
    public double TxSsbHighHz { get; init; } = 3450;
}

/// <summary>
/// Renders a §E2 ladder: the same bursts the mask suite simulates, put through the same channel
/// rig, ready to go out over real hardware.
/// </summary>
/// <remarks>
/// <para>The whole point of §E2 is a differential - simulation against reality at a matched SNR -
/// so the channel injected here is literally the mask suite's <see cref="WattersonChannel"/>,
/// compiled from where that suite keeps it rather than reimplemented. Two implementations would
/// measure the difference between two rigs and report it as the cost of real hardware.</para>
/// <para><b>Level policy.</b> One gain for the whole pass, taken from the point whose peak is
/// worst. Peak-normalising each point separately would quieten the signal at low SNR, where the
/// noise dominates the envelope - and since the leakage path adds its own noise at a fixed
/// absolute level, that would add a second, uncalibrated dose of noise exactly at the bottom of
/// the ladder where the measurement is most delicate. Signal power is therefore identical at
/// every point and only the injected noise varies, which is the definition of a ladder.</para>
/// <para><b>Nothing here trusts a nominal SNR.</b> Each point carries its own noise lead-in on
/// the air, so the receiver measures what was actually delivered. The injected figure is the
/// request; the scorer's figure is the measurement, and the two are compared.</para>
/// </remarks>
public sealed class LadderPass
{
    private readonly LadderPassOptions _options;

    public LadderPass(LadderPassOptions? options = null) => _options = options ?? new LadderPassOptions();

    /// <summary>Expands an SNR list into points, repeated, with seeds that never collide.</summary>
    /// <remarks>Repeats are interleaved rather than grouped - every SNR is visited once before
    /// any is visited twice - so a pass cut short by weather, a dropped connection or an
    /// operator still covers the whole ladder rather than the top of it.</remarks>
    public static IReadOnlyList<LadderPoint> Plan(
        int waveformNumber,
        IReadOnlyList<double> snrsDb,
        int repeats,
        LadderChannel channel = LadderChannel.Awgn,
        Ms110dInterleaverKind interleaver = Ms110dInterleaverKind.Short,
        int blocks = 1,
        int firstSeed = 1)
    {
        var points = new List<LadderPoint>(snrsDb.Count * repeats);
        int seed = firstSeed;
        for (int round = 0; round < repeats; round++)
        {
            foreach (double snr in snrsDb)
            {
                points.Add(new LadderPoint(waveformNumber, interleaver, snr, channel, seed++, blocks));
            }
        }

        return points;
    }

    /// <summary>Renders every point, applying the pass-wide gain.</summary>
    public IReadOnlyList<RenderedPoint> Render(IReadOnlyList<LadderPoint> points)
    {
        var rendered = new List<RenderedPoint>(points.Count);
        double peak = 0;
        double audioPeak = 0;

        foreach (LadderPoint point in points)
        {
            var settings = new Ms110dTxSettings
            {
                WaveformNumber = point.WaveformNumber,
                Interleaver = point.Interleaver,
                ConstraintLength = _options.ConstraintLength,
                PreambleSuperframes = _options.PreambleSuperframes,
            };

            int payloadBits = Ms110dReferenceBits.PayloadBitsForBlocks(
                point.WaveformNumber, point.Interleaver, point.Blocks);
            var reference = new Ms110dReferenceBits(settings, payloadBits, point.Seed);
            float[] clean = new Ms110dModulator(settings).Modulate(reference.PayloadBits);

            // The channel realisation is derived from the point's seed, so a manifest row is
            // enough to reproduce the exact transmission - payload, fade and noise alike.
            WattersonPath[] paths = point.Channel == LadderChannel.Poor ? WattersonChannel.Poor : [];
            float[] audio = new WattersonChannel(Ms110dAudioRate, point.Seed + 1_000_000, paths).Apply(
                clean,
                point.SnrDb,
                leadInSamples: (int)(_options.LeadInSeconds * Ms110dAudioRate),
                leadOutSamples: (int)(_options.LeadOutSeconds * Ms110dAudioRate));

            float[] iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions
            {
                InputRate = Ms110dAudioRate,
                OutputRate = _options.OutputRate,
                OffsetHz = _options.OffsetHz,
                SsbLowHz = _options.TxSsbLowHz,
                SsbHighHz = _options.TxSsbHighHz,
                PeakNormalise = false, // one gain for the pass, applied below
            }).Convert(audio);

            for (int k = 0; k < iq.Length; k += 2)
            {
                double magnitude = (iq[k] * (double)iq[k]) + (iq[k + 1] * (double)iq[k + 1]);
                peak = Math.Max(peak, magnitude);
            }

            // The DAX route transmits the audio, not the IQ - so its worst-point peak has to be
            // tracked separately (different natural scale) for the audio gain below.
            for (int k = 0; k < audio.Length; k++)
            {
                audioPeak = Math.Max(audioPeak, Math.Abs(audio[k]));
            }

            rendered.Add(new RenderedPoint(
                point, reference, iq, audio,
                _options.LeadInSeconds,
                clean.Length / (double)Ms110dAudioRate));
        }

        // The audio is carried out at NATURAL scale and the pass audio gain is applied at transmit
        // time (the DAX route resamples first, then scales) - so, exactly like the IQ gain, one
        // constant chosen from the worst point sets the level for every point. Signal power is
        // then identical at every point on both routes and only the injected noise varies.
        AudioGain = audioPeak > 1e-12 ? (float)(_options.AudioAmplitude / audioPeak) : 1f;

        peak = Math.Sqrt(peak);
        if (peak > 1e-12)
        {
            // The worst point sets the gain; every other point is quieter only because it
            // carries less noise, never because its signal was turned down.
            float gain = (float)(_options.Amplitude / peak);
            Gain = gain;
            foreach (RenderedPoint point in rendered)
            {
                float[] iq = point.Iq;
                for (int k = 0; k < iq.Length; k++)
                {
                    iq[k] *= gain;
                }
            }
        }

        return rendered;
    }

    /// <summary>The single IQ gain applied across the last <see cref="Render"/> - a pass constant,
    /// and one the manifest has to record for a level to mean anything later.</summary>
    public float Gain { get; private set; } = 1f;

    /// <summary>The single DAX audio gain from the last <see cref="Render"/> - the audio-route
    /// counterpart of <see cref="Gain"/>, applied to the resampled audio at transmit time rather
    /// than baked into <see cref="RenderedPoint.Audio"/>, and recorded in the manifest as the
    /// pass level whenever the DAX route transmitted.</summary>
    public float AudioGain { get; private set; } = 1f;

    /// <summary>MS110D's native audio rate, which is also the rig's.</summary>
    public const int Ms110dAudioRate = 9600;
}
