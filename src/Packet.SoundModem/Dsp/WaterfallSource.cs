using M0LTE.Dsp;

namespace Packet.SoundModem.Dsp;

/// <summary>
/// Produces display-rate waterfall lines from an audio stream: overlapping Hann-windowed
/// FFTs at a fixed hop of <c>sampleRate / linesPerSecond</c> samples, one dB-scaled byte
/// per bin covering 0 Hz to half the sample rate. Where <see cref="SpectrumSource"/> emits
/// a line per whole FFT frame (~3/s at 12 kHz — sized for a low-rate telemetry feed), this
/// source overlaps transforms so the line rate is a smooth display rate (30/s by default)
/// independent of the FFT length, which stays long for frequency resolution: 2048 points at
/// 12 kHz, 8192 at 48 kHz — ~5.9 Hz/bin either way. ~30 kB/s per channel at 12 kHz.
/// </summary>
/// <remarks>
/// The dB scale is absolute, not auto-ranged: a full-scale sine peaks at 0 dBFS = 255 and
/// the byte floor sits at <see cref="FloorDb"/>, so consumers can convert bytes back to
/// dBFS (and compare power across lines — what a per-burst SNR readout needs). Zero
/// steady-state allocation: the line buffer is reused, consumers must copy if they keep it.
/// </remarks>
public sealed class WaterfallSource
{
    /// <summary>dBFS mapped to byte 0; 0 dBFS (a full-scale sine's peak bin) maps to 255.</summary>
    public const double FloorDb = -100;

    private readonly Action<long, ReadOnlyMemory<byte>> _lineSink;
    private readonly float[] _ring;
    private readonly float[] _window;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly byte[] _line;
    private readonly int _fftSize;
    private readonly int _hop;
    private readonly float _magnitudeScale;
    private int _writeIndex;
    private int _untilNextLine;
    private long _lineIndex;

    /// <summary>Creates a source delivering waterfall lines to <paramref name="lineSink"/>.</summary>
    /// <param name="sampleRate">Input sample rate (fixes <see cref="BinWidthHz"/>).</param>
    /// <param name="lineSink">Receives (line index, line) once per hop — <see cref="LineLength"/>
    /// bytes, bin 0 = DC. The buffer is reused; consumers must copy if they keep it.</param>
    /// <param name="linesPerSecond">Display line rate; the hop is
    /// <c>sampleRate / linesPerSecond</c> samples (must divide evenly).</param>
    /// <param name="fftSize">Transform length (power of two, ≥ the hop). 0 picks the default
    /// for the rate: 2048 below 24 kHz, 8192 at and above — ~5.9 Hz/bin at both DSP rates.</param>
    public WaterfallSource(
        int sampleRate,
        Action<long, ReadOnlyMemory<byte>> lineSink,
        int linesPerSecond = 30,
        int fftSize = 0)
    {
        ArgumentNullException.ThrowIfNull(lineSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(linesPerSecond, 0);
        if (sampleRate % linesPerSecond != 0)
        {
            throw new ArgumentException(
                $"lines-per-second {linesPerSecond} must divide the sample rate {sampleRate}",
                nameof(linesPerSecond));
        }

        if (fftSize == 0)
        {
            fftSize = sampleRate >= 24000 ? 8192 : 2048;
        }

        if (!int.IsPow2(fftSize))
        {
            throw new ArgumentException($"fftSize {fftSize} is not a power of two", nameof(fftSize));
        }

        _hop = sampleRate / linesPerSecond;
        if (fftSize < _hop)
        {
            throw new ArgumentException(
                $"fftSize {fftSize} shorter than the hop {_hop} would skip samples", nameof(fftSize));
        }

        SampleRate = sampleRate;
        LinesPerSecond = linesPerSecond;
        _lineSink = lineSink;
        _fftSize = fftSize;
        _ring = new float[fftSize];
        _re = new float[fftSize];
        _im = new float[fftSize];
        _line = new byte[fftSize / 2];
        _window = new float[fftSize];
        double windowSum = 0;
        for (int n = 0; n < fftSize; n++)
        {
            _window[n] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * n / fftSize)));
            windowSum += _window[n];
        }

        // A unit-amplitude sine lands half the window's DC gain in its bin (the other half
        // is at the negative frequency), so this scale makes that bin magnitude 1 = 0 dBFS.
        _magnitudeScale = (float)(2 / windowSum);
        _untilNextLine = _hop;
    }

    /// <summary>The sample rate the source was created for.</summary>
    public int SampleRate { get; }

    /// <summary>Lines emitted per second of input audio.</summary>
    public int LinesPerSecond { get; }

    /// <summary>Bytes per emitted line (half the FFT size; bin 0 = DC).</summary>
    public int LineLength => _fftSize / 2;

    /// <summary>Width of one bin in hertz.</summary>
    public double BinWidthHz => (double)SampleRate / _fftSize;

    /// <summary>The index the next emitted line will carry (also the count emitted so far).</summary>
    public long NextLineIndex => _lineIndex;

    /// <summary>Feeds audio; emits one line per hop of samples.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _ring[_writeIndex] = sample;
            _writeIndex = (_writeIndex + 1) & (_fftSize - 1);
            if (--_untilNextLine == 0)
            {
                _untilNextLine = _hop;
                EmitLine();
            }
        }
    }

    private void EmitLine()
    {
        // The ring holds the last fftSize samples ending just before _writeIndex; unroll it
        // into the FFT buffer oldest-first with the window applied.
        int tail = _fftSize - _writeIndex;
        for (int n = 0; n < tail; n++)
        {
            _re[n] = _ring[_writeIndex + n] * _window[n];
        }

        for (int n = 0; n < _writeIndex; n++)
        {
            _re[tail + n] = _ring[n] * _window[tail + n];
        }

        Array.Clear(_im);
        Fft.Forward(_re, _im);

        for (int bin = 0; bin < _line.Length; bin++)
        {
            float re = _re[bin] * _magnitudeScale;
            float im = _im[bin] * _magnitudeScale;
            double db = 10 * Math.Log10(re * re + im * im + 1e-30);
            _line[bin] = (byte)Math.Clamp((db - FloorDb) * (255 / -FloorDb), 0, 255);
        }

        _lineSink(_lineIndex++, _line);
    }
}
