using M0LTE.Ardop;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Ota;

/// <summary>Outcome of one ARDOP frame trial.</summary>
/// <param name="Matched">The decoded payload equals the sent payload byte-exact (and the
/// decoder reported the body ok) - the frame-layer success criterion.</param>
/// <param name="Quality">The decoder's 0-100 quality for the frame when one was decoded
/// (matched or not); 0 when the frame never acquired - the soft margin indicator.</param>
internal readonly record struct ArdopProbeResult(bool Matched, int Quality);

/// <summary>
/// One ARDOP data frame through the Watterson rig - the ARDOP campaign's A3 sim seam
/// (docs/ardop/plan.md), closing rx-roadmap workstream 0's recorded gap. ARDOP is a session
/// TNC rather than a catalogue <c>IModem</c>, so the mode-generic <see cref="SimModem"/> rig
/// structurally cannot drive it; this probe is the <see cref="DatacPacketProbe"/> pattern
/// applied to the ARDOP engine instead: render a known frame with the library's own
/// modulator, inject the shared channel, decode with a fresh demodulator, score byte-exact.
/// <c>sm-ota sim --mode ardop:&lt;FrameName&gt;</c> and a mask row are the same measurement.
/// </summary>
/// <remarks>
/// <para>The demodulator is default-constructed (squelch 5, ±100 Hz tuning - the ardopcf
/// defaults), matching the package's own loopback bar, not the monitor's widened ±200 wild
/// setting. Each trial is a single isolated frame: no Memory-ARQ accumulation across
/// retries, so the measured floor is the single-copy floor - the number a wild one-shot
/// decode faces.</para>
/// <para>The frame is a full-capacity even-parity data frame (the wild corpus's .E bodies);
/// the payload derives from the trial seed, so a point reproduces from
/// (frame type, channel, SNR, first seed, count) exactly like every other mode's.</para>
/// <para><b>The single-shot ceiling is ~98 %, not 100 %, and that is the receiver, not the
/// rig.</b> A small fraction of noise realisations false-trigger the leader detector during
/// the lead-in and the capture swallows the real leader (measured: seeds 18 and 27 at every
/// AWGN SNR from +10 to +25, seed 18 with margin 0 = never acquired, seed 27 acquiring on a
/// wrong lock and failing the body at quality ~44 even at +25 dB; both decode at +40 dB,
/// where noise cannot trigger the squelch). Deployment absorbs this with ARQ retries; the
/// masks pin the measured single-copy reality including it. A leader re-arm improvement
/// would be a receiver change with its own A/B, not an instrument fix.</para>
/// </remarks>
internal sealed class ArdopFrameProbe
{
    /// <summary>Engine-native rate - ARDOP is hard 12 kHz.</summary>
    public const int Rate = 12000;

    private const string Prefix = "ardop:";

    private readonly ArdopFrameInfo _info;

    /// <summary>True when <paramref name="mode"/> names an ARDOP sim mode
    /// (<c>ardop:&lt;FrameName&gt;</c>).</summary>
    public static bool IsArdopMode(string mode) =>
        mode.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves <c>ardop:&lt;FrameName&gt;</c> (e.g. <c>ardop:4FSK.500.100</c>,
    /// parity suffix optional - the even frame is used) against the frame-type table.</summary>
    public ArdopFrameProbe(string mode)
    {
        if (!IsArdopMode(mode))
        {
            throw new ArgumentException($"'{mode}' is not an ardop:<FrameName> mode", nameof(mode));
        }

        string name = mode[Prefix.Length..];
        for (int type = 0; type <= byte.MaxValue; type++)
        {
            if (ArdopFrameInfo.TryGet((byte)type, out ArdopFrameInfo info)
                && !info.IsOdd && info.DataLength > 0 && ArdopFrameType.IsData((byte)type)
                && (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.Name, name + ".E", StringComparison.OrdinalIgnoreCase)))
            {
                _info = info;
                return;
            }
        }

        throw new ArgumentException(
            $"'{name}' names no ARDOP data frame; try e.g. ardop:4FSK.500.100", nameof(mode));
    }

    /// <summary>Full frame capacity - the payload every trial carries.</summary>
    public int PayloadBytes => _info.DataLength * _info.CarrierCount;

    /// <summary>The resolved even-parity frame name (e.g. <c>4FSK.500.100.E</c>).</summary>
    public string FrameName => _info.Name;

    /// <summary>Runs one frame through the channel and scores it.</summary>
    /// <param name="seed">Draws the payload and (offset) the channel realisation.</param>
    /// <param name="levelScale">Absolute post-channel level scale (1 = nominal); the SNR is
    /// unchanged - the level-invariance axis.</param>
    public ArdopProbeResult Run(
        int seed, SimChannelKind kind, double snrDb, float levelScale, double cfoHz = 0,
        double impulseRatePerMinute = 0)
    {
        byte[] payload = new byte[PayloadBytes];
        new Random(seed).NextBytes(payload);
        byte[] encoded = ArdopFrameCodec.EncodeDataFrame(_info.Type, payload, 0xFF);
        short[] burst = new ArdopModulator().Modulate(encoded);
        float[] active = new float[burst.Length];
        for (int i = 0; i < burst.Length; i++)
        {
            active[i] = Pcm16.ToFloat(burst[i]);
        }

        float[] rx = SimChannel.Apply(
            active, Rate, kind, snrDb, seed + 3_000_000, cfoHz: cfoHz,
            impulseRatePerMinute: impulseRatePerMinute);
        if (levelScale != 1f)
        {
            for (int i = 0; i < rx.Length; i++)
            {
                rx[i] *= levelScale;
            }
        }

        bool matched = false;
        int quality = 0;
        var demodulator = new ArdopDemodulator();
        demodulator.FrameDecoded += frame =>
        {
            quality = Math.Max(quality, frame.Quality);
            matched |= frame.Ok && frame.Data.AsSpan().SequenceEqual(payload);
        };
        demodulator.ProcessSamples(rx);
        return new ArdopProbeResult(matched, quality);
    }
}
