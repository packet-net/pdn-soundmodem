using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Ota;

/// <summary>The channel geometries the simulation baseline injects.</summary>
/// <remarks>
/// <para>The two fading profiles are the CCIR / ITU-R F.1487 mid-latitude pair. <b>Poor</b> is
/// the same rig the MS110D mask suite and §E2 ladder gate against -
/// <see cref="WattersonChannel.Poor"/>, two equal-power Rayleigh paths 2&#160;ms apart at a
/// 1&#160;Hz two-sigma fade. <b>Good</b> is its slow-fading counterpart: 0.5&#160;ms / 0.1&#160;Hz.
/// The MS110D campaign gated on Poor and ran off-rig direction checks at 1&#160;ms/0.5&#160;Hz and
/// 3&#160;ms/2&#160;Hz but never fixed a canonical Good, so we take the standard CCIR Good rather
/// than invent one - the same tabled pairing FreeDV's own MPG/MPP channel models come from.</para>
/// </remarks>
internal enum SimChannelKind
{
    /// <summary>The ideal path plus calibrated AWGN (D-LXIV) - no multipath, no fading.</summary>
    Awgn,

    /// <summary>CCIR / ITU-R F.1487 "Good": two equal-power Rayleigh paths 0.5&#160;ms apart,
    /// 0.1&#160;Hz two-sigma fade - a slow, shallow multipath.</summary>
    Good,

    /// <summary>CCIR / ITU-R F.1487 "Moderate": two equal-power Rayleigh paths 1&#160;ms
    /// apart, 0.5&#160;Hz two-sigma fade - the middle of the standard triple, and the most
    /// representative of an ordinary UK 40&#160;m NVIS day: Good is outage-bound and Poor is
    /// equaliser-bound, so this is the channel where receiver improvements actually show.</summary>
    Moderate,

    /// <summary>CCIR / ITU-R F.1487 "Poor" (D.6.1): two equal-power Rayleigh paths 2&#160;ms apart,
    /// 1&#160;Hz two-sigma fade - the mask suite's own rig.</summary>
    Poor,

    /// <summary>A real FM link through a radio's <b>microphone and speaker</b>: pre/de-emphasis at
    /// both ends and a 300-3000&#160;Hz voice passband, on a 12.5&#160;kHz channel (8&#160;kHz IF).
    /// The SNR axis becomes carrier-to-noise ratio in that IF bandwidth - see
    /// <see cref="Packet.SoundModem.Tests.Channel.FmChannel"/>, and note it is NOT comparable to
    /// the SSB modes' SNR3k.</summary>
    FmMic,

    /// <summary>A real FM link through a radio's <b>data port</b>: flat audio both ends, wider
    /// passband, same 12.5&#160;kHz channel. What a 9600 baud packet radio actually uses.</summary>
    FmData,
}

/// <summary>
/// What the FM modes are transmitted at. The peak deviations are
/// docs/mode-modulation-reference.md's targets, which is the same calibration the over-the-air
/// harness drives the Flex to - so a sim FM point and a bench FM point describe the same radio.
/// </summary>
internal static class FmModes
{
    /// <summary>The channel spacing a mode is meant to run on, in Hz. Not a preference: C4FSK
    /// 9600 is a narrow-channel mode and C4FSK 19200 is a wide-channel one (Tom, 2026-08-08,
    /// from MMDVM-TNC and NinoTNC practice), and running either on the wrong spacing measures a
    /// radio nobody deploys. The 5 kHz-deviation modes need the wide channel's IF filter or their
    /// own sidebands are truncated.</summary>
    public static double ChannelSpacingHz(string mode) => mode switch
    {
        // Tom, 2026-08-08, from MMDVM-TNC and NinoTNC practice: C4FSK 9600 is the narrow-channel
        // mode and C4FSK 19200 the wide one. GFSK 9600 keeps its G3RUH-lineage 25 kHz channel -
        // 2.4 kHz deviation against a 4800 Hz baseband is 14 kHz of Carson bandwidth, which an
        // 8 kHz IF filter cannot pass. qpsk3600 shares the C4FSK 19200 modulator, so it shares
        // its channel.
        _ when mode.StartsWith("c4fsk19200", StringComparison.Ordinal) => 25000,
        _ when mode.StartsWith("fsk9600", StringComparison.Ordinal) => 25000,
        _ => 12500,
    };

    /// <summary>Peak deviation in Hz for an FM catalogue mode, or null if the mode is an SSB
    /// mode and has no business on an FM channel.</summary>
    public static double? PeakDeviationHz(string mode) => mode switch
    {
        _ when mode.StartsWith("afsk1200", StringComparison.Ordinal) => 3000,
        _ when mode.StartsWith("fsk9600", StringComparison.Ordinal) => 2400,
        _ when mode.StartsWith("fsk4800", StringComparison.Ordinal) => 1200,
        _ when mode.StartsWith("c4fsk19200", StringComparison.Ordinal) => 5000,
        _ when mode.StartsWith("c4fsk9600", StringComparison.Ordinal) => 2500,
        _ when mode.StartsWith("qpsk3600", StringComparison.Ordinal) => 5000,
        _ => null,
    };

    /// <summary>True for the channel kinds that model a real FM link.</summary>
    public static bool IsFmChannel(SimChannelKind kind) =>
        kind is SimChannelKind.FmMic or SimChannelKind.FmData;

    /// <summary>The link profile for a (mode, channel) pair. Throws rather than quietly
    /// substituting a default: an SSB mode on an FM channel is a question with no answer, and a
    /// number produced for it would be a fiction.</summary>
    public static FmLinkProfile Profile(string mode, SimChannelKind kind)
    {
        double deviation = PeakDeviationHz(mode)
            ?? throw new ArgumentException(
                $"'{mode}' is not an FM mode - it has no deviation target in "
                + "docs/mode-modulation-reference.md, so an FM channel cannot carry it",
                nameof(mode));
        double ifBandwidth = FmLinkProfile.IfBandwidthForSpacing(ChannelSpacingHz(mode));
        return kind == SimChannelKind.FmMic
            ? FmLinkProfile.MicAndSpeaker(deviation, ifBandwidth)
            : FmLinkProfile.DataPort(deviation, ifBandwidthHz: ifBandwidth);
    }
}

/// <summary>Maps a <see cref="SimChannelKind"/> to the <see cref="WattersonChannel"/> path geometry,
/// and applies the channel to a rendered burst.</summary>
/// <remarks>
/// <para>The channel injected here is literally the mask suite's <see cref="WattersonChannel"/> -
/// the same file the MS110D sim baseline used - so an OFDM waterfall and an MS110D waterfall are
/// measured against one rig, not two. Only the <see cref="SimChannelKind.Good"/> geometry is
/// defined here (the file has only <c>Poor</c>); it is a plain path list, not a modem change.</para>
/// <para><b>SNR convention.</b> <paramref name="snrDb"/> is signal power over noise measured in a
/// 3&#160;kHz bandwidth - the HF SSB convention FreeDV quotes its operating points in
/// (&quot;SNR3k&quot;). The channel calibrates the noise floor against the mean power of the
/// <em>active</em> burst it is handed, so the caller must strip leading/trailing modulator silence
/// before calling - otherwise the silence dilutes the signal-power estimate and every point reads
/// high. The lead-in / lead-out padding is noise-only (acquisition sees a realistic floor, not
/// digital silence, and the receiver has trailing samples to run its end-of-burst detection into).
/// </para>
/// </remarks>
internal static class SimChannel
{
    /// <summary>CCIR / ITU-R F.1487 "Good": 2 equal Rayleigh paths, 0.5&#160;ms, 0.1&#160;Hz spread.</summary>
    public static WattersonPath[] Good =>
    [
        new WattersonPath(0, Fading: true, DopplerSpreadHz: 0.1),
        new WattersonPath(0.5, Fading: true, DopplerSpreadHz: 0.1),
    ];

    /// <summary>CCIR / ITU-R F.1487 "Moderate": 2 equal Rayleigh paths, 1&#160;ms, 0.5&#160;Hz spread.</summary>
    public static WattersonPath[] Moderate =>
    [
        new WattersonPath(0, Fading: true, DopplerSpreadHz: 0.5),
        new WattersonPath(1, Fading: true, DopplerSpreadHz: 0.5),
    ];

    /// <summary>The path geometry for a channel kind - empty for AWGN (the ideal direct path).</summary>
    public static WattersonPath[] Paths(SimChannelKind kind) => kind switch
    {
        SimChannelKind.Awgn => [],
        SimChannelKind.Good => Good,
        SimChannelKind.Moderate => Moderate,
        SimChannelKind.Poor => WattersonChannel.Poor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown channel"),
    };

    /// <summary>Parses a channel name (awgn|good|moderate|poor), case-insensitive,
    /// prefix-tolerant.</summary>
    public static SimChannelKind Parse(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n.StartsWith('a') ? SimChannelKind.Awgn
            : n.StartsWith('g') ? SimChannelKind.Good
            : n.StartsWith('m') ? SimChannelKind.Moderate
            : n.StartsWith('p') ? SimChannelKind.Poor
            : name.Equals("fm-mic", StringComparison.OrdinalIgnoreCase) ? SimChannelKind.FmMic
            : name.Equals("fm-data", StringComparison.OrdinalIgnoreCase) ? SimChannelKind.FmData
            : throw new ArgumentException(
                $"unknown channel '{name}' (awgn|good|moderate|poor|fm-mic|fm-data)", nameof(name));
    }

    /// <summary>
    /// Applies the channel to one active burst at a stated SNR, deriving the fade/noise realisation
    /// from <paramref name="seed"/> so a point reproduces from (mode, channel, SNR, seed) alone.
    /// </summary>
    /// <param name="activeBurst">The modulated burst with modulator silence already trimmed - its
    /// mean power is what the SNR is calibrated against.</param>
    /// <param name="rate">Sample rate of the burst.</param>
    /// <param name="kind">Channel geometry.</param>
    /// <param name="snrDb">SNR in a 3&#160;kHz noise bandwidth; <see cref="double.PositiveInfinity"/>
    /// for a noiseless run.</param>
    /// <param name="seed">Channel realisation seed.</param>
    /// <param name="leadInSeconds">Noise-only padding before the burst (acquisition floor).
    /// Must comfortably exceed the receiver's noise-floor seeding window:
    /// <see cref="Packet.SoundModem.Modems.EnergyBusyDetector"/> seeds its floor from its first
    /// 8 × 20 ms = 160 ms of audio, taking the LOUDEST seed block. The old 0.15 s default sat just
    /// inside that window, so the 8th seed block already contained burst audio, the floor seeded at
    /// ~signal level, and the burst never rose the 6 dB needed to assert - the energy-gated modes
    /// (C4FSK) discarded every sample and scored 0/N at any SNR while ungated modes were untouched
    /// (the 2026-08-01 diagnosis; the FM ladder never hit it because its lead-in is 6 s).</param>
    /// <param name="leadOutSeconds">Noise-only padding after the burst (end-of-burst window).</param>
    public static float[] Apply(
        ReadOnlySpan<float> activeBurst, int rate, SimChannelKind kind, double snrDb, int seed,
        double leadInSeconds = 0.5, double leadOutSeconds = 1.2, double cfoHz = 0,
        double impulseRatePerMinute = 0)
    {
        var channel = new WattersonChannel(rate, seed, Paths(kind));
        return channel.Apply(
            activeBurst,
            snrDb,
            noiseBandwidthHz: 3000,
            leadInSamples: (int)(leadInSeconds * rate),
            leadOutSamples: (int)(leadOutSeconds * rate),
            frequencyOffsetHz: cfoHz,
            impulses: impulseRatePerMinute > 0 ? new ImpulseNoiseProfile(impulseRatePerMinute) : null);
    }

    /// <summary>
    /// Applies a real FM link: the burst is frequency-modulated onto a carrier, meets noise there,
    /// and comes back through a limiter and a discriminator.
    /// </summary>
    /// <param name="cnrDb">Carrier-to-noise ratio in the receiver's IF bandwidth - the FM axis.
    /// Deliberately not the SSB modes' SNR3k: the two are different quantities and a number moved
    /// between them would be wrong.</param>
    /// <remarks>The lead-in is unmodulated carrier rather than silence, which is what a real
    /// transmitter's key-up gives the receiver, and it is long enough for the energy-gated modes to
    /// seed their noise floor on the floor rather than on the burst.</remarks>
    public static float[] ApplyFm(
        ReadOnlySpan<float> activeBurst, int rate, SimChannelKind kind, string mode, double cnrDb,
        int seed, double leadInSeconds = 0.5, double leadOutSeconds = 1.2)
    {
        FmLinkProfile profile = FmModes.Profile(mode, kind);
        return new FmChannel(profile, rate, seed).Apply(
            activeBurst, cnrDb, leadInSeconds, leadOutSeconds);
    }
}
