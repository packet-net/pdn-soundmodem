namespace Packet.SoundModem.Modems;

/// <summary>
/// Per-frame receive diagnostics, delivered alongside every decoded frame via
/// <see cref="IModem.FrameDecoded"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately named for what is measured, not "BER": true bit-error rate is not
/// observable from a receiver. Errors inside a Reed-Solomon-corrected byte are invisible
/// (one flipped bit and eight flipped bits both cost one corrected symbol), and frames
/// with more damage than the code can repair never decode at all, so they report nothing.
/// What IS honest per frame: how many bytes FEC repaired. <see cref="CorrectedBytes"/>
/// over the frame's on-air length is a floor on the channel's byte error rate - zero on a
/// clean link, and any persistent non-zero value is a link that is quietly consuming its
/// error budget and will start dropping frames when conditions worsen. That early-warning
/// property is the operational point of surfacing this.
/// </para>
/// <para>
/// The NinoTNC exposes the same idea in aggregate (its GETALL counters for corrected vs
/// uncorrectable IL2P receives); this is the per-frame version.
/// </para>
/// </remarks>
/// <param name="Mode">The catalogue name of the mode the frame was heard on, e.g.
/// <c>"qpsk2400-il2pc"</c> or <c>"afsk1200"</c>. This is an identity: consumers correlate it
/// against their configured mode and against their own mode catalogue, so it must be the same
/// string on every receiver that hears the same transmission. A diversity bank therefore does
/// NOT append its branch count here - bank width is static receiver configuration, identical
/// on every frame and changed by a receiver-local knob, and an identifier cannot vary with the
/// listener (issue #343). What a bank legitimately knows per frame - the winning branch - is
/// already carried structured, in <see cref="FrequencyOffsetHz"/> and <see cref="EmphasisDb"/>;
/// the receiver's own construction stays on <see cref="IModem.Mode"/>, where the daemon's logs
/// and waterfall read it.</param>
/// <param name="FrameBytes">Decoded AX.25 frame length in bytes.</param>
/// <param name="CorrectedBytes">Bytes repaired by forward error correction (Reed-Solomon,
/// IL2P and FX.25 framings). <c>null</c> for unprotected framings (classic HDLC), where no
/// error count exists - an FCS pass only proves zero residual errors.</param>
/// <param name="CrcValid">IL2P trailing-CRC state: true/false when the link runs IL2P+CRC,
/// <c>null</c> where the framing carries no trailer (plain IL2P, HDLC, FX.25).</param>
/// <param name="FrequencyOffsetHz">For multi-decoder banks, the frequency offset of the
/// branch that decoded the frame; <c>null</c> for single decoders. A persistent non-zero
/// value means the far station is off-frequency by about that much.</param>
/// <param name="EmphasisDb">For multi-decoder banks, the input pre-emphasis (dB/octave) of
/// the winning branch; <c>null</c> for single decoders. Persistent non-zero = the far
/// station's TX audio is twisted.</param>
/// <param name="HeaderType">Which IL2P encapsulation the frame arrived in - Type 1 translated
/// (the AX.25 header compressed into IL2P's own) or Type 0 transparent (the whole AX.25 frame
/// in the payload); <c>null</c> for framings that are not IL2P. Surfaced because it is the
/// first question worth asking about a frame that decoded cleanly and then would not yield
/// callsigns: the two types put the address field in different places, so which one it was
/// decides whether the payload is unusual or the decode is.</param>
/// <param name="PlainIl2p">The frame was read as plain IL2P, with no trailing CRC behind it: it
/// is standing on Reed-Solomon alone. A fact about the <em>decode</em>, and the one a display
/// should badge - <see cref="CrcValid"/> is <c>null</c> here, but it is also null for HDLC and
/// FX.25 and for every frame of a mode that has no CRC to check, so "no CRC was checked" and
/// "no CRC existed" are not the same question and one flag cannot answer both. True both for a
/// frame the second plain reading of an IL2P+CRC link produced (see
/// <see cref="Il2pReceiver"/>) and for every frame of a link that runs plain IL2P as its own
/// framing, because the guarantee behind them is identical.</param>
/// <param name="TrailerNearBits">For a frame only the plain reading produced: the Hamming
/// distance, in wire bits, between the 32 trailer bits that followed it and the trailer its
/// payload implies - present only when small enough to corroborate the frame (see
/// <see cref="Il2pReceiver.CorroborationMaxBits"/>), which is what delivered it despite
/// <see cref="CrcValid"/> being null. The usual cause is measured, not guessed: the transmit
/// pulse truncates at the end of the burst and the last symbols suffer for it, and the wire
/// format parks its only unprotected bytes exactly there. A corroborated frame is backed by
/// Reed-Solomon plus a near-exact trailer, evidence of the same order as a passing CRC;
/// badge it as its own thing, not as either "CRC OK" or "RS only".</param>
/// <param name="ErasedBytes">Bytes the decode erased on the receiver's own confidence
/// flags before Reed-Solomon repaired the frame - each costs one parity symbol where an
/// unlocated error costs two, so this is how a frame beyond the errors-only budget was
/// still read. Null when no erasures were needed (or the modem supplies no confidence);
/// see <see cref="M0LTE.Il2p.Il2pDecodeInfo.ErasedSymbols"/>.</param>
/// <param name="ChasedBits">Wire bits chase decoding flipped - the receiver's
/// least-confident bits, tried in combination after both errors-only decoding and the
/// erasure ladder failed, each accepted attempt still leaving Reed-Solomon parity in
/// reserve. The rescue for scattered bit errors too spread out for erasures, and the only
/// rescue the 2-parity IL2P header has at all. Null when no chase was needed (or the modem
/// supplies no confidence); see <see cref="M0LTE.Il2p.Il2pDecodeInfo.ChasedBits"/>.</param>
/// <param name="SnrDb">Strength of the burst this frame arrived on: mean in-band power
/// over the burst against a rolling minimum noise floor, in dB, measured by the channel's
/// own band tracker (<see cref="Packet.SoundModem.Channel.BurstSnrMonitor"/>) and rounded
/// to 0.1 dB so every consumer records the identical figure. <b>This is the band-tracker
/// convention, NOT the 3 kHz-referenced SNR the sim ladder and the Watterson masks use</b>
/// - the reference bandwidth is the modem's own band and the floor is its quietest recent
/// half second, so comparing this number against a mask row without converting is wrong.
/// Null when the band was quiet at decode time (a frame that cannot be attributed to
/// visible energy), when the modem's band was never measurable, or when the frame did not
/// come through a <see cref="Packet.SoundModem.Channel.SoundModemChannel"/>.</param>
/// <param name="PeakDbFs">How loud the audio this frame arrived on was: the loudest half
/// millisecond of the receive audio the frame itself occupied, in dBFS, measured by the
/// channel's own <see cref="Packet.SoundModem.Channel.FrameLevelMonitor"/>, rounded to 0.1 dB
/// so every consumer records the identical figure. <b>A measurement of the frame, not of the channel</b>:
/// the level meter's five-a-second reading covers whatever was on the input at the time, which
/// on a fast mode is mostly not this frame and on an FM receiver with the squelch open is mostly
/// the hiss between frames. Null when the modem cannot say where its frames were, when the frame
/// is older than the level history holds, or when the frame did not come through a
/// <see cref="Packet.SoundModem.Channel.SoundModemChannel"/>.</param>
/// <param name="Clipped">Whether the sound card ran out of codes anywhere in that same stretch -
/// judged on the card's own samples, before any resampling, which is the only place it is a fact
/// (see <see cref="Packet.SoundModem.Audio.InputLevelMeter.AddCardSamples"/>). Null where nothing
/// is handing the card's samples over, which is "not measured" and not "no": a station whose
/// audio arrives from a Flex or an ubersdr receiver has no converter of ours to have run out of
/// codes, and says so by leaving this null.</param>
/// <param name="MonitorOnly">The frame was <b>not</b> passed to the host: it reached
/// <see cref="IModem.FrameDecoded"/> and everything hanging off it - display, frame log,
/// journal, survey - but never the modem's constructor frame sink. A fact about what
/// <em>happened to</em> the frame rather than about the decode, which is why it is separate
/// from <see cref="PlainIl2p"/>. Set for a plain IL2P frame read by an IL2P+CRC link that was
/// not told to accept them (the default): the operator wants to see such a frame without
/// handing an RS-only frame to a host that asked for IL2P+CRC. Anything relaying frames onward
/// - the KISS server's quality sidecar included - must skip these, or it reports a frame its
/// peer never received.</param>
/// <param name="Level">
/// What <paramref name="PeakDbFs"/> and <paramref name="Clipped"/> came to, judged against the
/// limits of the modem that decoded this frame
/// (<see cref="Packet.SoundModem.Audio.FrameLevelLimits"/>). Set once, by the channel, at the
/// moment of the decode, so the frame log, the operator page and an uplinked monitor all carry
/// the same verdict without any of them knowing the rule. Null where there was nothing to judge:
/// a decoder that cannot place its frames in the audio, or one of our own transmissions.
/// </param>
/// <param name="PeakWorthShowing">
/// Whether the deciding modem's limits say <paramref name="PeakDbFs"/> is a figure worth putting
/// in front of an operator (<see cref="Packet.SoundModem.Audio.FrameLevelLimits.PeakWorthShowing"/>).
/// False on every mode whose slicer is a sign or an angle test, where the only level that means
/// anything is the converter's own rail and <paramref name="Level"/> says that already; true
/// where the slicer reads the level, which is the C4FSK pair and the 1200 baud AFSK family. Taken
/// at the decode alongside <paramref name="Level"/>, and for the same reason: the modem is the
/// only thing that knows, and a page matching a mode name to find out is how the C4FSK badge came
/// to be dead for a release. Null is "nothing said", which is a row from an older station or an
/// older log, and a figure that arrives without an answer is shown as it always was.
/// <b>The measurement is kept either way</b>: this hides a figure on a row and never a value in
/// the frame log or on the uplink.
/// </param>
public readonly record struct FrameQuality(
    string Mode,
    int FrameBytes,
    int? CorrectedBytes,
    bool? CrcValid,
    double? FrequencyOffsetHz = null,
    int? EmphasisDb = null,
    M0LTE.Il2p.Il2pHeaderType? HeaderType = null,
    bool PlainIl2p = false,
    bool MonitorOnly = false,
    int? TrailerNearBits = null,
    int? ErasedBytes = null,
    int? ChasedBits = null,
    double? SnrDb = null,
    double? PeakDbFs = null,
    bool? Clipped = null,
    Packet.SoundModem.Audio.FrameLevel? Level = null,
    bool? PeakWorthShowing = null)
{
    /// <summary>
    /// Whether <see cref="SnrDb"/> is a figure worth putting beside this frame on a row
    /// (<see cref="DecodeStanding.SnrWorthShowing"/>).
    /// </summary>
    /// <remarks>
    /// Derived from the two fields that say what stood behind the reading, so it cannot disagree
    /// with them, and read by the page, the burst tag and the uplink rather than each of them
    /// reapplying a rule. <b>The measurement is kept either way</b>: this hides a figure on a row
    /// and never a value in the frame log, the journal or the uplink.
    /// </remarks>
    public bool SnrWorthShowing => DecodeStanding.SnrWorthShowing(PlainIl2p, TrailerNearBits);

    /// <summary>
    /// Whether the callsigns read out of this frame may be presented as a station
    /// (<see cref="DecodeStanding.CallsignWorthShowing"/>).
    /// </summary>
    /// <remarks>
    /// Same seam and the same reason: a claim about who transmitted is a property of the decode,
    /// taken here where the three facts behind it live, and carried. The bytes and the callsigns
    /// read out of them still go to the frame log and the journal, which are records of what the
    /// station read; this decides only whether a row names a station.
    /// </remarks>
    public bool CallsignWorthShowing =>
        DecodeStanding.CallsignWorthShowing(PlainIl2p, TrailerNearBits, ChasedBits);
}

/// <summary>
/// What a reading of a frame established, and therefore which of the things a row can say about
/// it this station is entitled to say.
/// </summary>
/// <remarks>
/// <para>
/// One definition, in one place, because two carriers ask it: <see cref="FrameQuality"/> for a
/// frame as it is decoded, and <c>LoggedFrame</c> for the same frame read back out of the log
/// into a browser's opening backlog. A rule reapplied at each edge is how a badge came to be dead
/// for a release.
/// </para>
/// <para>
/// <b>Measured, not assumed.</b> On the GB7RDG 40 m slot, over a week of its own frame log, the
/// share of frames whose payload was never seen again - a payload seen exactly once on a band
/// where everything repeats all day is very likely a payload nobody sent - ran 3.5% on
/// CRC-verified frames, 1.5% on CRC-verified frames that chase decoding had rescued, 8 to 10% on
/// Reed-Solomon-only readings, and <b>75.6%</b> on Reed-Solomon-only readings that chase had
/// rescued. That last class is the one this withholds a callsign from. Chase decoding is not the
/// fault and is not capped here: behind a CRC it is the best-behaved class measured. Chase with
/// nothing checking it is. See docs/dev/false-decodes.md.
/// </para>
/// <para>
/// Not <see cref="DecodeConfidence"/>, which answers two neighbouring questions and neither of
/// these: which of several readings of one burst to name, and whether a reading is evidence that
/// anybody transmitted at all. This is narrower and is about one row's own wording - a frame can
/// be worth listing, worth logging and worth badging while still not being worth a callsign.
/// </para>
/// </remarks>
public static class DecodeStanding
{
    /// <summary>
    /// Whether nothing but Reed-Solomon's own arithmetic stood behind a reading: it arrived as
    /// plain IL2P, with no trailing CRC, and the trailer that followed it did not corroborate it
    /// either (<see cref="FrameQuality.TrailerNearBits"/>).
    /// </summary>
    /// <remarks>
    /// False for a verified CRC, for a corroborated trailer, and for HDLC and FX.25, whose FCS
    /// passed: in all of those something independent of the decoder agreed with the bytes.
    /// </remarks>
    public static bool NothingChecked(bool plainIl2p, int? trailerNearBits) =>
        plainIl2p && trailerNearBits is null;

    /// <summary>
    /// Whether a burst SNR belongs on a row for such a reading.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameQuality.SnrDb"/> is a band measurement and not a measurement of the frame:
    /// mean in-band power over a rolling <em>minimum</em> noise floor, which floors the figure
    /// near the 6 dB the burst gate demanded of every line in it, and which repeats the previous
    /// burst's figure for two seconds after a run ends (<c>BandActivityTracker</c>). Beside a
    /// frame something checked, that is a useful strength cue. Beside one nothing checked, it is
    /// what makes a row look like a real signal at a real strength - a fabricated frame emitted
    /// shortly after a real transmission on the same band inherits that transmission's number
    /// exactly, which is why the two were indistinguishable on the waterfall.
    /// </remarks>
    public static bool SnrWorthShowing(bool plainIl2p, int? trailerNearBits) =>
        !NothingChecked(plainIl2p, trailerNearBits);

    /// <summary>
    /// Whether the callsigns read out of such a reading may be presented as a station.
    /// </summary>
    /// <remarks>
    /// A callsign on a row is a claim, and this class cannot support one: Reed-Solomon alone, with
    /// the chase having flipped the receiver's least-confident bits until the parity closed, and
    /// nothing checking the result. The IL2P header carries two parity symbols, so mis-correction
    /// is cheap, and what it produces is a roll-call of the slot's real regulars one or two
    /// characters out - 16WBPQ and EVWBPQ for GB7BPQ, 16WLOX-2 for GB7LOX-2, EOSRSM-1 for EI0RSI -
    /// and, when it walks all the way back, the correct spelling of a station that did not
    /// transmit. Such a frame is still listed, still badged RS ONLY and still logged; it simply
    /// does not name anybody. Nothing downstream loses by it: the class is withheld from the host
    /// already, and the links pane already refuses it.
    /// </remarks>
    public static bool CallsignWorthShowing(bool plainIl2p, int? trailerNearBits, int? chasedBits) =>
        !(NothingChecked(plainIl2p, trailerNearBits) && chasedBits > 0);
}

/// <summary>
/// How much a reading of a transmission actually established, for choosing between two decoder
/// branches' copies of the same frame.
/// </summary>
/// <remarks>
/// <para>
/// A diversity bank routinely has several branches copy the same transmission, and since every
/// IL2P+CRC branch reads its bits both ways, they do not all establish the same thing about it.
/// One branch's IL2P+CRC reading may verify the trailer while another branch, closer to the
/// carrier and copying identical bytes, cannot - which is not hypothetical: on the committed
/// GB7RDG off-air capture the five low branches verify the frame and the four nearest the carrier
/// only manage the plain reading (<c>OffAirBpskTests</c>).
/// </para>
/// <para>
/// So the copy to report and deliver is the best-evidenced one, and nothing else gets to
/// outrank that: which branch happened to be best centred is a question about our receiver, and
/// whether the far station's CRC checked out is a question about the frame. Ranking on
/// <see cref="FrameQuality.MonitorOnly"/> instead looks equivalent and is not - that is the
/// operator's routing choice, identical across every branch of one bank, so with
/// <c>acceptPlainIl2p</c> on it says nothing at all and a verified frame gets reported as
/// RS-only by whichever branch was nearest.
/// </para>
/// </remarks>
internal static class DecodeEvidence
{
    /// <summary>Ranks a copy by what its reading proved, highest first: the trailing CRC verified,
    /// then the link's own reading with a trailer that did not verify, then a plain reading with
    /// no trailer behind it at all.</summary>
    internal static int RankOf(in FrameQuality quality) => quality switch
    {
        { CrcValid: true } => 2,
        { PlainIl2p: false } => 1,
        _ => 0,
    };
}
