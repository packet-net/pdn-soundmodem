using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>One point of an SSB audio-carrier §E2 ladder: a frame to transmit through a stated
/// channel at a stated SNR, carried on the mode's audio tone-pair or PSK carrier.</summary>
/// <param name="Mode">An SSB audio-carrier catalogue mode (<c>afsk300*</c>/<c>bpsk*</c>/<c>qpsk600</c>/<c>qpsk2400</c>).</param>
/// <param name="SnrDb">SNR the rig is asked to deliver, in the 3&#160;kHz reference bandwidth.</param>
/// <param name="Channel">Channel geometry (the same rig the sim baseline and the other ladders use).</param>
/// <param name="Seed">Payload seed; also derives the channel realisation, so a point reproduces from
/// the manifest alone.</param>
/// <param name="FrameBytes">AX.25 frame size the seed fills - carried so the scorer regenerates the
/// exact bytes that went out.</param>
internal sealed record SsbLadderPoint(
    string Mode, double SnrDb, SimChannelKind Channel, int Seed, int FrameBytes);

/// <summary>One rendered audio-carrier point: the two forms of the signal carrying its frame - the
/// complex IQ a simulated capture holds (rehearsal only) and the real audio the DAX route
/// transmits.</summary>
/// <param name="Point">The point this came from - its seed regenerates payload and channel alike.</param>
/// <param name="Iq">Interleaved complex baseband at the pass's output rate, <b>pre-scaled</b> by the
/// pass IQ gain - what a simulated capture writes. Empty when the pass was rendered audio-only (the
/// live path, which never needs IQ).</param>
/// <param name="Audio">The real post-channel audio at the mode's native DSP rate, noise lead-in and
/// lead-out included - the same signal before SSB placement, what the DAX route transmits. <b>Natural
/// scale</b>: the DAX route resamples it and applies <see cref="SsbLadderPass.AudioGain"/> at
/// transmit time.</param>
/// <param name="LeadInSeconds">Noise transmitted ahead of the active burst - the scorer's guard, and
/// where the burst's own delivered SNR is measured from.</param>
/// <param name="BurstSeconds">Length of the active modulated burst (preamble + frame), the region the
/// channel calibrated its noise against and the scorer measures signal power over.</param>
internal sealed record SsbRenderedPoint(
    SsbLadderPoint Point,
    float[] Iq,
    float[] Audio,
    double LeadInSeconds,
    double BurstSeconds);

/// <summary>Knobs for <see cref="SsbLadderPass"/>.</summary>
internal sealed record SsbLadderPassOptions
{
    /// <summary>Complex output rate of the rendered IQ (the simulated-capture rate on a rehearsal).
    /// Must have an integer ×N/÷2 path from the mode's native rate. Ignored when <see cref="RenderIq"/>
    /// is off.</summary>
    public int OutputRate { get; init; } = 48000;

    /// <summary>Where the suppressed carrier lands relative to the transmit dial. The DAX route places
    /// audio <c>f</c> at <c>dial + f</c> directly, so its offset is 0 - the carrier sits at its
    /// native audio centre and the scorer recovers it with <c>DialHz = 0</c>.</summary>
    public double OffsetHz { get; init; }

    /// <summary>Whether to render the simulated-capture IQ. On for a rehearsal (and the tests, which
    /// lay the IQ out as a capture); off for a live pass, which transmits the audio and would only
    /// throw the IQ away - skipping it drops the SSB up-conversion, the dominant render cost.</summary>
    public bool RenderIq { get; init; } = true;

    /// <summary>Peak real-audio amplitude of the <em>worst</em> point, which sets the DAX audio gain
    /// for all - the level policy applied at transmit time.</summary>
    public double AudioAmplitude { get; init; } = 0.9;

    /// <summary>Noise-only audio transmitted before each burst - what makes the ladder
    /// self-calibrating. It must cover the scorer's noise window with margin for the key-up ramp; it
    /// costs nothing into a dummy load. See <see cref="LadderPassOptions.LeadInSeconds"/>.</summary>
    public double LeadInSeconds { get; init; } = 6.0;

    /// <summary>Noise-only audio transmitted after each burst.</summary>
    public double LeadOutSeconds { get; init; } = 1.0;

    /// <summary>Transmit SSB passband edges about the suppressed carrier. The default clears the whole
    /// occupied band of every target mode (the widest, qpsk2400 at 1200&#160;baud about a
    /// 1500&#160;Hz carrier, spans ≈900-2100&#160;Hz; afsk300 sits at 1700&#160;Hz), so nothing is
    /// truncated.</summary>
    public double SsbLowHz { get; init; } = 150;

    /// <summary>Upper transmit passband edge.</summary>
    public double SsbHighHz { get; init; } = 3450;
}

/// <summary>
/// Renders an SSB audio-carrier §E2 ladder: frames put through the sim baseline's channel rig at a
/// stated SNR, ready to go out over real hardware on the DAX route - the audio-carrier sibling of
/// <see cref="LadderPass"/> and <see cref="OfdmLadderPass"/>.
/// </summary>
/// <remarks>
/// <para>These modes (<c>bpsk1200</c>, <c>qpsk600</c>, <c>afsk300</c> and its IL2P variants, and the
/// corpus-validated <c>bpsk300</c>/<c>qpsk2400</c>) place their carrier - a PSK carrier or an AFSK
/// tone-pair - <em>inside</em> the audio, so they ride the same DAX (DIGU SSB) route the MS110D and
/// datac ladders deploy on: the radio's own modulator does the sideband placement the software
/// up-converter does on the bench IQ route. (<c>qpsk3600</c> looks like one of these but is an FM mode
/// and is excluded - see <see cref="IsSsbMode"/>.)</para>
/// <para>Rendering drives the mode entirely through the <see cref="IModem"/> seam that
/// <see cref="ModemCatalog.Create"/> hands back - reusing <see cref="SimModem"/>, the same
/// mode-generic adapter the software <c>sim</c> waterfall uses - so nothing here is coupled to any one
/// modem. The channel injected is literally the sim baseline's <see cref="SimChannel"/> (the mask
/// suite's <see cref="Packet.SoundModem.Tests.Channel.WattersonChannel"/>), so an on-air ladder and
/// its software waterfall are measured against one rig, not two. The audio is then placed on a single
/// sideband with <see cref="Ms110dIqUpconverter"/> (a generic real-audio→SSB-IQ converter, despite its
/// MS110D name) so a rehearsal's simulated capture lands exactly where a real DAX capture would.</para>
/// <para><b>Level policy.</b> One gain for the whole pass, taken from the worst point - identical to
/// <see cref="LadderPass"/> and <see cref="OfdmLadderPass"/>. Signal power is then identical at every
/// rung and only the injected noise varies, which is the definition of a ladder.</para>
/// </remarks>
internal sealed class SsbLadderPass
{
    /// <summary>Peak IQ magnitude the worst point is scaled to when a simulated capture is rendered -
    /// full-scale headroom for the s16 path, the audio-route counterpart of <see cref="AudioGain"/>.
    /// A constant, not a knob: the demodulator equalises level, so the only thing that matters is not
    /// clipping the rehearsal's WAV.</summary>
    private const double IqPeakTarget = 0.9;

    private readonly SsbLadderPassOptions _options;

    public SsbLadderPass(SsbLadderPassOptions? options = null) =>
        _options = options ?? new SsbLadderPassOptions();

    /// <summary>Expands an SNR list into points, repeated, with seeds that never collide - repeats
    /// interleaved so a pass cut short still covers the whole ladder (see <see cref="LadderPass.Plan"/>).</summary>
    public static IReadOnlyList<SsbLadderPoint> Plan(
        string mode, IReadOnlyList<double> snrsDb, int repeats,
        SimChannelKind channel = SimChannelKind.Awgn, int firstSeed = 1, int frameBytes = 60)
    {
        if (!IsSsbMode(mode))
        {
            throw new ArgumentException(
                $"the SSB ladder only renders the SSB audio-carrier modes ({string.Join(", ", SsbModes)}), "
                + $"not '{mode}' (qpsk3600 is an FM mode - it belongs to the FM coverage path)", nameof(mode));
        }

        var points = new List<SsbLadderPoint>(snrsDb.Count * Math.Max(1, repeats));
        int seed = firstSeed;
        for (int round = 0; round < repeats; round++)
        {
            foreach (double snr in snrsDb)
            {
                points.Add(new SsbLadderPoint(mode, snr, channel, seed++, frameBytes));
            }
        }

        return points;
    }

    /// <summary>
    /// The SSB audio-carrier modes this ladder covers: the Bell-103-style AFSK 300 tone-pair and the
    /// BPSK/QPSK carriers, all placed inside the audio and deployed over SSB (DIGU) on the real
    /// NinoTNC IL2P network.
    /// </summary>
    /// <remarks>An explicit allow-list, not a name prefix, because whether an audio-carrier mode
    /// deploys over SSB or FM is <b>not</b> a property of its modem - the NinoTNC v3/4.44 firmware
    /// modulator table is the authority. <c>qpsk3600</c> accepts a centre frequency
    /// (<see cref="ModemCatalog.AcceptsCentreFrequency"/> is true for it) yet is an <em>FM</em> mode -
    /// it shares the FM modulator with C4FSK 19200 (2079&#160;Hz sub-carrier, 5&#160;kHz deviation) -
    /// so it is deliberately excluded here and handled by the separate FM coverage path. AFSK 1200 is
    /// likewise the classic VHF-FM mode and is not listed. So the set is enumerated, not derived.</remarks>
    private static readonly HashSet<string> SsbModes = new(StringComparer.Ordinal)
    {
        "afsk300", "afsk300-il2p", "afsk300-il2pc",
        "bpsk300", "bpsk300-multi", "bpsk300-nocrc",
        "bpsk1200", "bpsk1200-multi",
        "qpsk600", "qpsk2400",
    };

    /// <summary>True for the SSB audio-carrier modes this ladder covers, false for anything else
    /// (including null and the FM qpsk3600). These ride the DAX (DIGU SSB) route.</summary>
    public static bool IsSsbMode(string? mode) => mode is not null && SsbModes.Contains(mode);

    /// <summary>The mode's native DSP rate - the rate its mod/demod chain and the injected channel run
    /// at. All the SSB audio-carrier modes are 12&#160;kHz.</summary>
    public static int NativeRateFor(string mode) => ModemCatalog.DspRateFor(mode);

    /// <summary>Renders every point, applying the pass-wide gain.</summary>
    public IReadOnlyList<SsbRenderedPoint> Render(IReadOnlyList<SsbLadderPoint> points)
    {
        var rendered = new List<SsbRenderedPoint>(points.Count);
        double peak = 0;
        double audioPeak = 0;

        foreach (SsbLadderPoint point in points)
        {
            int nativeRate = NativeRateFor(point.Mode);

            // The active burst (preamble + one framed AX.25 frame) at the mode's native rate, modulator
            // silence trimmed so its mean power is what the channel calibrates the injected noise
            // against - exactly what the software sim path renders.
            var sm = new SimModem(point.Mode, nativeRate);
            byte[] frame = SimModem.Frame(point.FrameBytes, point.Seed);
            float[] clean = sm.RenderBurst(frame);

            // The sim baseline's channel rig, calibrated against the active-burst power, with a
            // generous noise lead-in for the self-calibrating SNR measurement. The channel realisation
            // is derived from the point's seed (offset from the payload draw), so a manifest row alone
            // reproduces payload, fade and noise.
            float[] audio = SimChannel.Apply(
                clean, nativeRate, point.Channel, point.SnrDb, point.Seed + 5_000_000,
                leadInSeconds: _options.LeadInSeconds, leadOutSeconds: _options.LeadOutSeconds);

            for (int k = 0; k < audio.Length; k++)
            {
                audioPeak = Math.Max(audioPeak, Math.Abs(audio[k]));
            }

            // The IQ is only the rehearsal's simulated capture (SSB placement is done in hardware on a
            // live pass and captured by the RSP), so render it only when asked - the up-conversion is
            // the dominant render cost and a live pass throws it away.
            float[] iq = [];
            if (_options.RenderIq)
            {
                iq = new Ms110dIqUpconverter(new Ms110dIqUpconverterOptions
                {
                    InputRate = nativeRate,
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

            rendered.Add(new SsbRenderedPoint(
                point, iq, audio, _options.LeadInSeconds, clean.Length / (double)nativeRate));
        }

        // Natural-scale audio + one pass gain applied at transmit time: exactly like the IQ gain, one
        // constant from the worst point sets every point's level, so only the injected noise varies.
        AudioGain = audioPeak > 1e-12 ? (float)(_options.AudioAmplitude / audioPeak) : 1f;

        peak = Math.Sqrt(peak);
        if (_options.RenderIq && peak > 1e-12)
        {
            float gain = (float)(IqPeakTarget / peak);
            Gain = gain;
            foreach (SsbRenderedPoint point in rendered)
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

    /// <summary>The single IQ gain from the last <see cref="Render"/> - the peak-normalisation constant
    /// already baked into every point's <see cref="SsbRenderedPoint.Iq"/>. This is the level that
    /// reaches the radio on the IQ transmit route; recorded in the manifest as that route's pass gain.
    /// Only set when the pass rendered IQ (<see cref="SsbLadderPassOptions.RenderIq"/>).</summary>
    public float Gain { get; private set; } = 1f;
}
