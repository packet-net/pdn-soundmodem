namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// A low pass on the audio the sync search looks at, and on nothing else.
/// </summary>
/// <remarks>
/// <para>An FM discriminator hands back everything its IF filter passed, about 4 kHz of audio on a
/// 12.5 kHz channel. A narrow profile occupies only the lower part of that, so the top slice
/// carries no signal at all, and discriminator noise power rises with the SQUARE of audio
/// frequency, making it the noisiest slice there is.</para>
/// <para>The demodulator never minded, because every carrier is a transform bin and out-of-band
/// noise lands outside all of them. The sync search does mind: it correlates the raw waveform in
/// the time domain and swallows the lot. Measured on the data-port path, 48 seeds, bursts
/// recovered at +12/+10/+8 dB: 39/7/0 with the full band, against 48/44/18 with the input low
/// passed. The genie column moved only 47 to 48 over the same span, which is the whole argument for
/// this class: <b>the gain is entirely in finding the burst, not in reading it.</b></para>
/// <para><b>So the decode path must not be filtered, and that is not a preference.</b> A filter
/// sharp enough to be worth having has an impulse response several times longer than the cyclic
/// prefix - hundreds of taps against a 64-sample prefix at this geometry - and anything longer
/// than the prefix puts inter-symbol interference into every symbol, which is exactly what the
/// prefix exists to prevent.
/// Filtering everything was tried and broke six tests for that reason. The search does not care:
/// it is looking for self-similarity, not orthogonality.</para>
/// <para>The filter is linear phase, so it delays by exactly half its length and the search's
/// answer is corrected by that amount to give a position in the unfiltered audio. Both the
/// whole-buffer search and the streaming one use this same class from a zeroed state, so they see
/// identical audio and agree on where a burst starts, which
/// <c>The_Incremental_Metric_Agrees_With_A_Direct_One_At_Every_Position</c> exists to enforce.</para>
/// </remarks>
internal sealed class SearchBandLimit
{
    private readonly float[] _taps;
    private readonly float[] _history;
    private int _at;

    internal SearchBandLimit(OfdmFmParameters parameters)
        : this(parameters, parameters.Geometry)
    {
    }

    /// <summary>A filter cut for a given layout: the acquisition geometry, on a station whose
    /// payload may be wider than the sync symbol it is looking for.</summary>
    internal SearchBandLimit(OfdmFmParameters parameters, OfdmFmGeometry geometry)
    {
        // Cut just above the highest occupied carrier of the layout the SYNC SYMBOL is on, with
        // two bins of margin so the filter is not starting to fall on a carrier that still has to
        // be correlated. On a station with a wide payload geometry that is deliberately narrower
        // than the payload: the search is looking for the sync symbol and nothing else, and
        // discriminator noise above the sync symbol's top carrier is noise the search need not
        // swallow.
        double top = (geometry.EndCarrier + 2)
            * (double)parameters.SampleRate / parameters.FftSize;
        double cutoff = Math.Min(top, parameters.SampleRate * 0.45);

        // Transition a fifth of the cutoff, tap count derived from it rather than fixed: a windowed
        // sinc's transition is about rate/taps, so a constant count would make this filter sharper
        // or blunter purely with the sample rate.
        int taps = (int)Math.Ceiling(4.0 * parameters.SampleRate / (cutoff * 0.2));
        taps = Math.Clamp(taps | 1, 15, 511);

        _taps = new float[taps];
        _history = new float[taps];
        int middle = (taps - 1) / 2;
        double omega = 2 * Math.PI * cutoff / parameters.SampleRate;
        double sum = 0;
        for (int n = 0; n < taps; n++)
        {
            int k = n - middle;
            double sinc = k == 0 ? omega : Math.Sin(omega * k) / k;
            double window = 0.54 - (0.46 * Math.Cos(2 * Math.PI * n / (taps - 1)));
            double v = sinc * window;
            _taps[n] = (float)v;
            sum += v;
        }

        for (int n = 0; n < taps; n++)
        {
            _taps[n] = (float)(_taps[n] / sum);
        }

        Delay = middle;
    }

    /// <summary>
    /// Samples of group delay. A burst starting at index <c>r</c> in the raw audio appears at
    /// <c>r + Delay</c> in the filtered audio, so a search answer must have this subtracted.
    /// </summary>
    internal int Delay { get; }

    /// <summary>Filters in place, carrying state across calls so a streaming caller sees no seam.
    /// </summary>
    internal void Process(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            _history[_at] = samples[i];
            double sum = 0;
            int h = _at;
            for (int t = 0; t < _taps.Length; t++)
            {
                sum += _taps[t] * _history[h];
                h = h == 0 ? _history.Length - 1 : h - 1;
            }

            _at = _at + 1 == _history.Length ? 0 : _at + 1;
            samples[i] = (float)sum;
        }
    }

    /// <summary>A filtered copy, from a fresh filter, leaving the caller's buffer alone.</summary>
    internal static float[] Once(
        OfdmFmParameters parameters, OfdmFmGeometry geometry, ReadOnlySpan<float> audio)
    {
        var filter = new SearchBandLimit(parameters, geometry);
        float[] copy = audio.ToArray();
        filter.Process(copy);
        return copy;
    }
}
