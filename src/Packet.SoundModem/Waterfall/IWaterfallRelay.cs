namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Where a station's display stream goes when it is not going to a browser: the receive audio it
/// is hearing, its own transmissions as they are painted, the frames it decoded and the sentence
/// in its status chip.
/// </summary>
/// <remarks>
/// <para>The station side of the uplink of <c>docs/uplink-plan.md</c>. A station with no
/// <c>publish</c> block has no relay, and <see cref="WaterfallWebServer.Relay"/> is null: every
/// offer below is then a null check on a field that is already in cache, and nothing else about
/// the server changes at all.</para>
/// <para>An implementation is called on the station's own threads - the receive loop for audio,
/// the display pacer for transmitted audio, the decoder for frames - so it must return promptly
/// and must not block on a socket. It is also allowed to throw: the server catches and drops the
/// one message, because a website being unreachable is not a reason for a node to stop passing
/// traffic.</para>
/// </remarks>
public interface IWaterfallRelay
{
    /// <summary>
    /// Whether anybody at the far end is watching. False costs the station nothing: no audio is
    /// converted, blocked or offered while it is false.
    /// </summary>
    /// <remarks>
    /// It gates the audio and nothing else. Frames and the radio sentence are offered whether or
    /// not anybody is watching, because they are under a kilobit a second between them and they
    /// are what makes a quiet band look alive to somebody arriving an hour later. What the far
    /// end does with a frame it did not ask for is the far end's business.
    /// </remarks>
    bool Wanted { get; }

    /// <summary>
    /// A block of audio, exactly as the station's own modems and display see it.
    /// </summary>
    /// <param name="samples">
    /// The block. Received audio is what the station drew its own picture from, at the channel's
    /// sample rate; transmitted audio is what the display pacer has just released, so it arrives
    /// at the rate real time passes rather than in one lump per keyup, and it carries
    /// <see cref="WaterfallWebServer.TransmitDisplayGainDb"/>'s scaling because that too is what
    /// the picture is drawn from.
    /// </param>
    /// <param name="transmitted">
    /// False for audio the station heard, true for the station's own transmission.
    /// </param>
    /// <remarks>
    /// <para><b>One kind at a time.</b> A block is never half one and half the other, and nor is
    /// the stream: the two do not interleave, because received audio is offered only while the
    /// station is drawing it, which is exactly when it is not drawing a transmission. That holds
    /// across the drain after a key-up, where the pacer goes on painting a burst the sound card
    /// has not finished playing while the input has already started delivering again; those
    /// received blocks are kept out of the station's own picture and out of this. A client can
    /// therefore accumulate fixed-length blocks per direction and never has to flush a short one
    /// at a switch.</para>
    /// <para><b>Not interleaved is not the same as not concurrent</b>, and an implementation that
    /// keeps state has to know the difference. The two kinds do not interleave in the stream, but
    /// the two calls can be in flight at once: received audio is offered on the station's audio
    /// read thread and a transmission from the display pacer's timer callback, and at the tail of
    /// a key-up the receive tap can pass its gate, queue on the display lock behind the pacer, and
    /// be released exactly as the pacer moves on to its offers. Neither call is made under a lock,
    /// so anything an implementation keeps between calls must be safe for two threads.</para>
    /// <para>The span is the caller's buffer and is not valid after the call returns: an
    /// implementation that keeps the samples must copy them.</para>
    /// <para>No display lock is held across this call, so a slow implementation costs itself and
    /// the station's own audio thread rather than the display. It still costs the audio thread,
    /// which is the reason for the "return promptly" rule above: this is called from it.</para>
    /// </remarks>
    void Audio(ReadOnlySpan<float> samples, bool transmitted);

    /// <summary>A frame this station decoded, or sent, or was told about.</summary>
    void Frame(RelayedFrame frame);

    /// <summary>
    /// The sentence in the page's status chip, as <see cref="WaterfallWebServer.SetRadioStatus"/>
    /// was given it. Null is "there is nothing to say", which is a state of its own and not the
    /// absence of a call.
    /// </summary>
    void Radio(string? status);
}

/// <summary>
/// One frame, in the shape the uplink carries it: everything the page's <c>frame</c> message says
/// about it, when it happened, and the bytes it was read from.
/// </summary>
/// <remarks>
/// <para>The wire format is section 4.2 of <c>docs/uplink-plan.md</c> and that document is
/// normative for it. This record is the same set of fields either side of that wire: a station
/// fills one in and sends it, and a monitor parses one out and hands it to
/// <see cref="WaterfallWebServer.PushFrame"/>.</para>
/// <para>There is no line index here. A frame is tagged onto the waterfall line the display had
/// reached when it was listed, and on a monitor that is the monitor's own line count over the
/// audio it has been given, not the station's.</para>
/// </remarks>
public sealed record RelayedFrame
{
    /// <summary>Which modem heard or sent it.</summary>
    public required int SubChannel { get; init; }

    /// <summary>Its mode string, as the modem reported it.</summary>
    public required string Mode { get; init; }

    /// <summary>Source callsign where the frame carried one.</summary>
    public string? From { get; init; }

    /// <summary>Destination callsign where the frame carried one.</summary>
    public string? To { get; init; }

    /// <summary>Decoded frame length in bytes.</summary>
    public int LengthBytes { get; init; }

    /// <summary>Burst SNR where anything measured one.</summary>
    public double? SnrDb { get; init; }

    /// <summary>How many display lines the burst occupied, where the band tracker saw it.</summary>
    public int? BurstLines { get; init; }

    /// <summary>Measured carrier offset, where the decoder measured one.</summary>
    public double? OffsetHz { get; init; }

    /// <summary>Bytes FEC repaired, where the framing counts them.</summary>
    public int? CorrectedBytes { get; init; }

    /// <summary>CRC verdict, where the framing carries one.</summary>
    public bool? CrcValid { get; init; }

    /// <summary>True for a station identification heard by an id-beacon ghost.</summary>
    public bool IdBeacon { get; init; }

    /// <summary>True for a frame this station sent.</summary>
    public bool Transmitted { get; init; }

    /// <summary>How far a transmission was shifted to suit the station it was addressed to.</summary>
    public double? TransmitTrimHz { get; init; }

    /// <summary>Why a frame's addresses would not read, where they would not.</summary>
    public string? Note { get; init; }

    /// <summary>Which IL2P encapsulation carried it, where one did.</summary>
    public string? HeaderType { get; init; }

    /// <summary>The frame's bytes as hex, on a frame whose addresses would not read.</summary>
    public string? FrameHex { get; init; }

    /// <summary>Read as plain IL2P: Reed-Solomon alone stood behind it.</summary>
    public bool PlainIl2p { get; init; }

    /// <summary>Listed and logged, but not passed to the station's own KISS hosts.</summary>
    public bool MonitorOnly { get; init; }

    /// <summary>
    /// How loud the audio this frame arrived on was, in dBFS, over the frame's own stretch of it
    /// (see <see cref="Modems.FrameQuality.PeakDbFs"/>). Null from a station running a version
    /// that does not measure it, which is what makes it safe to add: a monitor lists such a row
    /// exactly as it always did.
    /// </summary>
    public double? PeakDbFs { get; init; }

    /// <summary>Whether that station's card ran out of codes during the same stretch; null where
    /// it was not measured.</summary>
    public bool? Clipped { get; init; }

    /// <summary>When the station decoded it, or sent it (UTC).</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The AX.25 bytes, where the frame had any. Null on an id-beacon ghost and on a frame
    /// reported by a decoder that does not hand its bytes over, which is what ARDOP does.
    /// </summary>
    /// <remarks>
    /// This is what a monitor reads into its own link observer, so that the links panel is one
    /// implementation over the same bytes rather than two that can disagree, and so that the fold
    /// survives the station going off the air.
    /// </remarks>
    public byte[]? Raw { get; init; }
}
