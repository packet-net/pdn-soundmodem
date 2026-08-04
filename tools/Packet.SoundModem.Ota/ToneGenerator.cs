namespace Packet.SoundModem.Ota;

/// <summary>
/// Complex-baseband test tones for the Flex waveform IQ transmit sink
/// (<c>FlexWaveformIqOutput</c>, interleaved I,Q at 24 kHz).
/// </summary>
/// <remarks>
/// <para>The tone is the first thing on the air (ota-execution-plan §E0.5) and it is
/// deliberately the <em>only</em> instrument in that session: it needs no modem, no
/// upconverter and no scorer, and it is a far better detector of faults in the new
/// transmit stack than a modem burst. A waveform is reflection-driven - the radio pulls one
/// TX buffer at a time while keyed - so a dropped, starved or duplicated buffer produces a
/// <b>phase discontinuity</b>, which around an otherwise pure carrier is visible as spectral
/// splatter. The same fault inside a data burst would only read as "bad decode".</para>
/// <para>Phase is accumulated per tone and wrapped every sample, so a long tone stays
/// exactly on frequency rather than drifting with floating-point error - the point being
/// that any discontinuity we <em>do</em> see is the radio's, not ours.</para>
/// </remarks>
public static class ToneGenerator
{
    /// <summary>Default cosine-taper applied at each end of a burst, in seconds. Keying a
    /// rectangular-edged tone splatters; 5 ms is inaudible to the measurement and keeps the
    /// key-on/key-off transient out of the spectrum.</summary>
    public const double DefaultRampSeconds = 0.005;

    /// <summary>
    /// Generates a sum of complex exponentials as interleaved I,Q samples.
    /// </summary>
    /// <param name="frequenciesHz">Tone frequencies relative to the waveform centre
    /// (the slice/dial frequency). Positive = above the carrier. Two entries gives the
    /// two-tone IMD stimulus.</param>
    /// <param name="amplitudePerTone">Peak amplitude of each tone. Note the envelope of an
    /// N-tone sum peaks at N × this, so check <see cref="PeakMagnitude"/> before
    /// transmitting.</param>
    /// <param name="seconds">Duration of the tone itself (ramps included).</param>
    /// <param name="sampleRate">Complex sample rate - 24000 for the Flex waveform path.</param>
    /// <param name="rampSeconds">Raised-cosine taper at each end.</param>
    public static float[] Complex(
        IReadOnlyList<double> frequenciesHz,
        double amplitudePerTone,
        double seconds,
        int sampleRate,
        double rampSeconds = DefaultRampSeconds)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        if (frequenciesHz.Count == 0)
        {
            throw new ArgumentException("at least one tone frequency is required", nameof(frequenciesHz));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        double nyquist = sampleRate / 2.0;
        for (int t = 0; t < frequenciesHz.Count; t++)
        {
            if (Math.Abs(frequenciesHz[t]) >= nyquist)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequenciesHz),
                    frequenciesHz[t],
                    $"tone must be inside ±{nyquist:F0} Hz of the waveform centre");
            }
        }

        int n = (int)Math.Round(seconds * sampleRate);
        int rampSamples = Math.Min((int)Math.Round(Math.Max(0, rampSeconds) * sampleRate), n / 2);

        int tones = frequenciesHz.Count;
        var phase = new double[tones];
        var step = new double[tones];
        for (int t = 0; t < tones; t++)
        {
            step[t] = 2.0 * Math.PI * frequenciesHz[t] / sampleRate;
        }

        var iq = new float[2 * n];
        for (int k = 0; k < n; k++)
        {
            double envelope = Envelope(k, n, rampSamples) * amplitudePerTone;
            double re = 0, im = 0;
            for (int t = 0; t < tones; t++)
            {
                re += Math.Cos(phase[t]);
                im += Math.Sin(phase[t]);
                phase[t] += step[t];
                if (phase[t] > Math.PI)
                {
                    phase[t] -= 2.0 * Math.PI;
                }
                else if (phase[t] < -Math.PI)
                {
                    phase[t] += 2.0 * Math.PI;
                }
            }

            iq[2 * k] = (float)(re * envelope);
            iq[(2 * k) + 1] = (float)(im * envelope);
        }

        return iq;
    }

    /// <summary>
    /// Generates a sum of real cosines as mono samples - the DAX-audio counterpart of
    /// <see cref="Complex"/>.
    /// </summary>
    /// <remarks>The DAX route hands the radio real audio and the radio's own DIGU SSB modulator
    /// places it: a tone at <paramref name="frequenciesHz"/> lands at dial + that frequency, so
    /// there is no analytic baseband, no image to reject and no NCO - the point of the DAX path
    /// versus the IQ one. Phase is accumulated and wrapped per tone for the same reason the
    /// complex version does it: a long tone stays exactly on frequency.</remarks>
    /// <param name="frequenciesHz">Audio tone frequencies (Hz). On DIGU each lands at dial + f.</param>
    /// <param name="amplitudePerTone">Peak amplitude of each tone. An N-tone sum peaks at N × this;
    /// check <see cref="PeakReal"/> before transmitting.</param>
    /// <param name="seconds">Duration of the tone itself (ramps included).</param>
    /// <param name="sampleRate">Audio sample rate - 24000 for the reduced-bandwidth DAX path.</param>
    /// <param name="rampSeconds">Raised-cosine taper at each end.</param>
    public static float[] Real(
        IReadOnlyList<double> frequenciesHz,
        double amplitudePerTone,
        double seconds,
        int sampleRate,
        double rampSeconds = DefaultRampSeconds)
    {
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        if (frequenciesHz.Count == 0)
        {
            throw new ArgumentException("at least one tone frequency is required", nameof(frequenciesHz));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        double nyquist = sampleRate / 2.0;
        for (int t = 0; t < frequenciesHz.Count; t++)
        {
            if (frequenciesHz[t] <= 0 || frequenciesHz[t] >= nyquist)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequenciesHz),
                    frequenciesHz[t],
                    $"a real audio tone must be inside 0…{nyquist:F0} Hz");
            }
        }

        int n = (int)Math.Round(seconds * sampleRate);
        int rampSamples = Math.Min((int)Math.Round(Math.Max(0, rampSeconds) * sampleRate), n / 2);

        int tones = frequenciesHz.Count;
        var phase = new double[tones];
        var step = new double[tones];
        for (int t = 0; t < tones; t++)
        {
            step[t] = 2.0 * Math.PI * frequenciesHz[t] / sampleRate;
        }

        var audio = new float[n];
        for (int k = 0; k < n; k++)
        {
            double envelope = Envelope(k, n, rampSamples) * amplitudePerTone;
            double sample = 0;
            for (int t = 0; t < tones; t++)
            {
                sample += Math.Cos(phase[t]);
                phase[t] += step[t];
                if (phase[t] > Math.PI)
                {
                    phase[t] -= 2.0 * Math.PI;
                }
            }

            audio[k] = (float)(sample * envelope);
        }

        return audio;
    }

    /// <summary>Interleaved I,Q silence - the lead-in that absorbs the PTT settle
    /// (139 ms measured on the 6500) and the lead-out that lets the tail drain before
    /// unkeying.</summary>
    public static float[] Silence(double seconds, int sampleRate)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        return new float[2 * (int)Math.Round(seconds * sampleRate)];
    }

    /// <summary>Mono silence - the DAX-route lead-in/lead-out, one sample per frame rather than
    /// the interleaved I,Q pair <see cref="Silence"/> returns.</summary>
    public static float[] SilenceReal(double seconds, int sampleRate)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        return new float[(int)Math.Round(seconds * sampleRate)];
    }

    /// <summary>Concatenates interleaved I,Q blocks.</summary>
    public static float[] Concat(params float[][] blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        int total = 0;
        foreach (float[] b in blocks)
        {
            total += b.Length;
        }

        var result = new float[total];
        int offset = 0;
        foreach (float[] b in blocks)
        {
            b.CopyTo(result, offset);
            offset += b.Length;
        }

        return result;
    }

    /// <summary>Largest |I + jQ| in an interleaved buffer - the clipping check before a
    /// burst goes anywhere near the radio.</summary>
    public static double PeakMagnitude(ReadOnlySpan<float> interleavedIq)
    {
        double peak = 0;
        for (int k = 0; k + 1 < interleavedIq.Length; k += 2)
        {
            double m = (interleavedIq[k] * (double)interleavedIq[k])
                + (interleavedIq[k + 1] * (double)interleavedIq[k + 1]);
            if (m > peak)
            {
                peak = m;
            }
        }

        return Math.Sqrt(peak);
    }

    /// <summary>Largest |sample| in a mono buffer - the DAX-route clipping check, the real-audio
    /// counterpart of <see cref="PeakMagnitude"/>. The radio's s16 DAX transport clamps at ±1.0,
    /// so anything above that is clipped before it reaches the SSB modulator.</summary>
    public static double PeakReal(ReadOnlySpan<float> audio)
    {
        double peak = 0;
        for (int k = 0; k < audio.Length; k++)
        {
            double m = Math.Abs(audio[k]);
            if (m > peak)
            {
                peak = m;
            }
        }

        return peak;
    }

    private static double Envelope(int k, int n, int rampSamples)
    {
        if (rampSamples <= 0)
        {
            return 1.0;
        }

        if (k < rampSamples)
        {
            return 0.5 * (1.0 - Math.Cos(Math.PI * k / rampSamples));
        }

        int fromEnd = n - 1 - k;
        if (fromEnd < rampSamples)
        {
            return 0.5 * (1.0 - Math.Cos(Math.PI * fromEnd / rampSamples));
        }

        return 1.0;
    }
}
