namespace Packet.SoundModem.Modems;

/// <summary>
/// A receive-only 300 baud AFSK AX.25 demodulator riding alongside a PSK SSB modem, to hear the
/// station identification a NinoTNC sends in a mode its own data modem cannot read.
/// </summary>
/// <remarks>
/// <para><b>The behaviour.</b> The TARPN NinoTNC N9600A Operator's Manual
/// (<c>tarpn.net/t/nino-tnc/n9600a/n9600a_operation.html</c>, "ID beacon"): in the PSK SSB modes
/// - 300 BPSK, 600 QPSK, 1200 BPSK, 2400 QPSK - the TNC identifies with "a beacon in 300 AFSK
/// AX.25 (1600/1800 Hz tones)", from the host's callsign to <c>IDENT</c>, every 9.5 minutes by
/// default while the station is actively transmitting. No beacon is sent in 300 AFSK AX.25, 300
/// AFSK IL2Pc or 1200 AFSK AX.25, those modes being self-identifying already.</para>
///
/// <para>So on a PSK channel there is a recurring transmission that every station on it can hear
/// and none of them can decode: the data modem is a PSK demodulator and the ident is FSK. This
/// closes that - a second demodulator, on the same audio, whose only job is that beacon.</para>
///
/// <para><b>Where it listens.</b> Nino's PSK SSB modes are "phase modulation of a 1500 Hz tone"
/// and the beacon's tones are 1600/1800, so the ident sits 200 Hz above the carrier it
/// accompanies - <em>relative to the transmitting TNC's own audio layout</em>, which is what
/// makes the offset and not the absolute 1700 Hz the thing to track. Tune the base modem to
/// 1200 Hz because your dial sits 300 Hz above your neighbour's and their beacon arrives at your
/// 1400 Hz, not at 1700. <see cref="CentreHzFor"/> is that arithmetic and the only placement rule
/// there is.</para>
///
/// <para><b>What it deliberately is not.</b> It occupies no KISS sub-channel - beacons are not
/// traffic and a host asking for packet data should not have to filter idents out of it - and it
/// contributes to neither <see cref="IModem.CarrierDetect"/> nor <see cref="IModem.ChannelBusy"/>,
/// so channel access is unchanged by its presence. It never transmits: identifying ourselves is
/// the host's business and this is a listener. That makes it a receive tap rather than a modem,
/// which is also why it is a class of its own rather than another <see cref="ModemCatalog"/>
/// entry.</para>
/// </remarks>
public sealed class IdBeaconGhost
{
    /// <summary>
    /// The carrier the NinoTNC phase-modulates in every PSK SSB mode, per the operator's manual.
    /// Also the <see cref="ModemCatalog"/> default centre for those modes, which is why an
    /// unconfigured modem needs no special case here (asserted by a test, so the two cannot
    /// silently part company).
    /// </summary>
    public const double NinoPskCarrierHz = 1500;

    /// <summary>The beacon's mark/space midpoint on a NinoTNC: tones 1600/1800.</summary>
    public const double NinoBeaconCentreHz = 1700;

    /// <summary>
    /// How far above its PSK carrier a NinoTNC puts its ident. The offset travels with the
    /// transmitting station's audio layout; the absolute frequency does not.
    /// </summary>
    public const double BeaconOffsetHz = NinoBeaconCentreHz - NinoPskCarrierHz;

    /// <summary>
    /// The mode the beacon is sent in, as a <see cref="ModemCatalog"/> key. The receiver built
    /// from it is whatever that mode currently means - since 2026-08-02 the narrow-branch
    /// frequency-diversity bank rather than a single wide demodulator, which is why this names
    /// the catalogue entry and not a class.
    /// </summary>
    /// <remarks>
    /// That default is worth more here than it is on a data slot. A ghost listens 200 Hz from a
    /// PSK carrier by construction, and a quadrature discriminator follows the strongest thing in
    /// its passband - tight branches are exactly what keeps the neighbour it sits beside out of
    /// the ident. The bank's ±175 Hz of coverage is also the right shape for the only error this
    /// placement really has: a dial that differs from the transmitting station's.
    /// </remarks>
    public const string BeaconMode = "afsk300";

    private readonly IModem _modem;

    private IdBeaconGhost(IModem modem, int subChannel, string baseMode, double centreHz)
    {
        _modem = modem;
        SubChannel = subChannel;
        BaseMode = baseMode;
        CentreHz = centreHz;
    }

    /// <summary>The sub-channel of the modem this ghost accompanies - for labelling only; the
    /// ghost is not addressable and delivers nothing to a KISS host.</summary>
    public int SubChannel { get; }

    /// <summary>The mode of the modem this ghost accompanies.</summary>
    public string BaseMode { get; }

    /// <summary>The mark/space midpoint this ghost listens on.</summary>
    public double CentreHz { get; }

    /// <summary>
    /// What the receiver reports itself as - <c>afsk300-multi11</c> for the catalogue default,
    /// not the bare <see cref="BeaconMode"/> key it was built from. Read off the modem so a
    /// change to what that key means shows up here rather than being quietly mislabelled.
    /// </summary>
    public string Mode => _modem.Mode;

    /// <summary>Raised for each beacon heard, with the receive diagnostics of the decode.</summary>
    public event Action<byte[], FrameQuality>? BeaconHeard;

    /// <summary>
    /// Whether <paramref name="mode"/> is one a NinoTNC identifies alongside rather than within -
    /// the PSK SSB modes, and their aliases. Answered from the mode's own catalogue row
    /// (<see cref="ModemCatalog.HasNinoPskIdBeacon"/>) rather than a name list kept here: a new
    /// PSK mode used to have to remember this file existed, or its idents stayed unreadable
    /// bursts in the middle of the channel. The row notes say why <c>qpsk3600</c> answers no
    /// (an FM mode; Nino identifies those in 1200 AFSK - a different ghost, not this one).
    /// </summary>
    public static bool AppliesTo(string mode) => ModemCatalog.HasNinoPskIdBeacon(mode);

    /// <summary>
    /// How far either side of <see cref="CentreHz"/> a ghost can hear, for anything that needs to
    /// know a ghost is listening without holding one - the signal survey, which would otherwise
    /// report an ident on this frequency as a signal nobody was listening to.
    /// </summary>
    /// <remarks>
    /// The <see cref="BeaconMode"/> receiver is the afsk300 bank, whose default is five branch
    /// pairs stepped 35 Hz: 175 Hz of comb either side, plus a branch's own pull-in. Stated as a
    /// constant rather than measured because it is a property of a mode this class chooses, and
    /// <c>IdBeaconGhostTests</c> pins it against the bank the catalogue actually builds.
    /// </remarks>
    public const double CoverageHalfWidthHz = 175;

    /// <summary>
    /// Where a base modem's ident will land, given the audio centre that modem is tuned to
    /// (null = the mode default, which is <see cref="NinoPskCarrierHz"/>).
    /// </summary>
    public static double CentreHzFor(double? baseCentreHz) =>
        (baseCentreHz ?? NinoPskCarrierHz) + BeaconOffsetHz;

    /// <summary>
    /// Builds the ghost for a base modem, or null when <paramref name="baseMode"/> is not one
    /// that carries an ident alongside it.
    /// </summary>
    /// <param name="subChannel">The base modem's sub-channel, carried through for labelling.</param>
    /// <param name="baseMode">The base modem's mode string.</param>
    /// <param name="baseCentreHz">Its configured audio centre; null for the mode default.</param>
    /// <param name="dspRate">The channel's DSP rate.</param>
    public static IdBeaconGhost? TryCreate(int subChannel, string baseMode, double? baseCentreHz, int dspRate)
    {
        ArgumentNullException.ThrowIfNull(baseMode);
        if (!AppliesTo(baseMode))
        {
            return null;
        }

        double centre = CentreHzFor(baseCentreHz);

        // Declared before it is built because the modem's decode event has to reach the ghost's
        // own event, and the modem must exist before the ghost can hold it. Nothing can fire in
        // between: a modem decodes nothing until it is fed audio, which is the caller's next move.
        IdBeaconGhost? ghost = null;
        IModem modem = ModemCatalog.Create(
            BeaconMode,
            dspRate,
            // The plain frame sink goes nowhere: a beacon has no host to be delivered to, and
            // everything a caller wants to show or write down is on the quality event below.
            _ => { },
            new ModemOptions(CentreFrequencyHz: centre));
        modem.FrameDecoded += (frame, quality) => ghost!.BeaconHeard?.Invoke(frame, quality);
        ghost = new IdBeaconGhost(modem, subChannel, baseMode, centre);
        return ghost;
    }

    /// <summary>Feeds received audio at the channel's DSP rate. Wire to
    /// <c>SoundModemChannel.AddReceiveTap</c>, which is already gated on half duplex.</summary>
    public void Process(ReadOnlySpan<float> samples) => _modem.Process(samples);

    /// <summary>
    /// Clears receive carrier state. A tap is not one of the channel's modems, so nothing resets
    /// it on unkey; call this from <c>TransmittingChanged</c> or the demodulator carries its
    /// pre-keyup carrier state across the silence and the deframer sees a false edge on the far
    /// side of it.
    /// </summary>
    public void ResetCarrierState() => _modem.ResetCarrierState();
}
