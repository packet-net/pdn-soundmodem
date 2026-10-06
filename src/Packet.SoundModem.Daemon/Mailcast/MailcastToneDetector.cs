using M0LTE.Dsp;

namespace Packet.SoundModem.Daemon;

/// <summary>What was measured of a slot's opening tone.</summary>
/// <param name="FrequencyHz">The tone's audio frequency.</param>
/// <param name="OffsetHz">How far that is from where it should be; positive is high.</param>
/// <param name="SnrDb">Tone power over the noise in 3 kHz, the bandwidth MS110D's figures use.</param>
/// <param name="Duration">How long it lasted.</param>
internal sealed record MailcastTone(double FrequencyHz, double OffsetHz, double SnrDb, TimeSpan Duration);

/// <summary>
/// Finds the 10 s steady tone GB7RDG opens each slot with, and measures its frequency and
/// signal-to-noise ratio.
/// </summary>
/// <remarks>
/// <para>The same method as pdn-mailcast's receiver (its ToneDetector), written again here
/// because that one is not in a library: the audio is brought down to 8 kHz (more where the tone
/// sits high in a wide passband) and cut into blocks of about a second, so the FFT's bins are
/// under 1 Hz apart. A block holds the tone when the strongest bin within 100 Hz of where it
/// should be stands 15 dB over the noise (the median bin across the 3 kHz around it) and holds
/// nearly all the power within
/// 25 Hz of it. That last test is what tells the tone from the CW ident that follows it on the
/// same frequency: keying spreads power into sidebands, a steady carrier does not. A run of such
/// blocks within 2 Hz of each other, lasting 7 to 20 s, is reported when it ends.</para>
/// <para>Time is counted in samples, so nothing depends on how fast audio arrives. Not
/// thread-safe; fed from the receive thread. No allocation per block.</para>
/// </remarks>
internal sealed class MailcastToneDetector
{
    private const double SearchHz = 100;
    private const double DetectDb = 15;
    private const double StableHz = 2;
    private const double SteadyShare = 0.87;
    private const int LineBins = 3;
    private const int ExcludeBins = 20;
    private const int NearBins = 26;
    private static readonly TimeSpan Shortest = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(20);

    private readonly int _rate;
    private readonly int _size;
    private readonly double _binHz;
    private readonly double _noiseLowHz;
    private readonly double _noiseHighHz;
    private readonly double _expectedHz;
    private readonly Decimator? _decimator;
    private readonly float[] _decimated;
    private readonly float[] _block;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _window;
    private readonly double[] _power;
    private readonly double[] _noise;
    private readonly int _chunk;
    private int _filled;
    private int _runBlocks;
    private double _runHz;
    private double _runSignal;
    private double _runNoise;

    /// <summary>A detector for audio at <paramref name="sampleRate"/> (a multiple of 8000), the
    /// tone expected at <paramref name="expectedHz"/>.</summary>
    internal MailcastToneDetector(int sampleRate, double expectedHz)
    {
        if (sampleRate % 8000 != 0)
        {
            throw new ArgumentException("the sample rate must be a multiple of 8000", nameof(sampleRate));
        }

        // The lowest rate that still has the tone and 1.5 kHz of noise either side of it below
        // Nyquist, and a power-of-two block of about a second at that rate.
        _rate = new[] { 8000, 16000, 24000, 48000 }.FirstOrDefault(
            r => sampleRate % r == 0 && r / 2.0 > expectedHz + 1700, sampleRate);
        _size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)_rate);
        _binHz = (double)_rate / _size;
        _noiseLowHz = Math.Max(100, expectedHz - 1500);
        _noiseHighHz = Math.Min((_rate / 2.0) - 100, expectedHz + 1500);
        _expectedHz = expectedHz;
        _decimator = _rate == sampleRate ? null : new Decimator(sampleRate, sampleRate / _rate);
        _chunk = sampleRate / 2;
        _decimated = new float[_decimator?.MaxOutput(_chunk) ?? _chunk];
        _block = new float[_size];
        _re = new float[_size];
        _im = new float[_size];
        _window = new float[_size];
        _power = new double[_size / 2];
        for (int i = 0; i < _size; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _size)));
        }

        _noise = new double[(int)((_noiseHighHz - _noiseLowHz) / _binHz) + 1];
    }

    /// <summary>Raised on the feeding thread when a tone has ended.</summary>
    internal event Action<MailcastTone>? Measured;

    /// <summary>Feeds audio.</summary>
    internal void Process(ReadOnlySpan<float> samples)
    {
        for (int offset = 0; offset < samples.Length;)
        {
            int take = Math.Min(samples.Length - offset, _chunk);
            int produced;
            if (_decimator is null)
            {
                samples.Slice(offset, take).CopyTo(_decimated);
                produced = take;
            }
            else
            {
                produced = _decimator.Process(samples.Slice(offset, take), _decimated);
            }

            offset += take;
            for (int i = 0; i < produced; i++)
            {
                _block[_filled++] = _decimated[i];
                if (_filled == _size)
                {
                    _filled = 0;
                    Analyse();
                }
            }
        }
    }

    /// <summary>Forgets any run in progress, without reporting it: the audio is about to be
    /// something else (a rig retuned).</summary>
    internal void Reset()
    {
        _runBlocks = 0;
        _filled = 0;
    }

    private void Analyse()
    {
        for (int i = 0; i < _size; i++)
        {
            _re[i] = _block[i] * _window[i];
            _im[i] = 0;
        }

        Fft.Forward(_re, _im);
        for (int k = 0; k < _power.Length; k++)
        {
            _power[k] = ((double)_re[k] * _re[k]) + ((double)_im[k] * _im[k]);
        }

        int peak = Bin(_expectedHz - SearchHz);
        for (int k = peak + 1; k <= Bin(_expectedHz + SearchHz); k++)
        {
            if (_power[k] > _power[peak])
            {
                peak = k;
            }
        }

        // Noise per bin: the median across the passband away from the peak, as a mean (noise
        // power in a bin is exponential, whose median is ln 2 of its mean).
        int n = 0;
        for (int k = Bin(_noiseLowHz); k <= Bin(_noiseHighHz) && n < _noise.Length; k++)
        {
            if (Math.Abs(k - peak) > ExcludeBins)
            {
                _noise[n++] = _power[k];
            }
        }

        Array.Sort(_noise, 0, n);
        double noise = Math.Max(_noise[n / 2] / Math.Log(2), double.Epsilon);
        double line = 0;
        for (int k = peak - LineBins; k <= peak + LineBins; k++)
        {
            line += _power[k] - noise;
        }

        double near = 0;
        for (int k = peak - NearBins; k <= peak + NearBins; k++)
        {
            near += _power[k] - noise;
        }

        bool tone = line > noise * Math.Pow(10, DetectDb / 10) && (near <= 0 || line >= SteadyShare * near);
        double hz = Refine(peak);
        if (tone && (_runBlocks == 0 || Math.Abs(hz - (_runHz / _runBlocks)) <= StableHz))
        {
            _runBlocks++;
            _runHz += hz;
            _runSignal += line;
            _runNoise += noise;
            return;
        }

        EndRun();
        if (tone)
        {
            _runBlocks = 1;
            _runHz = hz;
            _runSignal = line;
            _runNoise = noise;
        }
    }

    private void EndRun()
    {
        if (_runBlocks > 0)
        {
            var duration = TimeSpan.FromSeconds(_runBlocks * _size / (double)_rate);
            if (duration >= Shortest && duration <= Longest)
            {
                double hz = _runHz / _runBlocks;
                double noiseIn3k = _runNoise / _runBlocks * (3000 / _binHz);
                double snr = 10 * Math.Log10(Math.Max(_runSignal / _runBlocks, double.Epsilon) / noiseIn3k);
                Measured?.Invoke(new MailcastTone(hz, hz - _expectedHz, snr, duration));
            }
        }

        _runBlocks = 0;
        _runHz = _runSignal = _runNoise = 0;
    }

    /// <summary>The peak's frequency between bins, from a parabola through the log powers.</summary>
    private double Refine(int peak)
    {
        double a = Math.Log(Math.Max(_power[peak - 1], double.Epsilon));
        double b = Math.Log(Math.Max(_power[peak], double.Epsilon));
        double c = Math.Log(Math.Max(_power[peak + 1], double.Epsilon));
        double denominator = a - (2 * b) + c;
        double shift = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
        return (peak + Math.Clamp(shift, -0.5, 0.5)) * _binHz;
    }

    private int Bin(double hz) => (int)Math.Round(hz / _binHz);
}
