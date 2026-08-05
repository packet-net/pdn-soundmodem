using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>
/// The IL2P receive seam. Every IL2P-carrying modem pushes its demodulated bits through one of
/// these rather than straight into an <see cref="Il2pDeframer"/>, so that the one thing that
/// varies between them - whether a link running IL2P+CRC will <em>also</em> accept plain IL2P,
/// which is off unless an operator asks for it - lives in a single place instead of being
/// bolted onto each of the eight modems that end up at <c>crcMode: true</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the second reading exists.</b> A station running <c>bpsk300-il2pc</c> on the 40 m slot
/// (7.0516 MHz, 2150 Hz audio) had a signal survey full of bursts it could not read. Run offline
/// through the whole 12 kHz mode family, one of them decoded as
/// <c>bpsk300-nocrc @ 2116 Hz -> 46 B GB7BPQ&gt;BEACON: =5828.54N/00612.69W- {BPQ32}</c>, with the
/// carrier measured at ~2123 Hz - 27 Hz from the station's own centre, well inside its diversity
/// bank. Same bank, same centre, same audio: <c>bpsk300</c> did not decode it and
/// <c>bpsk300-nocrc</c> did. The bits were demodulating perfectly and the frame was being thrown
/// away at the IL2P+CRC check, because that BPQ32 node transmits plain IL2P with no trailing CRC.
/// Right frequency, right modulation, right baud, wrong IL2P variant.
/// </para>
/// <para>
/// <b>Why the two readings cannot simply both be delivered.</b> Measured against M0LTE.Il2p 0.1.2
/// (<c>Il2pReceiverTests</c>, which pins it): a <c>crcMode: false</c> deframer handed a well-formed
/// IL2P+CRC frame emits the same AX.25 frame, byte for byte, exactly
/// <c>Il2pCodec.TrailingCrcWireLength * 8</c> = 32 bits <em>before</em> the <c>crcMode: true</c>
/// one does, at every payload size. It sizes the payload from the header, decodes it and goes back
/// to hunting, leaving the four trailer bytes to be hunted through as though they were channel
/// noise. So running both deframers naively would deliver every ordinary frame on the channel
/// twice, plain copy first - far worse than the bug being fixed. Instead the plain reading is
/// <em>held</em> for those 32 bits: if the link's own reading emits the same bytes within them it
/// wins and the held copy is dropped; otherwise the held copy is released, and that is the plain
/// IL2P frame this option is here for.
/// </para>
/// <para>
/// <b>What it costs.</b> A plain IL2P frame is validated by Reed-Solomon alone - there is no CRC
/// behind it, which is the entire reason the +CRC variant exists. RS will occasionally "correct"
/// noise into a plausible-looking frame, and on this path nothing catches that. Frames that arrive
/// this way carry <c>CrcValid: null</c> (the honest answer: no CRC was checked) rather than
/// <c>true</c>. There is also no way to tell a plain frame from an IL2P+CRC frame whose trailer
/// was corrupted, so with the option on, the latter is delivered too instead of being counted as
/// a CRC failure.
/// </para>
/// <para>
/// Nothing here allocates per bit: the held frame is the array the deframer had already allocated
/// for it, and the byte comparisons happen only when a frame is emitted.
/// </para>
/// </remarks>
internal sealed class Il2pReceiver
{
    /// <summary>How far behind the plain reading the IL2P+CRC reading of the same frame arrives.
    /// The trailer is a fixed four wire bytes, so this is exact rather than a guess, and it is
    /// exact at every payload size (measured; see the remarks above).</summary>
    private static readonly long CrcTrailerBits = Il2pCodec.TrailingCrcWireLength * 8;

    /// <summary>
    /// How far the plain reading may lag the link's own reading and still be recognised as a
    /// second copy of the same transmission. The two normally run in lockstep with the plain one
    /// 32 bits ahead, but they can fall out of step - a collection that turns out to be rubbish is
    /// rewound and its bits reconsidered (<c>Il2pDeframer.Backtrack</c>) - so a plain copy
    /// can also arrive late. One maximum-length IL2P frame is the longest thing a rewound
    /// collection could have been chewing on, which makes it the bound rather than a round number.
    /// </summary>
    /// <remarks>
    /// Deliberately one-way: a plain copy of something already delivered is dropped, but a frame
    /// the link's own reading produces is <em>never</em> suppressed because a plain copy went out
    /// first. The reverse guard would have to swallow a delivery on the ordinary path, and over a
    /// window this wide it would eventually swallow a genuine retransmission - two identical
    /// frames seconds apart is normal AX.25 behaviour, not a duplicate. Dropping a rare extra copy
    /// is worth more than losing a real frame, which is also why the diversity banks' own content
    /// dedupe keeps its window to a few seconds.
    /// </remarks>
    private static readonly long LateReadingGuardBits =
        (Il2pCodec.HeaderWireLength + Il2pBlockLayout.Compute(Il2pCodec.MaxPayloadBytes).WireLength) * 8;

    private readonly Action<byte[], Il2pDecodeInfo> _frameReceived;
    private readonly Il2pDeframer _deframer;
    private readonly Il2pDeframer? _plainDeframer;
    private long _bitsPushed;
    private byte[]? _heldFrame;
    private Il2pDecodeInfo _heldInfo;
    private long _releaseHeldAtBit;
    private byte[]? _lastDelivered;
    private long _lastDeliveredAtBit;

    /// <summary>Creates the receiver.</summary>
    /// <param name="frameReceived">Called synchronously from <see cref="PushBit"/> with each
    /// decoded AX.25 frame and its decode diagnostics, exactly as
    /// <see cref="Il2pDeframer"/> would call it.</param>
    /// <param name="crcMode">True when the link runs IL2P+CRC (both stations must agree).</param>
    /// <param name="acceptPlainIl2p">Also read the same bits as plain IL2P, for a neighbour that
    /// sends IL2P without the trailing CRC. Off by default, and inert unless
    /// <paramref name="crcMode"/> is on: a link already reading plain IL2P reads it with the one
    /// deframer it has.</param>
    /// <param name="syncWord">Non-standard 24-bit sync word (the MMDVM-TNC "Mode 2" C4FSK
    /// framing the NinoTNC C4FSK modes inherit); null for IL2P's own.</param>
    public Il2pReceiver(
        Action<byte[], Il2pDecodeInfo> frameReceived, bool crcMode,
        bool acceptPlainIl2p = false, int? syncWord = null)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _frameReceived = frameReceived;
        _deframer = syncWord is { } sync
            ? new Il2pDeframer(OnDeframed, crcMode, sync)
            : new Il2pDeframer(OnDeframed, crcMode);
        if (acceptPlainIl2p && crcMode)
        {
            _plainDeframer = syncWord is { } plainSync
                ? new Il2pDeframer(OnPlainDeframed, crcMode: false, plainSync)
                : new Il2pDeframer(OnPlainDeframed, crcMode: false);
        }
    }

    /// <summary>Whether this receiver is also reading the bits as plain IL2P.</summary>
    public bool AcceptsPlainIl2p => _plainDeframer is not null;

    /// <summary>Pushes one received bit (0/1) through both readings.</summary>
    public void PushBit(int bit)
    {
        _bitsPushed++;

        // The link's own reading goes first. At the bit where both readings have the same frame,
        // this is what cancels the held plain copy before the release check below could let it
        // out - which is the whole reason the order here is not arbitrary.
        _deframer.PushBit(bit);
        if (_plainDeframer is null)
        {
            return;
        }

        _plainDeframer.PushBit(bit);
        if (_heldFrame is not null && _bitsPushed >= _releaseHeldAtBit)
        {
            ReleaseHeld();
        }
    }

    /// <summary>Abandons any frame in progress and returns to hunting for a sync word - see
    /// <see cref="Il2pDeframer.Reset"/>. Every caller does this on the DCD or burst falling
    /// edge.</summary>
    public void Reset()
    {
        // Release a held plain frame rather than discard it: the carrier has stopped, so the
        // 32 bits the CRC reading is waiting for are never going to arrive and it is never going
        // to claim this frame. A plain frame that ends where the transmission ends is exactly the
        // case this option is for, and it is the usual case: on a 300 baud BPSK link DCD drops
        // about 24 bit times after the last symbol, before the 32-bit hold would have expired on
        // its own.
        ReleaseHeld();
        _deframer.Reset();
        _plainDeframer?.Reset();
    }

    /// <summary>The link's own reading produced a frame: deliver it, and drop any held plain copy
    /// of the same bytes, which is this same transmission read a second time.</summary>
    private void OnDeframed(byte[] frame, Il2pDecodeInfo info)
    {
        if (_heldFrame is not null && _heldFrame.AsSpan().SequenceEqual(frame))
        {
            _heldFrame = null;
        }

        _lastDelivered = frame;
        _lastDeliveredAtBit = _bitsPushed;
        _frameReceived(frame, info);
    }

    /// <summary>The plain reading produced a frame: hold it until the link's own reading has had
    /// its chance at the same transmission.</summary>
    private void OnPlainDeframed(byte[] frame, Il2pDecodeInfo info)
    {
        // Already delivered by the link's own reading, which got there first this time. See
        // LateReadingGuardBits for how the plain reading ends up behind rather than ahead.
        if (_lastDelivered is not null
            && _bitsPushed - _lastDeliveredAtBit <= LateReadingGuardBits
            && _lastDelivered.AsSpan().SequenceEqual(frame))
        {
            return;
        }

        // Two plain frames inside 32 bits of each other cannot happen - the shortest IL2P frame
        // on the wire is an order of magnitude longer than that - but if it somehow did, releasing
        // the older one keeps both rather than losing one silently.
        ReleaseHeld();
        _heldFrame = frame;
        _heldInfo = info;
        _releaseHeldAtBit = _bitsPushed + CrcTrailerBits;
    }

    /// <summary>Delivers the held plain frame, if there is one. The field is cleared before the
    /// sink is called so that a sink which pushes more bits cannot see it twice.</summary>
    private void ReleaseHeld()
    {
        if (_heldFrame is not { } frame)
        {
            return;
        }

        Il2pDecodeInfo info = _heldInfo;
        _heldFrame = null;
        _frameReceived(frame, info);
    }
}
