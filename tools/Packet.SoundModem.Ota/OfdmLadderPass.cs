using M0LTE.Ofdm;

namespace Packet.SoundModem.Ota;

/// <summary>One point of a FreeDV datac OFDM §E2 ladder: a datac packet to transmit through a
/// stated channel at a stated SNR.</summary>
/// <param name="Mode">A <c>freedv-datac*</c> catalogue mode string.</param>
/// <param name="SnrDb">SNR the rig is asked to deliver, in the 3&#160;kHz reference bandwidth.</param>
/// <param name="Channel">Channel geometry (the same rig the sim baseline and MS110D ladder use).</param>
/// <param name="Seed">Payload seed; also derives the channel realisation, so a point reproduces
/// from the manifest alone.</param>
internal sealed record OfdmLadderPoint(string Mode, double SnrDb, SimChannelKind Channel, int Seed);

/// <summary>One rendered OFDM point: the two forms of the signal carrying its datac packet — the
/// complex IQ a simulated capture holds (rehearsal only) and the real 8&#160;kHz audio the DAX route
/// transmits.</summary>
/// <param name="Point">The point this came from — its seed regenerates payload and channel alike.</param>
/// <param name="Iq">Interleaved complex baseband at the pass's output rate, <b>pre-scaled</b> by the
/// pass IQ gain — what a simulated capture writes. Empty when the pass was rendered audio-only (the
/// live path, which never needs IQ).</param>
/// <param name="Audio">The real post-channel audio at the engine-native 8&#160;kHz, noise lead-in and
/// lead-out included — the same signal before SSB placement, what the DAX route transmits. <b>Natural
/// scale</b>: the DAX route resamples it and applies <see cref="OfdmLadderPass.AudioGain"/> at
/// transmit time.</param>
/// <param name="LeadInSeconds">Noise transmitted ahead of the active burst — the scorer's guard, and
/// where the burst's own delivered SNR is measured from.</param>
/// <param name="BurstSeconds">Length of the active modulated burst (preamble + packet + postamble),
/// the region the channel calibrated its noise against and the scorer measures signal power over.</param>
internal sealed record OfdmRenderedPoint(
    OfdmLadderPoint Point,
    float[] Iq,
    float[] Audio,
    double LeadInSeconds,
    double BurstSeconds);

/// <summary>Knobs for <see cref="OfdmLadderPass"/>.</summary>
internal sealed record OfdmLadderPassOptions
{
    /// <summary>Complex output rate of the rendered IQ (the simulated-capture rate on a rehearsal).
    /// Must have an integer ×N/÷2 path from 8&#160;kHz. Ignored when <see cref="RenderIq"/> is off.</summary>
    public int OutputRate { get; init; } = 48000;

    /// <summary>Where the suppressed carrier lands relative to the transmit dial. The DAX route places
    /// audio <c>f</c> at <c>dial + f</c> directly, so its offset is 0 — the datac band sits at its
    /// native ~1500&#160;Hz audio centre and the scorer recovers it with <c>DialHz = 0</c>.</summary>
    public double OffsetHz { get; init; }

    /// <summary>Whether to render the simulated-capture IQ. On for a rehearsal (and the tests, which
    /// lay the IQ out as a capture); off for a live pass, which transmits the audio and would only
    /// throw the IQ away — skipping it drops the SSB up-conversion, the dominant render cost.</summary>
    public bool RenderIq { get; init; } = true;

    /// <summary>Peak real-audio amplitude of the <em>worst</em> point, which sets the DAX audio gain
    /// for all — the level policy applied at transmit time.</summary>
    public double AudioAmplitude { get; init; } = 0.9;

    /// <summary>Noise-only audio transmitted before each burst — what makes the ladder
    /// self-calibrating. It must cover the scorer's noise window with margin for the key-up ramp; it
    /// costs nothing into a dummy load. See <see cref="LadderPassOptions.LeadInSeconds"/>.</summary>
    public double LeadInSeconds { get; init; } = 6.0;

    /// <summary>Noise-only audio transmitted after each burst.</summary>
    public double LeadOutSeconds { get; init; } = 1.0;

    /// <summary>Transmit SSB passband edges about the suppressed carrier. The default clears the whole
    /// datac band (all six modes occupy ≤1700&#160;Hz about a 1500&#160;Hz centre), so nothing is
    /// truncated; narrow it to model a transceiver whose IF filter is narrower than the waveform.</summary>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>Upper transmit passband edge.</summary>
    public double SsbHighHz { get; init; } = 3450;
}

/// <summary>
/// Renders a FreeDV datac OFDM §E2 ladder: datac packets put through the sim baseline's channel rig
/// at a stated SNR, ready to go out over real hardware on the DAX route — the OFDM sibling of
/// <see cref="LadderPass"/>.
/// </summary>
/// <remarks>
/// <para>The channel injected here is literally the sim baseline's <see cref="SimChannel"/> (which is
/// the mask suite's <see cref="Packet.SoundModem.Tests.Channel.WattersonChannel"/>), so an OFDM
/// on-air ladder and its software waterfall are measured against one rig, not two. Rendering drives
/// the datac engine at its native 8&#160;kHz through <see cref="DatacEngine"/> — the same primitives
/// <see cref="DatacPacketProbe"/> grades on — then places the audio on a single sideband with
/// <see cref="Ms110dIqUpconverter"/> (a generic real-audio→SSB-IQ converter, despite its MS110D name)
/// so a rehearsal's simulated capture lands exactly where a real DAX capture would.</para>
/// <para><b>Level policy.</b> One gain for the whole pass, taken from the worst point — identical to
/// <see cref="LadderPass"/>. Signal power is then identical at every rung and only the injected noise
/// varies, which is the definition of a ladder; peak-normalising each point would add a second,
/// uncalibrated dose of noise at the bottom where the measurement is most delicate.</para>
/// <para><b>One packet per burst.</b> Each burst carries exactly one datac packet, so a rehearsal or
/// pass scores per-packet CRC and post-LDPC coded BER — the terms FreeDV publishes its operating
/// points in — with an unambiguous 0/1 result per burst.</para>
/// </remarks>
internal sealed class OfdmLadderPass
{
    /// <summary>The datac engine's native sample rate; every datac mode runs at 8&#160;kHz.</summary>
    public const int NativeRate = 8000;

    /// <summary>Peak IQ magnitude the worst point is scaled to when a simulated capture is rendered —
    /// full-scale headroom for the DAX s16 path, the audio-route counterpart of
    /// <see cref="AudioGain"/>. A constant, not a knob: the demodulator equalises level, so the only
    /// thing that matters is not clipping the rehearsal's WAV.</summary>
    private const double IqPeakTarget = 0.9;

    private readonly OfdmLadderPassOptions _options;

    public OfdmLadderPass(OfdmLadderPassOptions? options = null) =>
        _options = options ?? new OfdmLadderPassOptions();

    /// <summary>Expands an SNR list into points, repeated, with seeds that never collide — repeats
    /// interleaved so a pass cut short still covers the whole ladder (see <see cref="LadderPass.Plan"/>).</summary>
    public static IReadOnlyList<OfdmLadderPoint> Plan(
        string mode, IReadOnlyList<double> snrsDb, int repeats,
        SimChannelKind channel = SimChannelKind.Awgn, int firstSeed = 1)
    {
        _ = DatacEngine.Mode(mode); // fail fast on an unknown datac mode, before any rendering
        var points = new List<OfdmLadderPoint>(snrsDb.Count * Math.Max(1, repeats));
        int seed = firstSeed;
        for (int round = 0; round < repeats; round++)
        {
            foreach (double snr in snrsDb)
            {
                points.Add(new OfdmLadderPoint(mode, snr, channel, seed++));
            }
        }

        return points;
    }

    /// <summary>Renders every point, applying the pass-wide gain.</summary>
    public IReadOnlyList<OfdmRenderedPoint> Render(IReadOnlyList<OfdmLadderPoint> points)
    {
        var rendered = new List<OfdmRenderedPoint>(points.Count);
        double peak = 0;
        double audioPeak = 0;

        foreach (OfdmLadderPoint point in points)
        {
            OfdmMode mode = DatacEngine.Mode(point.Mode);

            // The active burst (preamble + one packet + postamble) at native 8 kHz; its mean power is
            // what the channel calibrates the injected noise against.
            float[] clean = DatacEngine.CleanBurst(mode, DatacEngine.Payload(mode, point.Seed));

            // The sim baseline's channel rig, calibrated against the active-burst power, with a
            // generous noise lead-in for the self-calibrating SNR measurement. The channel realisation
            // is derived from the point's seed (offset from the payload draw), so a manifest row alone
            // reproduces payload, fade and noise.
            float[] audio = SimChannel.Apply(
                clean, NativeRate, point.Channel, point.SnrDb, point.Seed + 5_000_000,
                leadInSeconds: _options.LeadInSeconds, leadOutSeconds: _options.LeadOutSeconds);

            for (int k = 0; k < audio.Length; k++)
            {
                audioPeak = Math.Max(audioPeak, Math.Abs(audio[k]));
            }

            // The IQ is only the rehearsal's simulated capture (SSB placement is done in hardware on a
            // live pass and captured by the RSP), so render it only when asked — the up-conversion is
            // the dominant render cost and a live pass throws it away.
            float[] iq = [];
            if (_options.RenderIq)
            {
                iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions
                {
                    InputRate = NativeRate,
                    OutputRate = _options.OutputRate,
                    OffsetHz = _options.OffsetHz,
                    SsbLowHz = _options.SsbLowHz,
                    SsbHighHz = _options.SsbHighHz,
                    PeakNormalise = false, // one gain for the pass, applied below
                }).Convert(audio);

                for (int k = 0; k < iq.Length; k += 2)
                {
                    peak = Math.Max(peak, (iq[k] * (double)iq[k]) + (iq[k + 1] * (double)iq[k + 1]));
                }
            }

            rendered.Add(new OfdmRenderedPoint(
                point, iq, audio, _options.LeadInSeconds, clean.Length / (double)NativeRate));
        }

        // Natural-scale audio + one pass gain applied at transmit time: exactly like the IQ gain, one
        // constant from the worst point sets every point's level, so only the injected noise varies.
        AudioGain = audioPeak > 1e-12 ? (float)(_options.AudioAmplitude / audioPeak) : 1f;

        peak = Math.Sqrt(peak);
        if (_options.RenderIq && peak > 1e-12)
        {
            float gain = (float)(IqPeakTarget / peak);
            foreach (OfdmRenderedPoint point in rendered)
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

    /// <summary>The single DAX audio gain from the last <see cref="Render"/>, applied to the resampled
    /// audio at transmit time and recorded in the manifest as the pass level.</summary>
    public float AudioGain { get; private set; } = 1f;
}
