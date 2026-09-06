namespace Packet.SoundModem.Audio;

/// <summary>One interval's worth of receive level, in dBFS, as the operator page draws it.</summary>
/// <param name="PeakDbFs">The loudest sample in the interval, as dBFS: 1.0 is 0 dBFS, and
/// nothing reads above that or below <see cref="InputLevelMeter.FloorDbFs"/>.</param>
/// <param name="RmsDbFs">The interval's root-mean-square level, as dBFS, on the same scale.</param>
/// <param name="Clipped">Whether any sample the card delivered in the interval was at the top or
/// the bottom code of the converter's range.</param>
/// <remarks>
/// <para>Peak first because peak is what decides whether the card clips, and because a peak is
/// what this code base already states a level as: every modulator's key-down amplitude is a peak
/// (0.8), and <see cref="TestTone"/> says why - "the transmitter sees peaks". RMS travels with it
/// because the two answer different questions: the peak says whether a signal fits, and the RMS
/// with no signal on the channel says where the noise floor is sitting.</para>
/// <para><b>The two halves are measured in different places, deliberately.</b> The peak and the
/// RMS come from the audio the modems get, which on a 48 kHz card has been through the decimating
/// FIR; that costs a fraction of a dB and buys a free hook. <see cref="Clipped"/> cannot be
/// measured there at all: the filter's ripple moves peaks either way, so a signal with real
/// headroom at the converter can leave the decimator at 0.99997 and a genuinely railed one can
/// leave it lower. It is counted on the card's own samples instead, before the decimator, which
/// is the only place "the converter ran out of codes" is a fact rather than an inference.</para>
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
/// (-26 dBFS) and whose "CLIPPING" is 0.90 (-0.9 dBFS), and 6 dB is the headroom figure this
/// tree already works to elsewhere.</para>
/// <para><b>Being under the zone is not a fault.</b> Every demodulator here is level-tolerant:
/// the AFSK discriminator power-normalises and is "barely touched" from -40 dBFS up, the PSK
/// detectors are scale-invariant by construction, and MS110D has an AGC of its own. Clipping is
/// the failure that actually costs decodes, so the meter's alarm is at the top and its advice at
/// the bottom is only advice.</para>
/// <para><b>Cost.</b> One pass over the block: an absolute value, a comparison and a
/// multiply-add per sample, no allocation, no LINQ, plus one compare per card-rate sample in
/// <see cref="AddCardSamples"/>. Both are called from the audio thread and neither is
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
    /// At or above this - or with the card clipped - one frame's own peak is called too loud.
    /// </summary>
    /// <remarks>
    /// The same edge as <see cref="HotPeakDbFs"/>, which is where the meter's bar turns red, and
    /// written as that constant rather than as another -3 so the badge on a frame row and the
    /// colour on the bar can never come to say different things. Here rather than beside the
    /// panel that draws it because the verdict is the daemon's: the page is told <c>loud</c> or
    /// <c>quiet</c>, and keeps its own copy of these two numbers only to word the explanation
    /// (pinned by <c>The_Pages_Frame_Level_Thresholds_Are_The_Daemons</c>).
    /// </remarks>
    public const double FrameLoudPeakDbFs = HotPeakDbFs;

    /// <summary>Below this, one frame's own peak is called too quiet.</summary>
    /// <remarks>
    /// Six dB under the bottom of the target zone, which is the headroom figure this tree already
    /// works to and one S-point-ish step: a frame a little under the zone is not worth a badge -
    /// every demodulator here is level-tolerant and being quiet costs nothing until it is very
    /// quiet - but a frame this far under says the capture gain, or the far station, has
    /// something wrong with it. Deliberately not <see cref="QuietPeakDbFs"/>, the meter's own
    /// grey edge: that one is about a bar with nothing on the channel, and a frame is a signal.
    /// </remarks>
    public const double FrameQuietPeakDbFs = TargetPeakLowDbFs - 6;

    /// <summary>
    /// How often a reading is produced: five a second, which is fast enough to see a slider move
    /// and slow enough that the message is nothing on any link that carries a waterfall.
    /// </summary>
    /// <remarks>
    /// A ceiling as well as a target. The boundary is only tested when an audio block arrives, so
    /// a station whose blocks are longer than this gets one reading per block instead: the packet
    /// stations read 100 ms at a time and ARDOP 20 ms, so both get the five.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The top code of a 16-bit converter, as a float: <c>short.MaxValue</c>.</summary>
    /// <remarks>
    /// <see cref="Pcm16.ToFloat"/> divides by 32768, so the largest positive code arrives as
    /// 32767/32768 and not as 1.0. Comparing against 1.0 would never see a clipped positive
    /// half-cycle at all.
    /// </remarks>
    public const float TopCode = 32767f / 32768f;

    /// <summary>The bottom code, as a float: <c>short.MinValue</c>, which is exactly -1.0.</summary>
    public const float BottomCode = -1f;

    /// <summary>
    /// Whether one sample from the card is sitting on a rail.
    /// </summary>
    /// <remarks>
    /// <para><b>Exactly the two end codes, nothing near them.</b> A converter that runs out of
    /// range does not produce a value close to the rail, it produces the rail, usually several
    /// samples in a row; a signal that merely comes close is a signal with less headroom than you
    /// wanted, which is what the meter's red band above -3 dBFS is for. Widening this to "within
    /// a code or two" would only make the pill fire on loud-but-clean audio, which is the fault
    /// this test exists to avoid.</para>
    /// <para>Judged on the card's own samples, before any resampling - see
    /// <see cref="AddCardSamples"/>.</para>
    /// </remarks>
    /// <param name="sample">One sample as the device delivered it, nominally -1 to +1.</param>
    public static bool IsClipped(float sample) => sample >= TopCode || sample <= BottomCode;

    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;

    /// <summary>The interval in the clock's own timestamp units, so the pacing is integer maths.</summary>
    private readonly long _intervalStamps;

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
        _intervalStamps = Math.Max(
            1, (long)Math.Round(_interval.TotalSeconds * _clock.TimestampFrequency));
        _startedAt = _clock.GetTimestamp();
    }

    /// <summary>How often <see cref="TryTake"/> will produce a reading.</summary>
    public TimeSpan Interval => _interval;

    /// <summary>
    /// Takes one block of the audio the modems hear into the current interval, for the peak and
    /// the RMS.
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about clipping: on a 48 kHz card these samples have been through
    /// the decimating FIR, whose ripple moves peaks either way, so "this reached full scale" is
    /// not a question they can answer. <see cref="AddCardSamples"/> answers it.
    /// </remarks>
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
    }

    /// <summary>
    /// Takes one block of audio exactly as the card delivered it, for the clip indicator alone.
    /// </summary>
    /// <remarks>
    /// <para>Called before any resampling, which is the whole point of it existing separately
    /// from <see cref="Add"/>. On the bench CM108 the decimator's output ran up to about 1.3 dB
    /// above the card's own peak, so a clip test on the decimated audio lit the indicator on
    /// signals with real headroom and reported levels past the top of the scale (radio1,
    /// 2026-09-06). The converter's rails are a fact about the card, and this is where they
    /// are.</para>
    /// <para>It contributes nothing to the peak or the RMS. Doing both here would mean two
    /// passes over the 48 kHz block instead of one over the 12 kHz one, for a fraction of a dB.
    /// </para>
    /// </remarks>
    /// <param name="samples">The block as the device delivered it, at the card's own rate.</param>
    public void AddCardSamples(ReadOnlySpan<float> samples)
    {
        if (_clipped)
        {
            // Already latched for this interval; nothing another sample can add.
            return;
        }

        foreach (float sample in samples)
        {
            if (IsClipped(sample))
            {
                _clipped = true;
                return;
            }
        }
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
        long elapsed = now - _startedAt;
        if (elapsed < _intervalStamps)
        {
            return false;
        }

        // Advanced by whole intervals, not set to now. Set to now, the interval restarts from the
        // moment of the take rather than from the boundary it crossed, and the boundary is only
        // ever tested when an audio block arrives - so with 100 ms blocks and a 200 ms interval,
        // one block a hair under 100 ms puts the next test at 199.x ms, skips it, and locks the
        // meter at 300 ms spacing for ever. That is what the bench measured: 3.4 messages a
        // second against a documented five (radio1, 2026-09-06). Catching up in whole intervals
        // has no such ratchet, and a station that was not watched for an hour resumes on the next
        // boundary rather than firing an hour's worth.
        _startedAt += elapsed / _intervalStamps * _intervalStamps;
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
    /// <returns>The level in dBFS, never below <see cref="FloorDbFs"/> and never above 0.</returns>
    /// <remarks>
    /// <b>Clamped at both ends.</b> 0 dBFS is the top of the scale by definition, and a reading
    /// above it is not a measurement of anything a converter can deliver - it is the decimating
    /// FIR's ripple, which put "+0.8 dBFS" on the bench Pi's meter (radio1, 2026-09-06) and made
    /// an operator-facing number say the impossible. The bottom is clamped because digital
    /// silence is minus infinity, which JSON cannot carry and a bar cannot be drawn at.
    /// </remarks>
    public static double DbFs(double magnitude) => magnitude <= 0
        ? FloorDbFs
        : Math.Clamp(20 * Math.Log10(magnitude), FloorDbFs, 0);
}
