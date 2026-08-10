using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>
/// The FM-native catalogue modes and each one's target peak deviation.
/// </summary>
/// <remarks>
/// A shim over <see cref="FmModeProfiles"/>, which is the single table. This used to be a dictionary of
/// exact mode names, maintained by hand alongside a second copy in <c>SimChannel</c> and a third
/// for channel spacings. Three copies of one fact drift, and the ladder and the simulator
/// disagreeing about how hard a mode is driven would have been invisible in both.
/// </remarks>
internal static class FmModeCatalog
{
    /// <summary>Whether a mode is carried over FM (and so belongs to the FM ladder, not the SSB one).</summary>
    public static bool IsFmMode(string? mode) => FmModeProfiles.IsFmMode(mode);

    /// <summary>The mode's target peak FM deviation in Hz.</summary>
    /// <exception cref="ArgumentException">The mode is not an FM-native mode.</exception>
    public static double TargetDeviationHz(string mode) =>
        FmModeProfiles.For(mode)?.PeakDeviationHz
        ?? throw new ArgumentException(
            $"'{mode}' is not an FM-native mode - the FM ladder carries "
            + $"{string.Join(", ", Modes)}", nameof(mode));

    /// <summary>Every FM-native mode, in catalogue order.</summary>
    /// <remarks>
    /// Taken from the catalogue rather than listed here, so a new framing variant of an FM mode
    /// joins the ladder by existing rather than by being remembered.
    /// </remarks>
    public static IReadOnlyCollection<string> Modes =>
        [.. ModemCatalog.KnownModes.Where(FmModeProfiles.IsFmMode)];
}

/// <summary>One point of an FM §E2 ladder: a burst to transmit through a stated channel at a stated
/// SNR, frequency-modulated at the mode's target deviation.</summary>
/// <param name="Mode">An FM-native <see cref="ModemCatalog"/> mode.</param>
/// <param name="SnrDb">SNR the rig is asked to deliver, in the 3&#160;kHz reference bandwidth.</param>
/// <param name="Channel">Channel geometry (the same rig the sim baseline and other ladders use).</param>
/// <param name="Seed">Payload seed; also derives the channel realisation, so a point reproduces
/// from the manifest alone.</param>
internal sealed record FmLadderPoint(string Mode, double SnrDb, SimChannelKind Channel, int Seed);

/// <summary>One rendered FM point.</summary>
/// <param name="Point">The point this came from - its seed regenerates payload and channel alike.</param>
/// <param name="Frame">The AX.25 frame that went out, for the scorer's exact comparison.</param>
/// <param name="Iq">Interleaved complex FM baseband at the pass's output rate - noise lead-in,
/// burst and lead-out, all frequency-modulated at the target deviation. This is what the RSP1
/// captures on a live pass and what a rehearsal lays out as a simulated capture.</param>
/// <param name="Audio">The real post-channel audio (noise lead-in included) at the mode's DSP rate,
/// before FM modulation - the signal the DAX FM route hands the radio. <b>Natural scale</b>.</param>
/// <param name="LeadInSeconds">Noise transmitted ahead of the active burst - the scorer's guard and
/// where the burst's delivered SNR is measured from.</param>
/// <param name="BurstSeconds">Length of the active modulated burst.</param>
/// <param name="DspRate">The mode's DSP audio rate (the rate of <paramref name="Audio"/>).</param>
internal sealed record FmRenderedPoint(
    FmLadderPoint Point,
    byte[] Frame,
    float[] Iq,
    float[] Audio,
    double LeadInSeconds,
    double BurstSeconds,
    int DspRate);

/// <summary>Knobs for <see cref="FmLadderPass"/>.</summary>
internal sealed record FmLadderPassOptions
{
    /// <summary>Complex output rate of the rendered FM baseband (the simulated-capture rate on a
    /// rehearsal, and the rate the live capture is scored against). Must be an integer multiple of
    /// the mode's DSP rate, and wide enough for the FM Carson bandwidth.</summary>
    public int OutputRate { get; init; } = 48000;

    /// <summary>AX.25 frame payload size, bytes.</summary>
    public int FrameBytes { get; init; } = 48;

    /// <summary>TXDELAY - the flag/preamble run-in ahead of each frame, milliseconds. The fast
    /// baseband FSK detectors need a real run-in to lock before the frame; 150 ms is ample at every
    /// FM baud and is realistic over-the-air. See <see cref="SimModem.RenderBurst"/>.</summary>
    public int TxDelayMs { get; init; } = 150;

    /// <summary>Whether to render the FM baseband IQ. On for a rehearsal and the tests; off for a
    /// live pass, which transmits the audio through the radio's FM modulator and would only throw
    /// the software IQ away.</summary>
    public bool RenderIq { get; init; } = true;

    /// <summary>Constant-envelope magnitude of the rendered IQ - headroom for the capture's s16
    /// clamp only; FM carries no amplitude the demodulator reads.</summary>
    public double IqAmplitude { get; init; } = 0.9;

    /// <summary>Peak real-audio amplitude of the worst point, which sets the DAX FM drive for all -
    /// the level the radio's FM modulator sees. The actual deviation is then this drive times the
    /// radio's FM gain, which the on-air calibration sets so the achieved deviation is the target.</summary>
    public double AudioAmplitude { get; init; } = 0.9;

    /// <summary>Noise-only audio transmitted before each burst - what makes the ladder
    /// self-calibrating (see <see cref="LadderPassOptions.LeadInSeconds"/>).</summary>
    public double LeadInSeconds { get; init; } = 6.0;

    /// <summary>Noise-only audio transmitted after each burst.</summary>
    public double LeadOutSeconds { get; init; } = 1.0;
}

/// <summary>
/// Renders an FM §E2 ladder: catalogue-mode bursts put through the sim baseline's channel at a
/// stated SNR and <b>frequency-modulated at the mode's target peak deviation</b>, ready to go out
/// over real hardware on the FM route - the FM sibling of <see cref="OfdmLadderPass"/>.
/// </summary>
/// <remarks>
/// <para>The audio is generated and the channel injected exactly as the software waterfall does -
/// the mode's own <see cref="IModem"/> through <see cref="SimModem"/>, the mask suite's
/// <see cref="Packet.SoundModem.Tests.Channel.WattersonChannel"/> through <see cref="SimChannel"/> -
/// then the whole thing (noise lead-in and all) is frequency-modulated by <see cref="FmModulator"/>.
/// Injecting the noise as audio <em>before</em> the FM modulator is faithful to the on-air ladder,
/// which transmits its noise lead-in through the radio's FM modulator too, and it is what makes the
/// mod/demod round trip preserve the delivered SNR for the scorer to recover.</para>
/// <para><b>Deviation policy - the FM counterpart of the SSB one-gain-per-pass.</b> Where the SSB
/// ladder fixes one <em>amplitude</em> gain from the worst point so signal power is constant across
/// rungs, the FM ladder fixes one <em>deviation</em> gain: the modulator's reference amplitude is the
/// worst point's clean-signal peak, so the signal deviates to the target at every rung and only the
/// injected noise (which adds deviation on top) varies. FM is constant-envelope, so amplitude is not
/// the lever here - deviation is.</para>
/// </remarks>
internal sealed class FmLadderPass
{
    private readonly FmLadderPassOptions _options;

    public FmLadderPass(FmLadderPassOptions? options = null) =>
        _options = options ?? new FmLadderPassOptions();

    /// <summary>The mode's DSP rate - the audio rate the FM chain runs the modem at.</summary>
    public int DspRate { get; private set; }

    /// <summary>The mode's target peak FM deviation, Hz - the value the drive is calibrated to.</summary>
    public double TargetDeviationHz { get; private set; }

    /// <summary>The single DAX FM audio drive from the last <see cref="Render"/>.</summary>
    public float AudioGain { get; private set; } = 1f;

    /// <summary>Expands an SNR list into points, repeated, with seeds that never collide - repeats
    /// interleaved so a pass cut short still covers the whole ladder.</summary>
    public static IReadOnlyList<FmLadderPoint> Plan(
        string mode, IReadOnlyList<double> snrsDb, int repeats,
        SimChannelKind channel = SimChannelKind.Awgn, int firstSeed = 1)
    {
        if (!FmModeCatalog.IsFmMode(mode))
        {
            throw new ArgumentException(
                $"'{mode}' is not an FM-native mode - the FM ladder carries "
                + $"{string.Join(", ", FmModeCatalog.Modes)}", nameof(mode));
        }

        var points = new List<FmLadderPoint>(snrsDb.Count * Math.Max(1, repeats));
        int seed = firstSeed;
        for (int round = 0; round < repeats; round++)
        {
            foreach (double snr in snrsDb)
            {
                points.Add(new FmLadderPoint(mode, snr, channel, seed++));
            }
        }

        return points;
    }

    /// <summary>Renders every point, applying the pass-wide deviation reference and DAX drive gain.</summary>
    public IReadOnlyList<FmRenderedPoint> Render(IReadOnlyList<FmLadderPoint> points)
    {
        if (points.Count == 0)
        {
            return [];
        }

        string mode = points[0].Mode;
        DspRate = ModemCatalog.DspRateFor(mode);
        TargetDeviationHz = FmModeCatalog.TargetDeviationHz(mode);
        if (_options.OutputRate % DspRate != 0)
        {
            throw new ArgumentException(
                $"OutputRate {_options.OutputRate} must be an integer multiple of the {mode} DSP "
                + $"rate {DspRate}");
        }

        // Pass 1: render each burst's clean signal and its post-channel audio, tracking the worst
        // clean-signal peak (the deviation reference) and the worst post-channel audio peak (the
        // DAX drive reference) - one of each for the whole pass, from the worst point.
        var frames = new byte[points.Count][];
        var cleans = new float[points.Count][];
        var audios = new float[points.Count][];
        double cleanPeak = 0;
        double audioPeak = 0;
        for (int k = 0; k < points.Count; k++)
        {
            FmLadderPoint point = points[k];
            var sm = new SimModem(point.Mode, DspRate);
            byte[] frame = SimModem.Frame(_options.FrameBytes, point.Seed);
            float[] clean = sm.RenderBurst(frame, _options.TxDelayMs);
            float[] audio = SimChannel.Apply(
                clean, DspRate, point.Channel, point.SnrDb, point.Seed + 7_000_000,
                leadInSeconds: _options.LeadInSeconds, leadOutSeconds: _options.LeadOutSeconds);

            for (int i = 0; i < clean.Length; i++)
            {
                cleanPeak = Math.Max(cleanPeak, Math.Abs(clean[i]));
            }

            for (int i = 0; i < audio.Length; i++)
            {
                audioPeak = Math.Max(audioPeak, Math.Abs(audio[i]));
            }

            frames[k] = frame;
            cleans[k] = clean;
            audios[k] = audio;
        }

        double referenceAmplitude = cleanPeak > 1e-12 ? cleanPeak : 1.0;
        AudioGain = audioPeak > 1e-12 ? (float)(_options.AudioAmplitude / audioPeak) : 1f;

        // Pass 2: frequency-modulate each point's post-channel audio at the target deviation. The
        // reference amplitude is the worst clean-signal peak, so the signal deviates to the target
        // at every rung and only the noise adds deviation on top.
        var rendered = new List<FmRenderedPoint>(points.Count);
        for (int k = 0; k < points.Count; k++)
        {
            float[] iq = [];
            if (_options.RenderIq)
            {
                iq = new FmModulator(new FmModulatorOptions
                {
                    InputRate = DspRate,
                    OutputRate = _options.OutputRate,
                    PeakDeviationHz = TargetDeviationHz,
                    ReferenceAmplitude = referenceAmplitude,
                    Amplitude = _options.IqAmplitude,
                }).Modulate(audios[k]);
            }

            rendered.Add(new FmRenderedPoint(
                points[k], frames[k], iq, audios[k],
                _options.LeadInSeconds, cleans[k].Length / (double)DspRate, DspRate));
        }

        return rendered;
    }
}
