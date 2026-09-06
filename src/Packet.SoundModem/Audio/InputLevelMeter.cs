namespace Packet.SoundModem.Audio;

/// <summary>One interval's worth of receive level, in dBFS, as the operator page draws it.</summary>
/// <param name="PeakDbFs">The loudest sample in the interval, as dBFS: 1.0 is 0 dBFS.</param>
/// <param name="RmsDbFs">The interval's root-mean-square level, as dBFS.</param>
/// <param name="Clipped">Whether any sample in the interval reached full scale.</param>
/// <remarks>
/// Peak first because peak is what decides whether the card clips, and because a peak is what
/// this code base already states a level as: every modulator's key-down amplitude is a peak
/// (0.8), and <see cref="TestTone"/> says why - "the transmitter sees peaks". RMS travels with it
/// because the two answer different questions: the peak says whether a signal fits, and the RMS
/// with no signal on the channel says where the noise floor is sitting.
/// </remarks>
public readonly record struct InputLevel(double PeakDbFs, double RmsDbFs, bool Clipped);

/// <summary>
/// The peak and RMS of the audio arriving from the sound card, a few times a second, so that an
/// operator setting the capture gain can see what it is doing.
/// </summary>
/// <remarks>
/// <para><b>Why this exists</b> (Tom, 2026-09-06, watching the Mixer group on the bench Pi):
/// "Some assistance in setting the capture level would be useful. No way for the user to know
/// what good means." The slider was already bounded by the card's own dB range, which says what
/// can be asked for and nothing at all about what should be.</para>
/// <para><b>Where the numbers come from.</b> The target zone below is not a convention borrowed
/// from somewhere else; it is what this repository has already measured and written down, in four
/// places that agree:</para>
/// <list type="bullet">
/// <item><description><c>docs/ninotnc-loop.md</c>: the bench NinoTNC loop's "GOOD band" is a
/// received peak of 0.17 to 0.28 full scale, which is -15.4 to -11.1 dBFS, with a fixed capture
/// gain and the AGC off.</description></item>
/// <item><description><c>docs/cfo/evidence/2026-07-31-cfo-1-qpsk-differential</c>: recordings
/// exonerated at "peak 0.18-0.25", the same band from a different campaign.</description></item>
/// <item><description><c>docs/hardware/tm8100-cm108-interface-notes.md</c>: the interface is
/// designed for -12 dBFS at 60% of class deviation, which puts 100% of class at -7.6 dBFS and
/// clips the codec only at 240% of it.</description></item>
/// <item><description><c>docs/ms110d/evidence/2026-07-24-ota-c0</c>: a capture called
/// "comfortably adequate" at -11.7 dBFS peak, 12 dB of headroom, no clipped
/// samples.</description></item>
/// </list>
/// <para>So the zone is <see cref="TargetPeakLowDbFs"/> to <see cref="TargetPeakHighDbFs"/> on
/// received-signal peaks, widened a little either side of the measured band because a real
/// station's bursts vary and a zone the signal flickers out of teaches an operator to ignore it.
/// The two edges either side of it come from the same place: the only existing verdict on a
/// capture level in this tree is <c>Packet.SoundModem.NinoBench</c>, whose "TOO LOW" is 0.05
/// (-26 dBFS) and whose "CLIPPING" is 0.90 (-0.9 dBFS), and the house reserve for a receiver is
/// about 6 dB of headroom (<c>Packet.SoundModem.Ota.FlexIqTransmitter</c>).</para>
/// <para><b>Being under the zone is not a fault.</b> Every demodulator here is level-tolerant:
/// the AFSK discriminator power-normalises and is "barely touched" from -40 dBFS up, the PSK
/// detectors are scale-invariant by construction, and MS110D has an AGC of its own. Clipping is
/// the failure that actually costs decodes, so the meter's alarm is at the top and its advice at
/// the bottom is only advice.</para>
/// <para><b>Cost.</b> One pass over the block: an absolute value, a comparison and a
/// multiply-add per sample, no allocation, no LINQ. It is called from the audio thread and is not
/// thread-safe - one meter, one thread, which is what a receive tap is.</para>
/// </remarks>
public sealed class InputLevelMeter
{
    /// <summary>The quietest level reported, standing in for silence.</summary>
    /// <remarks>
    /// Digital silence is minus infinity dB, which is not a number JSON can carry and not a
    /// number a bar can be drawn at. A floor well under any real receiver's noise (the live 40 m
    /// station's quietest spectrum bins sit around -84 dBFS) says "nothing at all" without
    /// pretending to have measured it.
    /// </remarks>
    public const double FloorDbFs = -120;

    /// <summary>The bottom of the target zone for received-signal peaks.</summary>
    public const double TargetPeakLowDbFs = -18;

    /// <summary>The top of the target zone for received-signal peaks.</summary>
    public const double TargetPeakHighDbFs = -9;

    /// <summary>Below this, a signal is quieter than it needs to be; advice, not a fault.</summary>
    public const double QuietPeakDbFs = -30;

    /// <summary>Above this there is less headroom left than a receive path should keep.</summary>
    public const double HotPeakDbFs = -3;

    /// <summary>
    /// How often a reading is produced: five a second, which is fast enough to see a slider move
    /// and slow enough that the message is nothing on any link that carries a waterfall.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The magnitude at which a sample counts as having reached full scale.
    /// </summary>
    /// <remarks>
    /// <see cref="Pcm16.ToFloat"/> divides by 32768, so the largest positive 16-bit code arrives
    /// as 32767/32768 and the largest negative one as exactly -1.0. Comparing against 1.0 would
    /// therefore never see a clipped positive half-cycle.
    /// </remarks>
    public const float ClipMagnitude = 32767f / 32768f;

    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private long _startedAt;
    private float _peak;
    private double _sumSquares;
    private long _count;
    private bool _clipped;

    /// <summary>Creates a meter.</summary>
    /// <param name="clock">The clock the interval is measured on.</param>
    /// <param name="interval">How often a reading is produced; null takes
    /// <see cref="DefaultInterval"/>.</param>
    public InputLevelMeter(TimeProvider? clock = null, TimeSpan? interval = null)
    {
        _clock = clock ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        _startedAt = _clock.GetTimestamp();
    }

    /// <summary>How often <see cref="TryTake"/> will produce a reading.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>Takes one block of audio into the current interval.</summary>
    /// <param name="samples">The block, nominally -1 to +1.</param>
    public void Add(ReadOnlySpan<float> samples)
    {
        float peak = _peak;
        double sum = _sumSquares;
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }

            sum += (double)sample * sample;
        }

        _peak = peak;
        _sumSquares = sum;
        _count += samples.Length;
        _clipped |= peak >= ClipMagnitude;
    }

    /// <summary>
    /// The reading for the interval just ended, if one has ended and there was anything in it.
    /// </summary>
    /// <param name="level">What the interval measured.</param>
    /// <returns>False while the interval is still running, or when no audio arrived in it at all -
    /// a station whose card has stopped delivering has nothing to report, and reporting the floor
    /// would draw a bar that looks like a measurement of silence rather than an absence of one.
    /// </returns>
    public bool TryTake(out InputLevel level)
    {
        level = default;
        long now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_startedAt, now) < _interval)
        {
            return false;
        }

        _startedAt = now;
        if (_count == 0)
        {
            return false;
        }

        level = new InputLevel(DbFs(_peak), DbFs(Math.Sqrt(_sumSquares / _count)), _clipped);
        _peak = 0;
        _sumSquares = 0;
        _count = 0;
        _clipped = false;
        return true;
    }

    /// <summary>
    /// Throws away whatever has been accumulated and restarts the interval.
    /// </summary>
    /// <remarks>
    /// For the moment nobody is watching any more: a half-interval kept across a gap of minutes
    /// would be reported as the first reading the next viewer sees, and it would be a peak from
    /// whenever the last one left.
    /// </remarks>
    public void Reset()
    {
        _startedAt = _clock.GetTimestamp();
        _peak = 0;
        _sumSquares = 0;
        _count = 0;
        _clipped = false;
    }

    /// <summary>A magnitude on the 0 to 1 full-scale convention, in dBFS.</summary>
    /// <param name="magnitude">The magnitude; 1.0 is 0 dBFS.</param>
    /// <returns>The level in dBFS, never below <see cref="FloorDbFs"/>. Values above 0 are not
    /// clamped: a resampler can overshoot a clipped waveform, and saying so is more use than
    /// hiding it.</returns>
    public static double DbFs(double magnitude) => magnitude <= 0
        ? FloorDbFs
        : Math.Max(FloorDbFs, 20 * Math.Log10(magnitude));
}
