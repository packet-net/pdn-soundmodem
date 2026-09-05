namespace Packet.SoundModem.Audio;

/// <summary>
/// The audio for a transmitter test: the classic SSB two-tone pair, or one tone on its own for a
/// carrier-level or FM deviation check.
/// </summary>
/// <remarks>
/// <para><b>What the two tones are for.</b> Two equal tones through an SSB transmitter make an
/// envelope that swings between zero and twice one tone's amplitude, which is the standard way to
/// read ALC action, peak power and intermodulation off a radio or a monitor receiver: the
/// third-order products land 3f2-2f1 and 3f1-2f2 either side of the pair, where nothing else is,
/// so how far down they sit is a direct measurement. 700 and 1900 Hz is the usual pair - wide
/// enough apart that the products fall inside the passband where they can be seen, and both well
/// inside any SSB filter.</para>
/// <para><b>Level.</b> The tones are equal, and each is given half of
/// <see cref="PeakAmplitude"/>, so their sum peaks at exactly the same amplitude a single tone
/// would - and so the peak envelope drive of a two-tone test equals the key-down drive of the
/// carrier check beside it, which is what makes the two readings comparable. That is also why
/// this is stated as a peak rather than as a per-tone level: the transmitter sees peaks.</para>
/// <para><b>Edges are shaped</b>, for the reason
/// <see cref="Ident.MorseGenerator"/> shapes its own: hard-keying a tone splatters, because the
/// transform of a rectangular envelope decays at only 6 dB/octave. A raised-cosine rise and fall
/// of a few milliseconds costs nothing, and a test transmission whose own edges are dirty is a
/// poor instrument for measuring a transmitter's cleanliness.</para>
/// <para><b>Rendered in blocks, with continuous phase.</b> <see cref="Next"/> may be called as
/// many times as the caller likes; the oscillators keep their phase across calls, so a burst
/// assembled out of several blocks is one unbroken signal. <see cref="Render"/> is the
/// whole-burst convenience over it.</para>
/// </remarks>
public sealed class TestTone
{
    /// <summary>The low tone of the standard two-tone pair, in Hz.</summary>
    public const double TwoToneLowHz = 700;

    /// <summary>The high tone of the standard two-tone pair, in Hz.</summary>
    public const double TwoToneHighHz = 1900;

    /// <summary>
    /// The modulation index at which an FM carrier vanishes: the first zero of the Bessel
    /// function J0. Deviation = this x the modulating tone's frequency, so a single tone of a
    /// known frequency turns "the carrier has disappeared on the spectrum display" into a
    /// calibrated deviation.
    /// </summary>
    /// <remarks>The exact first zero is 2.404826; 2.405 is the figure the amateur literature and
    /// the roadmap entry both quote, and the difference is 0.4 Hz of deviation at 2 kHz - far
    /// inside the accuracy of reading a null off a panadapter.</remarks>
    public const double BesselNullIndex = 2.405;

    /// <summary>
    /// Rise and fall time in seconds. 5 ms, the usual click-free compromise, and the same figure
    /// <see cref="Ident.MorseGenerator"/> uses.
    /// </summary>
    public const double EdgeSeconds = 0.005;

    private readonly double[] _toneHz;
    private readonly double[] _phase;
    private readonly double _perTone;
    private readonly int _sampleRate;
    private readonly int _edge;
    private readonly Lock _gate = new();
    private int _target;
    private int _produced;

    /// <summary>Builds the generator for one test transmission.</summary>
    /// <param name="toneHz">The tones to send together. One for a carrier or deviation check,
    /// two for a linearity check.</param>
    /// <param name="peakAmplitude">What the sum of the tones peaks at, 0 to 1. The transmit level
    /// the station's data goes out at, so the test measures what a frame gets.</param>
    /// <param name="sampleRate">The channel's audio rate.</param>
    /// <param name="seconds">How long the burst lasts.</param>
    public TestTone(IReadOnlyList<double> toneHz, double peakAmplitude, int sampleRate, double seconds)
    {
        ArgumentNullException.ThrowIfNull(toneHz);
        if (toneHz.Count == 0)
        {
            throw new ArgumentException("a test transmission needs at least one tone", nameof(toneHz));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(peakAmplitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(peakAmplitude, 1.0);
        foreach (double hz in toneHz)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz);
            if (hz >= sampleRate / 2.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(toneHz), hz,
                    $"a test tone must be below the {sampleRate / 2.0:F0} Hz Nyquist of a "
                    + $"{sampleRate} Hz channel");
            }
        }

        _toneHz = [.. toneHz];
        _phase = new double[_toneHz.Length];
        _sampleRate = sampleRate;
        PeakAmplitude = peakAmplitude;

        // Equal tones, and their sum peaks at the stated amplitude - see the class remarks.
        _perTone = peakAmplitude / _toneHz.Length;
        _target = checked((int)Math.Round(seconds * sampleRate));
        if (_target <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds), seconds, $"too short to render a sample at {sampleRate} Hz");
        }

        // The rise and the fall have to fit inside the burst with something between them; a burst
        // shorter than two edges is all edge, and shaping it would only halve its amplitude.
        _edge = Math.Min((int)Math.Round(EdgeSeconds * sampleRate), _target / 2);
    }

    /// <summary>The tones being sent, in the order they were given.</summary>
    public IReadOnlyList<double> Tones => _toneHz;

    /// <summary>What the sum of the tones peaks at.</summary>
    public double PeakAmplitude { get; }

    /// <summary>How long the burst still has to run, in samples, from where it has got to.</summary>
    public int Remaining
    {
        get
        {
            lock (_gate)
            {
                return _target - _produced;
            }
        }
    }

    /// <summary>Everything rendered so far, in samples.</summary>
    public int Produced
    {
        get
        {
            lock (_gate)
            {
                return _produced;
            }
        }
    }

    /// <summary>Whether the whole burst has been rendered.</summary>
    public bool Complete => Remaining <= 0;

    /// <summary>
    /// Ends the burst early: it runs on only for as long as the fall takes, so a cancelled test
    /// stops without a hard edge, and then <see cref="Next"/> returns nothing.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _target = Math.Min(_target, _produced + _edge);
        }
    }

    /// <summary>
    /// The next block of the burst, up to <paramref name="maxSamples"/> long. Empty once the
    /// burst has finished. Phase carries across calls, so consecutive blocks join seamlessly.
    /// </summary>
    public float[] Next(int maxSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSamples);
        lock (_gate)
        {
            int n = Math.Min(maxSamples, _target - _produced);
            if (n <= 0)
            {
                return [];
            }

            var block = new float[n];
            for (int t = 0; t < _toneHz.Length; t++)
            {
                double step = 2.0 * Math.PI * _toneHz[t] / _sampleRate;
                double phase = _phase[t];
                for (int k = 0; k < n; k++)
                {
                    block[k] += (float)(_perTone * Math.Cos(phase));
                    phase += step;
                    if (phase > Math.PI)
                    {
                        phase -= 2.0 * Math.PI;
                    }
                }

                _phase[t] = phase;
            }

            for (int k = 0; k < n; k++)
            {
                block[k] = (float)(block[k] * Envelope(_produced + k));
            }

            _produced += n;
            return block;
        }
    }

    /// <summary>The whole burst in one array - what a single-keyup transmission sends.</summary>
    public float[] Render()
    {
        int remaining = Remaining;
        return remaining <= 0 ? [] : Next(remaining);
    }

    /// <summary>
    /// The raised-cosine rise and fall, as a factor on the sample at <paramref name="at"/>.
    /// </summary>
    /// <remarks>Read against the target rather than a stored envelope, so that a
    /// <see cref="Stop"/> arriving mid-burst moves the fall to where the burst now ends.</remarks>
    private double Envelope(int at)
    {
        if (_edge <= 0)
        {
            return 1.0;
        }

        double rise = at < _edge ? 0.5 * (1.0 - Math.Cos(Math.PI * at / _edge)) : 1.0;
        int fromEnd = _target - 1 - at;
        double fall = fromEnd < _edge ? 0.5 * (1.0 - Math.Cos(Math.PI * fromEnd / _edge)) : 1.0;

        // The smaller of the two, so that a Stop landing inside the rise cuts the burst short
        // without ever letting it reach full amplitude and stop dead - which is the click both
        // edges exist to avoid, arriving by the back door.
        return Math.Min(rise, fall);
    }

    /// <summary>
    /// The FM deviation a single tone drives the carrier to its Bessel null at: 2.405 x the
    /// tone, in Hz. Raise the transmit audio until the carrier disappears on a spectrum display
    /// and the level at that point is calibrated for this deviation.
    /// </summary>
    public static double BesselNullDeviationHz(double toneHz) => BesselNullIndex * toneHz;

    /// <summary>
    /// The four tones worth having a preset for, each with the deviation its null calibrates:
    /// 500 Hz -> 1.2 kHz, 999 Hz -> 2.4 kHz, 1248 Hz -> 3.0 kHz, 2079 Hz -> 5.0 kHz.
    /// </summary>
    /// <remarks>
    /// The tones are the constants and the deviations are calculated from them, so the pairs
    /// cannot drift apart. They cover the deviations an FM packet station actually wants to set:
    /// 2.5 to 3 kHz on a 12.5 kHz channel, 5 kHz on a 25 kHz one, and the two lower figures for
    /// checking the low end of the range the same radio has to be linear over.
    /// </remarks>
    public static IReadOnlyList<double> BesselNullTonesHz { get; } = [500, 999, 1248, 2079];
}
