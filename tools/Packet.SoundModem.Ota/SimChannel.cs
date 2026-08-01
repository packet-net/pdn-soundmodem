using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Ota;

/// <summary>The channel geometries the simulation baseline injects.</summary>
/// <remarks>
/// <para>The two fading profiles are the CCIR / ITU-R F.1487 mid-latitude pair. <b>Poor</b> is
/// the same rig the MS110D mask suite and §E2 ladder gate against —
/// <see cref="WattersonChannel.Poor"/>, two equal-power Rayleigh paths 2&#160;ms apart at a
/// 1&#160;Hz two-sigma fade. <b>Good</b> is its slow-fading counterpart: 0.5&#160;ms / 0.1&#160;Hz.
/// The MS110D campaign gated on Poor and ran off-rig direction checks at 1&#160;ms/0.5&#160;Hz and
/// 3&#160;ms/2&#160;Hz but never fixed a canonical Good, so we take the standard CCIR Good rather
/// than invent one — the same tabled pairing FreeDV's own MPG/MPP channel models come from.</para>
/// </remarks>
internal enum SimChannelKind
{
    /// <summary>The ideal path plus calibrated AWGN (D-LXIV) — no multipath, no fading.</summary>
    Awgn,

    /// <summary>CCIR / ITU-R F.1487 "Good": two equal-power Rayleigh paths 0.5&#160;ms apart,
    /// 0.1&#160;Hz two-sigma fade — a slow, shallow multipath.</summary>
    Good,

    /// <summary>CCIR / ITU-R F.1487 "Poor" (D.6.1): two equal-power Rayleigh paths 2&#160;ms apart,
    /// 1&#160;Hz two-sigma fade — the mask suite's own rig.</summary>
    Poor,
}

/// <summary>Maps a <see cref="SimChannelKind"/> to the <see cref="WattersonChannel"/> path geometry,
/// and applies the channel to a rendered burst.</summary>
/// <remarks>
/// <para>The channel injected here is literally the mask suite's <see cref="WattersonChannel"/> —
/// the same file the MS110D sim baseline used — so an OFDM waterfall and an MS110D waterfall are
/// measured against one rig, not two. Only the <see cref="SimChannelKind.Good"/> geometry is
/// defined here (the file has only <c>Poor</c>); it is a plain path list, not a modem change.</para>
/// <para><b>SNR convention.</b> <paramref name="snrDb"/> is signal power over noise measured in a
/// 3&#160;kHz bandwidth — the HF SSB convention FreeDV quotes its operating points in
/// (&quot;SNR3k&quot;). The channel calibrates the noise floor against the mean power of the
/// <em>active</em> burst it is handed, so the caller must strip leading/trailing modulator silence
/// before calling — otherwise the silence dilutes the signal-power estimate and every point reads
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

    /// <summary>The path geometry for a channel kind — empty for AWGN (the ideal direct path).</summary>
    public static WattersonPath[] Paths(SimChannelKind kind) => kind switch
    {
        SimChannelKind.Awgn => [],
        SimChannelKind.Good => Good,
        SimChannelKind.Poor => WattersonChannel.Poor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown channel"),
    };

    /// <summary>Parses a channel name (awgn|good|poor), case-insensitive, prefix-tolerant.</summary>
    public static SimChannelKind Parse(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n.StartsWith('a') ? SimChannelKind.Awgn
            : n.StartsWith('g') ? SimChannelKind.Good
            : n.StartsWith('p') ? SimChannelKind.Poor
            : throw new ArgumentException($"unknown channel '{name}' (awgn|good|poor)", nameof(name));
    }

    /// <summary>
    /// Applies the channel to one active burst at a stated SNR, deriving the fade/noise realisation
    /// from <paramref name="seed"/> so a point reproduces from (mode, channel, SNR, seed) alone.
    /// </summary>
    /// <param name="activeBurst">The modulated burst with modulator silence already trimmed — its
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
    /// ~signal level, and the burst never rose the 6 dB needed to assert — the energy-gated modes
    /// (C4FSK) discarded every sample and scored 0/N at any SNR while ungated modes were untouched
    /// (the 2026-08-01 diagnosis; the FM ladder never hit it because its lead-in is 6 s).</param>
    /// <param name="leadOutSeconds">Noise-only padding after the burst (end-of-burst window).</param>
    public static float[] Apply(
        ReadOnlySpan<float> activeBurst, int rate, SimChannelKind kind, double snrDb, int seed,
        double leadInSeconds = 0.5, double leadOutSeconds = 1.2, double cfoHz = 0)
    {
        var channel = new WattersonChannel(rate, seed, Paths(kind));
        return channel.Apply(
            activeBurst,
            snrDb,
            noiseBandwidthHz: 3000,
            leadInSamples: (int)(leadInSeconds * rate),
            leadOutSamples: (int)(leadOutSeconds * rate),
            frequencyOffsetHz: cfoHz);
    }
}
