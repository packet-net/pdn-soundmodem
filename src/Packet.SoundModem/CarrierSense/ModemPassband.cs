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
    /// How much wider than its own emission a modem is assumed to listen, each side, in Hz.
    /// </summary>
    /// <remarks>
    /// <para>The edges below are measured off what a modem <em>transmits</em>, and a modem listens
    /// wider than it transmits for two reasons that are worth naming because both are real on the
    /// shipped modes.</para>
    /// <para><b>A station we would work can be off frequency.</b> ardopcf pads its own busy
    /// window by its 100 Hz <c>TuningRange</c> for exactly this, and the daemon's
    /// <c>ArdopBusyDetector</c> copies it: a caller a tuning range off is still one we want to
    /// hear, so a detector watching only our own emission would transmit over them.</para>
    /// <para><b>The diversity banks ladder their branches either side of centre.</b>
    /// <see cref="Afsk300MultiModem"/> runs 11 branches 35 Hz apart, so its outermost branch is
    /// 175 Hz off the bank's centre, and each branch's receive filter is +/-250 Hz against a
    /// measured 99 % occupied bandwidth of +/-170 Hz. That bank therefore listens about 255 Hz
    /// beyond the edges measured here. <see cref="BpskMultiModem"/>'s ladder is far narrower
    /// (baud/40, so +/-30 Hz at 300 baud).</para>
    /// <para><b>250 Hz is the figure, and what it costs is nothing on the geometry this was
    /// written for.</b> Measured at 12 kHz on GB7RDG's own modems: afsk300 at 850, 987 and
    /// 1120 Hz occupy 680-1020, 820-1148 and 961-1289 Hz, so that cluster overlaps with no guard
    /// at all and keeps deferring to itself; bpsk300 at 2150 Hz occupies 1980-2320 Hz, which is
    /// 691 Hz clear of the nearest AFSK edge and still 191 Hz clear with 250 Hz added to each
    /// side. That separation is the whole point of the change: over the 228 s that station could
    /// not answer a connect request, its bpsk300 modem was busy 81.1 % of the time and the OR
    /// across the four modems gating the transmitter was busy 96.4 %, one usable gap against
    /// fourteen (packet-net/pdn-soundmodem#526).</para>
    /// <para>Erring wide is the safe direction: a guard that is too large only makes a station
    /// defer where it need not, which is where every station was before this.</para>
    /// </remarks>
    internal const double GuardHz = 250;

    /// <summary>
    /// Whether transmitting in this band would land inside <paramref name="other"/>'s, with
    /// <see cref="GuardHz"/> allowed on each side of each.
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
