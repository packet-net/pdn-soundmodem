using M0LTE.Il2p;

namespace Packet.SoundModem.Modems;

/// <summary>
/// What the receiver decided about a frame, handed to the sink alongside the frame itself.
/// </summary>
/// <remarks>
/// Two facts, deliberately separate, and copied verbatim onto <see cref="FrameQuality"/> by every
/// modem that owns one of these: what the decode was, and where the frame is allowed to go. They
/// answer different questions and a display wants the first while the KISS host path wants the
/// second.
/// </remarks>
/// <param name="PlainIl2p">The reading that produced this frame had no trailing CRC behind it.</param>
/// <param name="MonitorOnly">This frame must not be passed to the host - see
/// <see cref="FrameQuality.MonitorOnly"/>.</param>
/// <param name="TrailerNearBits">For a frame only the plain reading produced: how many of the
/// 32 trailer wire bits that followed it differ from the trailer its payload implies, when
/// that distance is small enough to corroborate the frame - see
/// <see cref="Il2pReceiver.CorroborationMaxBits"/>. Null when the trailer was not close
/// (or was never seen whole).</param>
internal readonly record struct Il2pDelivery(bool PlainIl2p, bool MonitorOnly, int? TrailerNearBits = null);

/// <summary>
/// The IL2P receive seam. Every IL2P-carrying modem pushes its demodulated bits through one of
/// these rather than straight into an <see cref="Il2pDeframer"/>, so that the one thing that
/// varies between them - what a link running IL2P+CRC does about plain IL2P - lives in a single
/// place instead of being bolted onto each of the eight modems that end up at
/// <c>crcMode: true</c>.
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
/// <b>The second reading always runs; only its destination is configurable.</b> An IL2P+CRC link
/// reads its bits both ways whatever the operator asked for, because a station that cannot read a
/// neighbour cannot tell you it is there, and "nothing decoded" and "a CRC-less neighbour you are
/// structurally deaf to" look identical from the outside. What <c>acceptPlainIl2p</c> decides is
/// narrower and is the only part an operator should have to think about: whether such a frame is
/// <em>passed to the host</em>. Off (the default) it is <see cref="Il2pDelivery.MonitorOnly"/> -
/// it reaches the display, the frame log, the journal and the survey, and stops there. IL2P+CRC
/// exists precisely because Reed-Solomon alone was letting too much corrupt traffic through, so a
/// host that asked for IL2P+CRC gets IL2P+CRC; seeing what the band is doing is a different
/// question from feeding it to a node.
/// </para>
/// <para>
/// <b>Why the two readings cannot simply both be delivered.</b> Measured against M0LTE.Il2p 0.1.2
/// (<c>Il2pReceiverTests</c>, which pins it): a <c>crcMode: false</c> deframer handed a well-formed
/// IL2P+CRC frame emits the same AX.25 frame, byte for byte, exactly
/// <c>Il2pCodec.TrailingCrcWireLength * 8</c> = 32 bits <em>before</em> the <c>crcMode: true</c>
/// one does, at every payload size. It sizes the payload from the header, decodes it and goes back
/// to hunting, leaving the four trailer bytes to be hunted through as though they were channel
/// noise. So running both deframers naively would emit every ordinary frame on the channel twice,
/// plain copy first - far worse than the bug being fixed, and now that the second reading runs on
/// every IL2P+CRC link it would be far worse on every one of them. Instead the plain reading is
/// <em>held</em> for those 32 bits: if the link's own reading emits the same bytes within them it
/// wins and the held copy is dropped; otherwise the held copy is released, and that is the plain
/// IL2P frame all of this is here for.
/// </para>
/// <para>
/// <b>What a plain frame is worth.</b> It is validated by Reed-Solomon alone - there is no CRC
/// behind it, which is the entire reason the +CRC variant exists. RS will occasionally "correct"
/// noise into a plausible-looking frame, and on this path nothing catches that. Frames that arrive
/// this way carry <c>CrcValid: null</c> (the honest answer: no CRC was checked) and
/// <c>PlainIl2p: true</c> (the badge-able one: nothing but RS stood behind it). There is also no
/// way to tell a plain frame from an IL2P+CRC frame whose trailer was corrupted, so the latter
/// comes through here too rather than being counted as a CRC failure - which is a good reason to
/// look at such a row and a poor reason to hand it to a node.
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

    /// <summary>
    /// Largest Hamming distance, in wire bits, at which the 32 trailer bits following a
    /// plain-only frame still corroborate it. The payload implies the trailer exactly - the
    /// trailing CRC is a function of the AX.25 bytes - so a received trailer that is merely
    /// grazed is strong evidence the Reed-Solomon-validated payload is genuine: a frame RS
    /// hallucinated from noise implies a trailer uncorrelated with the received bits, and
    /// the chance of an uncorrelated 32-bit word landing within 4 bits is
    /// sum(C(32,i), i&lt;=4) / 2^32 ~ 1.1e-5 - the same order as the trailing CRC check's own
    /// false-accept (~2^-16). Why grazed trailers are the common case is measured, not
    /// assumed: on the GB7RDG 24 h miss corpus, 29 of 32 byte-exact frames failed only the
    /// trailer, with the damage clustered in the last few bits - the transmit pulse
    /// truncates at the end of the burst and the matched filter loses the final symbols, and
    /// the IL2P+CRC wire format parks its only unprotected bytes exactly there.
    /// </summary>
    internal const int CorroborationMaxBits = 4;

    private readonly Action<byte[], Il2pDecodeInfo, Il2pDelivery> _frameReceived;
    private readonly Il2pDeframer _deframer;
    private readonly Il2pDeframer? _plainDeframer;
    private readonly Il2pDelivery _ownReading;
    private readonly Il2pDelivery _plainReading;
    private long _bitsPushed;
    private byte[]? _heldFrame;
    private Il2pDecodeInfo _heldInfo;
    private long _releaseHeldAtBit;
    private byte[]? _lastDelivered;
    private long _lastDeliveredAtBit;
    private uint _trailerBits;
    private int _trailerBitCount;
    private readonly uint _syncWord;
    private uint _syncShift;

    /// <summary>Creates the receiver.</summary>
    /// <param name="frameReceived">Called synchronously from <see cref="PushBit(int, float)"/> with each
    /// decoded AX.25 frame, its decode diagnostics and where the frame is allowed to go, much as
    /// <see cref="Il2pDeframer"/> would call it. A caller that ignores the
    /// <see cref="Il2pDelivery"/> passes plain IL2P frames straight to its host, which is
    /// precisely what the default must not do.</param>
    /// <param name="crcMode">True when the link runs IL2P+CRC (both stations must agree).</param>
    /// <param name="acceptPlainIl2p">Pass plain IL2P frames - a neighbour that sends IL2P without
    /// the trailing CRC - on to the host as well as to the display. Off by default, in which case
    /// they are still read, and still reported, but marked
    /// <see cref="Il2pDelivery.MonitorOnly"/>. Inert unless <paramref name="crcMode"/> is on: a
    /// link already reading plain IL2P reads it with the one deframer it has, and its frames are
    /// its own traffic rather than a second opinion.</param>
    /// <param name="syncWord">Non-standard 24-bit sync word (the MMDVM-TNC "Mode 2" C4FSK
    /// framing the NinoTNC C4FSK modes inherit); null for IL2P's own.</param>
    public Il2pReceiver(
        Action<byte[], Il2pDecodeInfo, Il2pDelivery> frameReceived, bool crcMode,
        bool acceptPlainIl2p = false, int? syncWord = null)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        _frameReceived = frameReceived;
        _syncWord = (uint)(syncWord ?? Il2pCodec.SyncWord) & SyncMask;

        // On a crcMode: false link the one deframer IS the plain reading, and its frames are the
        // link's own traffic: RS-only, so worth badging, and delivered, because that is the mode
        // the operator configured.
        _ownReading = new Il2pDelivery(PlainIl2p: !crcMode, MonitorOnly: false);
        _plainReading = new Il2pDelivery(PlainIl2p: true, MonitorOnly: !acceptPlainIl2p);
        _deframer = syncWord is { } sync
            ? new Il2pDeframer(OnDeframed, crcMode, sync)
            : new Il2pDeframer(OnDeframed, crcMode);

        // Unconditional on an IL2P+CRC link, where it used to wait for acceptPlainIl2p. The
        // option now decides where a plain frame goes, not whether it is read at all, and a
        // station cannot report a neighbour it never demodulated. See the remarks for the CPU
        // this costs and why it is worth it.
        if (crcMode)
        {
            _plainDeframer = syncWord is { } plainSync
                ? new Il2pDeframer(OnPlainDeframed, crcMode: false, plainSync)
                : new Il2pDeframer(OnPlainDeframed, crcMode: false);
        }
    }

    /// <summary>
    /// Raised, synchronously from <see cref="PushBit(int, float)"/>, on the bit that completes
    /// the sync word - where the frame's own bits begin, for a caller counting samples.
    /// </summary>
    /// <remarks>
    /// <para><b>Watched here rather than asked of the deframer</b>, which is
    /// <see cref="Il2pDeframer"/> in a package that reports frames and nothing about where they
    /// started. This is the same 24 bits the deframer hunts, taken from the same published
    /// constant (<see cref="Il2pCodec.SyncWord"/>, or the non-standard word a C4FSK link uses),
    /// shifted in most-significant bit first as the wire carries it - so it fires on the bit the
    /// deframer starts collecting on, and one shift, one mask and one compare is the whole
    /// cost.</para>
    /// <para>Both readings of an IL2P+CRC link share it: they hunt the same word over the same
    /// bits and start together, and which of them eventually delivers the frame does not move
    /// where the frame began. See <see cref="Modems.FrameSpan"/> for what reads it.</para>
    /// </remarks>
    public Action? SyncFound { get; set; }

    /// <summary>The sync word is 24 bits, whichever word a link uses.</summary>
    private const uint SyncMask = 0xFFFFFF;

    /// <summary>Whether this receiver runs a second, plain reading alongside the link's own -
    /// true on every IL2P+CRC link, false on one that is already reading plain IL2P.</summary>
    public bool ReadsPlainIl2p => _plainDeframer is not null;

    /// <summary>Whether a frame that only the plain reading produced is passed to the host, as
    /// opposed to being reported and withheld.</summary>
    public bool DeliversPlainIl2p => !_plainReading.MonitorOnly;

    /// <summary>
    /// Cumulative count of sync-found-but-unrecoverable events across this receiver's readings:
    /// every time a deframer matched the IL2P sync word, collected the frame the header implied,
    /// and could not bring it back through Reed-Solomon (forwards
    /// <see cref="Il2pDeframer.RsFailures"/>). This is the only observable form of "there was a
    /// transmission here and we lost it" - a decode failure with no sync found leaves no trace at
    /// all - which makes the delta across a burst the burst-verdict instrument's
    /// sync-without-decode signal.
    /// </summary>
    /// <remarks>
    /// On an IL2P+CRC link both readings hunt the same bits, so one damaged transmission
    /// typically bumps this twice (and a diversity bank multiplies it again per branch). Treat a
    /// positive delta as "sync was found and the frame was unrecoverable", never as a count of
    /// lost transmissions. Noise can also tick it - a 24-bit sync word surfaces from random bits
    /// now and then - which is why callers measure deltas across a DCD-bounded burst rather than
    /// reading the counter cold.
    /// </remarks>
    public long RsFailures => _deframer.RsFailures + (_plainDeframer?.RsFailures ?? 0);

    /// <summary>
    /// Cumulative count of frames the link's own IL2P+CRC reading recovered through Reed-Solomon
    /// whose trailing CRC then failed to verify (forwards <see cref="Il2pDeframer.CrcFailures"/>;
    /// always zero on a plain-IL2P link, which has no trailer to check). Such a frame is not
    /// lost - the plain reading releases it as a monitor copy - but the counter is the honest
    /// record that the link's own reading got all the way to the trailer and was refused there.
    /// </summary>
    public long CrcFailures => _deframer.CrcFailures;

    /// <summary>Pushes one received bit (0/1) through both readings.</summary>
    public void PushBit(int bit) => PushBit(bit, 1f);

    /// <summary>Pushes one received bit with the demodulator's confidence in it, which both
    /// readings use to retry failed Reed-Solomon blocks with the weakest bytes erased - see
    /// <see cref="Il2pDeframer.PushBit(int, float)"/>. Lower = less reliable; only the
    /// ordering matters, but values must sit below 1 for the deframer to engage.</summary>
    public void PushBit(int bit, float confidence)
    {
        _bitsPushed++;
        _syncShift = ((_syncShift << 1) | (uint)(bit & 1)) & SyncMask;
        if (_syncShift == _syncWord)
        {
            SyncFound?.Invoke();
        }

        // While a plain frame is held, the wire is delivering the very bits the hold exists
        // to wait for - the trailer. Collect them for the corroboration check at release.
        if (_heldFrame is not null && _trailerBitCount < Il2pCodec.TrailingCrcWireLength * 8)
        {
            _trailerBits = (_trailerBits << 1) | (uint)(bit & 1);
            _trailerBitCount++;
        }

        // The link's own reading goes first. At the bit where both readings have the same frame,
        // this is what cancels the held plain copy before the release check below could let it
        // out - which is the whole reason the order here is not arbitrary.
        _deframer.PushBit(bit, confidence);
        if (_plainDeframer is null)
        {
            return;
        }

        _plainDeframer.PushBit(bit, confidence);
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

        // The late-reading guard must not survive the burst it was armed in. Its window is
        // sized for deframer skew within one transmission (a rewound collection), but at
        // 300 baud that many bits is over half a minute of wall clock - long enough that a
        // station retransmitting the identical frame in its next burst would have the plain
        // copy of the retransmission swallowed as a "late second reading" of the first, when
        // the retransmission is the only copy whose trailer never verified. The GB7RDG
        // off-air fixture holds exactly that pair, 12.5 s apart. Skew cannot cross a burst
        // boundary - Reset abandons both collections - so clearing the guard here costs
        // nothing and un-deafens the receiver to in-window retransmissions.
        _lastDelivered = null;
    }

    /// <summary>The link's own reading produced a frame: emit it, and drop any held plain copy
    /// of the same bytes, which is this same transmission read a second time.</summary>
    private void OnDeframed(byte[] frame, Il2pDecodeInfo info)
    {
        if (_heldFrame is not null && _heldFrame.AsSpan().SequenceEqual(frame))
        {
            _heldFrame = null;
        }

        _lastDelivered = frame;
        _lastDeliveredAtBit = _bitsPushed;
        _frameReceived(frame, info, _ownReading);
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
        _trailerBits = 0;
        _trailerBitCount = 0;
    }

    /// <summary>Emits the held plain frame, if there is one, marked as the plain reading's and
    /// with the operator's answer about where it may go - unless the trailer bits that followed
    /// it corroborate the frame, in which case it is delivered. The field is cleared before the
    /// sink is called so that a sink which pushes more bits cannot see it twice.</summary>
    private void ReleaseHeld()
    {
        if (_heldFrame is not { } frame)
        {
            return;
        }

        Il2pDecodeInfo info = _heldInfo;
        _heldFrame = null;

        // Corroboration: re-derive the trailer the payload implies and measure how far the
        // received trailer bits sit from it. Only when all 32 arrived - a burst that ended
        // before the trailer (the Reset release path) has nothing whole to measure. A genuine
        // plain-IL2P station fails this naturally: nothing follows its frames but noise or the
        // next preamble, half the bits disagree, and the frame stays a plain reading.
        if (_trailerBitCount == Il2pCodec.TrailingCrcWireLength * 8)
        {
            byte[] expected = Il2pCodec.Encode(frame, appendCrc: true);
            uint expectedBits = 0;
            for (int i = expected.Length - Il2pCodec.TrailingCrcWireLength; i < expected.Length; i++)
            {
                expectedBits = (expectedBits << 8) | expected[i];
            }

            int distance = System.Numerics.BitOperations.PopCount(_trailerBits ^ expectedBits);
            if (distance <= CorroborationMaxBits)
            {
                _frameReceived(frame, info, _plainReading with
                {
                    MonitorOnly = false,
                    TrailerNearBits = distance,
                });
                return;
            }
        }

        _frameReceived(frame, info, _plainReading);
    }
}
