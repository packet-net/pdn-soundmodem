namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// The OFDM-FM carrier layouts this repository ships, and the modes the catalogue builds from
/// them.
/// </summary>
/// <remarks>
/// <para><b>Written natively at 48 kHz</b>, which is the rate a pdn-soundmodem channel actually
/// runs at, so nothing has to be rescaled before a burst is rendered. <see
/// cref="OfdmFmParameters.Rescaled"/> still exists and still works, because a profile supplied by
/// an operator may be written at some other rate, but no preset here needs it.</para>
/// <para><b>All eight share one geometry table, so a station hears every span.</b> There are three
/// distinct carrier layouts here and each has a <see cref="OfdmFmParameters.GeometryId"/>: 0 is
/// <see cref="Narrow"/>, 1 is the 6 kHz span, 2 is the 8 kHz span. Rate variants of one span share
/// its id, because they share its layout. Every burst names its payload layout by id in its header,
/// and its sync, preamble and header always go out on entry 0, so a receiver configured for any
/// preset in this table can decode a burst sent on any other. That is what lets a link change
/// bandwidth without a handshake, and bandwidth is worth more on this path than constellation or
/// code rate.</para>
/// <para><b>What to know about entry 0.</b> It carries the sync, preamble and header of every
/// burst, whatever the payload's span, which makes it the least forgiving layout in the waveform.
/// <see cref="Narrow"/> was keyed for the first time on 2026-09-20 and delivered every frame in
/// both directions at 2.2 to 2.3 kbit/s, so entry 0 is no longer an untested layout. What was
/// already proved on air is cross-geometry decoding
/// itself: on 2026-09-19 a station configured for the narrowest layout in its table decoded every
/// burst of a transmission sent on a span twice its width, on carriers its own preamble never
/// occupied, and the reverse and the wider-than-the-receiver case both worked too. The mechanism
/// is therefore measured; what is not is this particular narrow layout, which differs from the one
/// that ran only in where its band starts and by a couple of carriers, so the header arithmetic is
/// unchanged. Keying <c>ofdm-fm-narrow</c> between two stations is the run that would close
/// it.</para>
/// <para><b>What has been on the air.</b> Everything on the 6 kHz and 8 kHz spans below has run
/// between two Tait TM8110s on a 25 kHz channel, at the payload sizes and rates named in
/// docs/dev/ofdm-fm/. <see cref="Narrow"/> joined them on 2026-09-20: a layout of our own design,
/// sized so the coded header still fits one symbol, and here because a radio with a voice-bandwidth
/// audio path cannot run the wider ones at all. It measured 2.2 to 2.3 kbit/s with every frame
/// delivered in both directions across a multi-hour soak.</para>
/// </remarks>
public static class OfdmFmPresets
{
    /// <summary>The host rate every preset is written at.</summary>
    private const int Rate = 48000;

    /// <summary>Transform size at <see cref="Rate"/>.</summary>
    private const int Transform = 2048;

    /// <summary>
    /// Guard samples at <see cref="Rate"/>. Halved from what this waveform first used, measured on
    /// air: every profile delivered the same frames at the shorter guard, the decoded bursts'
    /// own error vectors showed no inter-symbol interference, per-carrier signal to noise moved
    /// less than half a decibel, and the same carrier stayed the worst one. It buys about 3 % of
    /// air time back on every burst.
    /// </summary>
    private const int Guard = 64;

    /// <summary>
    /// The lowest occupied bin, shared by every preset so the narrow layouts sit inside the wide
    /// ones. Low enough to use the bottom of a flat data-port path, which is where this waveform
    /// is injected; a radio whose audio path rolls off below 300 Hz loses the first few carriers,
    /// and the interleave spreads that loss across the code rather than concentrating it.
    /// </summary>
    private const int FirstCarrier = 9;

    /// <summary>
    /// The table entry every burst's sync, preamble and header goes out on, whatever its payload's
    /// span. Entry 0 by the wire format's own rule, and it is <see cref="Narrow"/> because the
    /// acquisition layout has to be contained in every other span and the narrowest one is.
    /// </summary>
    private const int AcquisitionLayout = 0;

    private static OfdmFmCoding Code(int numerator, int denominator) =>
        new(OfdmFmFec.Convolutional, ConstraintLength: 7, numerator, denominator, Interleave: true);

    /// <summary>
    /// A voice-bandwidth layout, 211 Hz to 2.9 kHz, QPSK at rate 1/2: the most robust thing here
    /// and the only one a microphone-and-speaker path can carry.
    /// <para><b>Measured on air 2026-09-20</b>, 2.2 to 2.3 kbit/s with every frame delivered in
    /// both directions. 112 data carriers is comfortably above the 104 coded bits the header needs
    /// to fit a single symbol, which is the one hard constraint on how narrow a layout can go, and
    /// the rendered-burst arithmetic predicts 2347 bit/s, which is what it does.</para>
    /// </summary>
    public static OfdmFmParameters Narrow { get; } = new(
        SampleRate: Rate, FftSize: Transform, CyclicPrefix: Guard, FirstCarrier: FirstCarrier,
        DataCarriers: 112, PilotCarriers: 4,
        Coding: Code(1, 2),
        Constellation: OfdmFmConstellation.Qpsk,
        GeometryId: AcquisitionLayout);

    /// <summary>
    /// 211 Hz to 6.0 kHz, QPSK at rate 1/2: the robust choice on a data port, and the one to fall
    /// back to when the fast presets stop delivering.
    /// <para>Measured on air. It does not set <see cref="OfdmFmParameters.ContiguousBursts"/>,
    /// which is worth about 12 % on the 8 kHz span but has never been measured on this one.</para>
    /// </summary>
    public static OfdmFmParameters Robust6k { get; } = new(
        SampleRate: Rate, FftSize: Transform, CyclicPrefix: Guard, FirstCarrier: FirstCarrier,
        DataCarriers: 240, PilotCarriers: 8,
        Coding: Code(1, 2),
        Constellation: OfdmFmConstellation.Qpsk,
        PeakToAverageLimitDb: 10,
        GeometryId: 1);

    /// <summary>
    /// 211 Hz to 6.0 kHz, QAM-64 at rate 2/3, contiguous bursts. Measured at 13.3 to 17.8 kbit/s
    /// of application goodput depending on frame size, delivering every frame.
    /// </summary>
    public static OfdmFmParameters Fast6k { get; } = new(
        SampleRate: Rate, FftSize: Transform, CyclicPrefix: Guard, FirstCarrier: FirstCarrier,
        DataCarriers: 240, PilotCarriers: 8,
        Coding: Code(2, 3),
        Constellation: OfdmFmConstellation.Qam64,
        ContiguousBursts: true,
        GeometryId: 1);

    /// <summary>
    /// 211 Hz to 8.0 kHz, QAM-64 at rate 2/3, contiguous bursts: <b>the default, and the honest
    /// one</b>. 17.5 to 24.7 kbit/s of goodput, every frame delivered at 1024 and 1900 bytes in
    /// both directions and 19 of 20 at 3000.
    /// <para>The peak-to-average limit is stated although it is this constellation's default, so
    /// that a copy edited to another constellation keeps the measured setting rather than quietly
    /// inheriting a different one.</para>
    /// </summary>
    public static OfdmFmParameters Default8k { get; } = new(
        SampleRate: Rate, FftSize: Transform, CyclicPrefix: Guard, FirstCarrier: FirstCarrier,
        DataCarriers: 323, PilotCarriers: 10,
        Coding: Code(2, 3),
        Constellation: OfdmFmConstellation.Qam64,
        PeakToAverageLimitDb: 10,
        ContiguousBursts: true,
        GeometryId: 2);

    /// <summary>
    /// The default at rate 5/6: the choice for bulk traffic on a good link. 19.2 to 29.8 kbit/s,
    /// and it delivered every frame or all but one in every cell in both directions, which none of
    /// the follow-on presets managed.
    /// </summary>
    public static OfdmFmParameters Bulk8k { get; } = Default8k with { Coding = Code(5, 6) };

    /// <summary>
    /// The default at rate 7/8. 21.1 to 29.8 kbit/s. Here to mark where the margin runs out on a
    /// bench link rather than as a recommendation: it dropped frames at the larger payload sizes
    /// in both directions.
    /// </summary>
    public static OfdmFmParameters Edge8k { get; } = Default8k with { Coding = Code(7, 8) };

    /// <summary>
    /// The default with follow-on frames: frames after the first in a keyup carry header and
    /// payload only, with no sync symbol, preamble, estimate symbol or lead-in, because the
    /// receiver still holds the timing and the channel. 23.9 to 28.3 kbit/s.
    /// <para>The trade is stated in <see cref="OfdmFmParameters.FollowOnFrames"/>: a follow-on
    /// header that fails costs the rest of the keyup, because there is no sync symbol to find the
    /// next frame by. It dropped frames at 1900 bytes where the full-burst default did not.</para>
    /// </summary>
    public static OfdmFmParameters Follow8k { get; } = Default8k with { FollowOnFrames = true };

    /// <summary>
    /// Follow-on frames with the link choosing its own rate, capped at QAM-64.
    /// <para><b>Right for a point-to-point link and wrong for a shared channel</b>, for the reason
    /// in <see cref="OfdmFmParameters.AdaptiveRate"/>: the payload here is an opaque AX.25 frame
    /// and this layer never reads an address, so several correspondents' reports average into one
    /// nonsense recommendation. The cap is what a Raspberry Pi 4 was measured to decode in less
    /// than the burst's own air time, including for a burst that will not decode at all.</para>
    /// </summary>
    public static OfdmFmParameters Adaptive8k { get; } = Follow8k with
    {
        AdaptiveRate = true,
        AdaptiveTopConstellation = OfdmFmConstellation.Qam64,
    };

    /// <summary>Mode name to layout, for the catalogue and for anything that wants to enumerate
    /// what this modem offers.</summary>
    public static IReadOnlyDictionary<string, OfdmFmParameters> ByMode { get; } =
        new Dictionary<string, OfdmFmParameters>(StringComparer.Ordinal)
        {
            ["ofdm-fm-narrow"] = Narrow,
            ["ofdm-fm-6k"] = Robust6k,
            ["ofdm-fm-6k-fast"] = Fast6k,
            ["ofdm-fm-8k"] = Default8k,
            ["ofdm-fm-8k-r56"] = Bulk8k,
            ["ofdm-fm-8k-r78"] = Edge8k,
            ["ofdm-fm-8k-follow"] = Follow8k,
            ["ofdm-fm-8k-adaptive"] = Adaptive8k,
        };

    /// <summary>
    /// The geometry table every shipped mode acquires against: three layouts, ids 0 to 2, built
    /// from <see cref="ByMode"/> so a preset and the table can never disagree about a span.
    /// </summary>
    /// <remarks>
    /// Built once and shared by every modem. <see cref="OfdmFmParameters.TableOf"/> refuses a set
    /// of profiles that cannot form a table, so a mistake here is a start-up failure rather than a
    /// link that acquires and then decodes the payload against the wrong layout. Nothing is ever
    /// rejected from this particular set, and the test suite holds that.
    /// </remarks>
    public static OfdmFmGeometryTable Table { get; } =
        OfdmFmParameters.TableOf(ByMode, out _)
        ?? throw new InvalidOperationException(
            "the shipped OFDM-FM presets do not form a geometry table, which is a build-time "
            + "mistake in OfdmFmPresets rather than anything an operator can cause");
}
