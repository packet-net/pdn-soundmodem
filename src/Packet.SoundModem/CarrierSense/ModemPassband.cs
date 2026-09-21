using Packet.SoundModem.Modems;

namespace Packet.SoundModem.CarrierSense;

/// <summary>
/// One modem's place in the audio band, as edges in Hz, and whether two of them are close enough
/// that transmitting on one would land in the other's.
/// </summary>
/// <remarks>
/// <para>This is what makes carrier sense a question per sub-channel rather than one answer for
/// the station. A modem's in-band energy detector only hears what is in its own passband, so a
/// modem 1.3 kHz away reporting busy says nothing about whether transmitting here would collide
/// with anything. See <see cref="Channel.SoundModemChannel.ChannelBusyFor(int)"/>.</para>
/// </remarks>
internal readonly record struct ModemPassband(double LowHz, double HighHz)
{
    /// <summary>
    /// How far outside its own emission a modem is assumed to hear a station it would work, each
    /// side, in Hz.
    /// </summary>
    /// <remarks>
    /// <para><b>What the guard is actually for is frequency error.</b> The edges below are
    /// measured off what a modem <em>transmits</em>, and a station calling us can be off tune, so
    /// a passband that stops at our own emission would treat a caller sitting just outside it as
    /// somebody else's business. That is measurable, and it has been measured rather than
    /// inferred.</para>
    /// <para><b>Measured on GB7RDG's frame log</b>, every received frame carrying an offset
    /// since 2026-09-01, n = 28,839. Absolute offset from the sub-channel's own centre:</para>
    /// <para>p50 3.1 Hz, p75 6.5 Hz, p90 18.6 Hz, p95 20.2 Hz, p99 115.7 Hz, max 265.9 Hz.
    /// Within 25 Hz: 96.97 %. Within 50 Hz: 98.52 %. Within 100 Hz: 98.84 %.</para>
    /// <para>By mode, which is where the distribution comes from: BPSK300 IL2Pc n = 25,948,
    /// p99 21.5 Hz, max 72.0 Hz; AFSK300 IL2Pc n = 2,318, p99 83.1 Hz, max 265.9 Hz; AFSK300
    /// n = 391, p99 232.9 Hz, max 244.5 Hz. <b>50 Hz covers 98.5 % of all of it and sits at
    /// 2.3x the p99 of BPSK300, the mode carrying 90 % of the traffic.</b> The long tail is a
    /// couple of badly off-tune AFSK stations in a few hundred frames, and sizing the guard to
    /// cover those is how an earlier draft of this reached 250 Hz.</para>
    /// <para><b>Not from the receive filters, which was the earlier mistake.</b> A diversity
    /// bank's branch filter is +/-250 Hz and its branch ladder spans +/-175 Hz
    /// (<see cref="Afsk300MultiModem"/>), but those numbers are the receiver's SEARCH RANGE, not
    /// the error it meets: the bank ladders branches precisely so that an off-tune signal is
    /// caught, which is a different quantity from how far off tune signals actually are. Sizing
    /// the guard from the filter makes it as wide as the search and throws away most of the
    /// separation the change is trying to recover. ardopcf's 100 Hz <c>TuningRange</c>, which the
    /// daemon's <c>ArdopBusyDetector</c> copies, is the same kind of figure.</para>
    /// <para><b>Which way to err, and why it is this way.</b> The guard only ever widens a busy
    /// decision. An undersized one means two very nearly adjacent sub-channels might fail to
    /// defer to each other; it cannot affect decoding, which never consults this. An oversized
    /// one puts back exactly the deferral this change exists to remove, across the whole station.
    /// That asymmetry is why the right size is a measured p99 rather than a worst case.</para>
    /// <para><b>What it costs on the geometry it was sized for: nothing.</b> GB7RDG runs
    /// afsk300-il2pc at 850 Hz audio and bpsk300 at 2150 Hz, occupying 680-1020 and 1980-2320 Hz
    /// measured at 12 kHz. That is 960 Hz of clear air between the edges, against the 100 Hz two
    /// 50 Hz guards spend. Two afsk300 modems 133 Hz apart, which that station ran until
    /// 2026-09-21, overlap outright with no guard at all and keep deferring to each
    /// other.</para>
    /// </remarks>
    internal const double GuardHz = 50;

    /// <summary>
    /// Whether transmitting in this band would land inside <paramref name="other"/>'s, with
    /// <see cref="GuardHz"/> of frequency error allowed on each side of each.
    /// </summary>
    internal bool Overlaps(ModemPassband other) =>
        LowHz - GuardHz < other.HighHz + GuardHz
        && other.LowHz - GuardHz < HighHz + GuardHz;

    /// <summary>
    /// Measures what <paramref name="modem"/> occupies, or null when it will not say.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ModemBandProbe"/> rather than a second notion of how wide a mode is: it
    /// is what the waterfall draws each modem's band from and what the RF band planner fits a
    /// passband with, so what carrier sense treats as overlapping is the same measurement an
    /// operator is looking at. No modem has to expose anything new for it.</para>
    /// <para><b>It catches everything, deliberately.</b> This runs while a station is being
    /// built, and the probe asks a modem - which on a plugin mode is somebody else's code - to
    /// modulate a frame. A modem that throws must cost the station its per-sub-channel carrier
    /// sense and nothing else; throwing out of here would cost it the whole service. Same
    /// judgement, and the same reason, as the catch around opening a radio's serial port in
    /// <c>TaitCarrierSense</c>. Null then means "could be anywhere", and a null band defers to
    /// everything, which is exactly where the station was before this existed.</para>
    /// </remarks>
    internal static ModemPassband? Measure(IModem modem, int sampleRate)
    {
        try
        {
            return ModemBandProbe.TryMeasure(modem, sampleRate, out double low, out double high)
                ? new ModemPassband(low, high)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
