using M0LTE.Fec;

namespace Packet.SoundModem.Hdlc;

/// <summary>
/// Streaming HDLC deframer for AX.25: hunts 0x7E flags in a logical-bit stream
/// (post-NRZI-decode), removes stuffed zeros, assembles LSB-first bytes and emits frames
/// whose CRC-16/X-25 frame check sequence verifies. Seven or more consecutive ones abort
/// the frame in progress. One instance per receive bit stream; not thread-safe.
/// </summary>
/// <remarks>
/// With an <see cref="HdlcRepairPolicy"/> the deframer also attempts confidence-ordered
/// repair of frames that failed their FCS or lost their bit alignment - see
/// <see cref="HdlcRepairPolicy"/> for the case, the budgets and the measured false-pass
/// record. Repairs are delivered through <see cref="FrameRepaired"/>, never through the
/// ordinary sink: a repaired frame's bytes were chosen by a search, and a caller decides for
/// itself what standing to give them (the modems mark them with
/// <see cref="Modems.FrameQuality.ChasedBits"/> and hold them against the clean deliveries
/// around them). Repair ordering needs the soft magnitudes of
/// <see cref="PushBit(int, float)"/>; hard-bit callers should leave repair off.
/// </remarks>
public sealed class HdlcDeframer
{
    /// <summary>Shortest emitted frame in bytes, FCS excluded: a v2.2 minimal frame is
    /// destination + source + control = 15 bytes.</summary>
    public const int MinFrameBytes = 15;

    /// <summary>Longest frame the repair engine works on, FCS included. Longer accumulations
    /// still deframe and verify normally - they are only never repair candidates, which
    /// keeps the per-deframer soft-decision buffers a bounded 16 kB rather than tracking
    /// <see cref="_maxFrameBytes"/>: frames that long are vanishingly rare on the channels
    /// this serves (APRS runs to ~140 bytes) and a repair's search grows with length.</summary>
    private const int RepairMaxFrameBytes = 512;

    private readonly int _maxFrameBytes;
    private readonly byte[] _buffer;
    private readonly Action<byte[]> _frameReceived;
    private readonly HdlcRepairPolicy? _repair;

    // Repair state, allocated only when a policy is set: soft magnitudes of the assembled
    // frame bits (LSB-first wire order), the raw stuffed-domain stream between the flags
    // with its own softs (what a bit-slip realignment rebuilds from), and a reassembly
    // scratch. All bounded by RepairMaxFrameBytes.
    private readonly float[]? _soft;
    private readonly byte[]? _raw;
    private readonly float[]? _rawSoft;
    private readonly byte[]? _reassemble;
    private int _softCount;
    private int _rawCount;

    private int _flagShift;      // last 8 line bits, newest in bit 0
    private int _onesRun;        // consecutive logical 1s (for destuff/abort)
    private int _byteShift;      // bits of the byte being assembled (LSB first)
    private int _bitCount;       // bits collected into the current byte
    private int _length;         // bytes collected for the current frame
    private bool _inFrame;

    /// <summary>Creates a deframer delivering good frames (FCS stripped) to
    /// <paramref name="frameReceived"/>.</summary>
    /// <param name="frameReceived">Called synchronously from <see cref="PushBit"/> for every
    /// frame whose FCS verifies.</param>
    /// <param name="maxFrameBytes">Upper bound on the frame size collected (FCS included);
    /// larger accumulations are discarded as noise. Default fits AX.25 with a 1023-byte
    /// payload comfortably.</param>
    /// <param name="repair">Opt-in confidence-ordered repair of FCS failures and bit slips -
    /// see <see cref="HdlcRepairPolicy"/>. Null (the default) is the historical behaviour:
    /// a failed frame is dropped.</param>
    public HdlcDeframer(Action<byte[]> frameReceived, int maxFrameBytes = 1100, HdlcRepairPolicy? repair = null)
    {
        ArgumentNullException.ThrowIfNull(frameReceived);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, MinFrameBytes + 2);
        _frameReceived = frameReceived;
        _maxFrameBytes = maxFrameBytes;
        _buffer = new byte[maxFrameBytes];
        _repair = repair;
        if (repair is not null)
        {
            _soft = new float[RepairMaxFrameBytes * 8];
            _raw = new byte[(RepairMaxFrameBytes * 8) + 16];
            _rawSoft = new float[(RepairMaxFrameBytes * 8) + 16];
            _reassemble = new byte[RepairMaxFrameBytes];
        }
    }

    /// <summary>Count of frames dropped for a bad FCS since construction (diagnostics).</summary>
    public long CrcFailures { get; private set; }

    /// <summary>Count of repairs delivered through <see cref="FrameRepaired"/> since
    /// construction (diagnostics).</summary>
    public long RepairedFrames { get; private set; }

    /// <summary>
    /// Raised, synchronously from <see cref="PushBit"/>, on the bit that completes a flag and so
    /// opens a frame - the instant a caller counting samples can mark as where the frame began.
    /// </summary>
    /// <remarks>
    /// Raised after the flag has closed whatever was in progress, so a caller that marks the
    /// start here and reads it when a frame is delivered gets that frame's own opening flag and
    /// not the closing one it shares with the next. Fires on every flag, including the run of
    /// them a station sends as its transmit delay, which is what makes the last one before the
    /// data the mark that is wanted. See <see cref="Modems.FrameSpan"/>.
    /// </remarks>
    public Action? FrameOpened { get; set; }

    /// <summary>
    /// Receives a frame the repair engine recovered: FCS stripped, gates passed, with the
    /// number of bit edits it took. Set together with a repair policy - repairs with nowhere
    /// to go are not attempted. A consumer treats these as monitor-grade: the bytes passed
    /// the same FCS an ordinary decode passes, but only after a search touched them (see
    /// <see cref="HdlcRepairPolicy"/> for the false-pass record).
    /// </summary>
    public Action<byte[], int>? FrameRepaired { get; set; }

    /// <summary>Pushes one logical bit (0/1) through the deframer.</summary>
    /// <param name="bit">The logical bit, post-NRZI.</param>
    /// <param name="soft">The demodulator's confidence in the bit: the magnitude of its
    /// decision variable at the sampling instant (bigger is surer). Only used by the repair
    /// engine's ordering; a hard-bit-only caller leaves every bit equally confident and the
    /// ordering degenerates to bit position, which is why repair wants a soft-aware feed.</param>
    public void PushBit(int bit, float soft = float.MaxValue)
    {
        bit &= 1;
        _flagShift = ((_flagShift << 1) | bit) & 0xFF;

        if (_flagShift == 0x7E)
        {
            // Flag: closes any frame in progress and opens the next. The six ones inside
            // the flag will have bumped _onesRun; that state dies with the reset below.
            EndFrame();
            _inFrame = true;
            _byteShift = 0;
            _bitCount = 0;
            _length = 0;
            _softCount = 0;
            _rawCount = 0;
            _onesRun = 0;
            FrameOpened?.Invoke();
            return;
        }

        if (bit == 1)
        {
            _onesRun++;
            if (_onesRun >= 7)
            {
                // Abort sequence: discard and go back to hunting for a flag.
                _inFrame = false;
                return;
            }
        }
        else
        {
            if (_onesRun == 5)
            {
                _onesRun = 0;
                if (_inFrame && _raw is not null && _rawCount < _raw.Length)
                {
                    _raw[_rawCount] = 0;
                    _rawSoft![_rawCount] = soft;
                    _rawCount++;
                }

                return; // stuffed zero: drop it
            }

            _onesRun = 0;
        }

        if (!_inFrame)
        {
            return;
        }

        if (_raw is not null && _rawCount < _raw.Length)
        {
            _raw[_rawCount] = (byte)bit;
            _rawSoft![_rawCount] = soft;
            _rawCount++;
        }

        // HDLC bytes go over the air least-significant bit first.
        _byteShift = (_byteShift >> 1) | (bit << 7);
        if (++_bitCount == 8)
        {
            _bitCount = 0;
            if (_length == _maxFrameBytes)
            {
                _inFrame = false; // oversize: noise or not for us
                return;
            }

            _buffer[_length++] = (byte)_byteShift;
            _byteShift = 0;
        }

        if (_soft is not null && _softCount < _soft.Length)
        {
            _soft[_softCount++] = soft;
        }
    }

    private void EndFrame()
    {
        // By the time the flag's final bit completes the 0x7E pattern, its first 7 bits
        // have already passed through the byte assembler - so a byte-aligned frame always
        // shows exactly 7 residual bits here. Anything else means a slip mid-frame.
        if (!_inFrame || _length < MinFrameBytes + 2)
        {
            return; // no frame or runt - silently discard
        }

        if (_bitCount != 7)
        {
            // A slip mid-frame: the byte grid no longer lines up with the closing flag.
            if (_repair is not null && FrameRepaired is not null && _length <= RepairMaxFrameBytes)
            {
                TrySlipRepair();
            }

            return;
        }

        var content = _buffer.AsSpan(0, _length - 2);
        ushort fcs = (ushort)(_buffer[_length - 2] | (_buffer[_length - 1] << 8));
        if (Crc16X25.Compute(content) != fcs)
        {
            CrcFailures++;
            if (_repair is not null && FrameRepaired is not null && _length <= RepairMaxFrameBytes)
            {
                TryCrcRepair();
            }

            return;
        }

        _frameReceived(content.ToArray());
    }

    /// <summary>
    /// FCS-failure repair: flip the bits the demodulator was least sure of - singles in
    /// weakest-first order, then every pair among the weakest - re-checking the FCS on each
    /// candidate and delivering the first that verifies and clears the gates.
    /// </summary>
    private void TryCrcRepair()
    {
        if (!RepairedFrameGates.LooksAddressed(_buffer.AsSpan(0, _length)))
        {
            return;
        }

        int bits = Math.Min(_length * 8, _softCount);
        if (bits < 16)
        {
            return; // no soft information worth ordering by
        }

        var policy = _repair!;

        // Selection of the weakest budget bits, ascending by soft. The budget is small
        // against the frame, so the O(budget·bits) selection beats a full sort.
        int budget = Math.Min(policy.CrcSingles + policy.CrcPairWidth, bits);
        Span<int> order = stackalloc int[bits];
        for (int i = 0; i < bits; i++)
        {
            order[i] = i;
        }

        for (int i = 0; i < budget; i++)
        {
            int best = i;
            for (int j = i + 1; j < bits; j++)
            {
                if (_soft![order[j]] < _soft[order[best]])
                {
                    best = j;
                }
            }

            (order[i], order[best]) = (order[best], order[i]);
        }

        Span<byte> candidate = stackalloc byte[_length];
        _buffer.AsSpan(0, _length).CopyTo(candidate);

        int singles = Math.Min(policy.CrcSingles, budget);
        for (int i = 0; i < singles; i++)
        {
            int bitIndex = order[i];
            candidate[bitIndex >> 3] ^= (byte)(1 << (bitIndex & 7));
            if (FcsVerifies(candidate))
            {
                DeliverRepaired(candidate, 1);
                return;
            }

            candidate[bitIndex >> 3] ^= (byte)(1 << (bitIndex & 7));
        }

        int pairWidth = Math.Min(policy.CrcPairWidth, budget);
        for (int i = 0; i < pairWidth; i++)
        {
            for (int j = i + 1; j < pairWidth; j++)
            {
                int b1 = order[i];
                int b2 = order[j];
                candidate[b1 >> 3] ^= (byte)(1 << (b1 & 7));
                candidate[b2 >> 3] ^= (byte)(1 << (b2 & 7));
                if (FcsVerifies(candidate))
                {
                    DeliverRepaired(candidate, 2);
                    return;
                }

                candidate[b1 >> 3] ^= (byte)(1 << (b1 & 7));
                candidate[b2 >> 3] ^= (byte)(1 << (b2 & 7));
            }
        }
    }

    /// <summary>
    /// Bit-slip repair: the stream between the flags lost one bit (six residual bits at the
    /// closing flag) or gained one (zero residual). Delete, or insert either value, at the
    /// weakest raw-stream positions, rebuild the frame from scratch - destuffing and byte
    /// assembly included - and FCS-check each candidate. Two or more slips are out of scope:
    /// their residue is indistinguishable from single-slip damage plus noise.
    /// </summary>
    private void TrySlipRepair()
    {
        bool insert = _bitCount == 6;   // lost a bit: put one back
        bool delete = _bitCount == 0;   // gained a bit: take one out
        if (!insert && !delete)
        {
            return;
        }

        if (_rawCount < (MinFrameBytes + 2) * 8)
        {
            return;
        }

        // weakest raw positions, ascending by soft
        int budget = Math.Min(_repair!.SlipPositions, _rawCount);
        int[] order = new int[_rawCount];
        for (int i = 0; i < _rawCount; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => _rawSoft![a].CompareTo(_rawSoft![b]));

        for (int oi = 0; oi < budget; oi++)
        {
            int pos = order[oi];
            if (delete)
            {
                if (Reassemble(skip: pos, insertAt: -1, insertBit: 0) is byte[] frame)
                {
                    DeliverRepaired(frame, 1);
                    return;
                }
            }
            else
            {
                foreach (int value in new[] { 0, 1 })
                {
                    if (Reassemble(skip: -1, insertAt: pos, insertBit: value) is byte[] frame)
                    {
                        DeliverRepaired(frame, 1);
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Rebuilds the frame from the raw stuffed-domain stream with one edit applied,
    /// re-running destuffing and byte assembly. Returns the FCS-verified, gate-passed frame
    /// content (FCS stripped), or null.</summary>
    private byte[]? Reassemble(int skip, int insertAt, int insertBit)
    {
        byte[] assembled = _reassemble!;
        int length = 0;
        int byteShift = 0;
        int bitCount = 0;
        int onesRun = 0;

        int total = _rawCount + (insertAt >= 0 ? 1 : 0) - (skip >= 0 ? 1 : 0);
        for (int n = 0; n < total; n++)
        {
            int bit;
            if (insertAt >= 0)
            {
                if (n == insertAt)
                {
                    bit = insertBit;
                }
                else
                {
                    int rawIndex = n < insertAt ? n : n - 1;
                    bit = _raw![rawIndex];
                }
            }
            else
            {
                int rawIndex = n < skip ? n : n + 1;
                bit = _raw![rawIndex];
            }

            if (bit == 1)
            {
                onesRun++;
                if (onesRun >= 7)
                {
                    return null; // abort inside the candidate: not a frame
                }
            }
            else
            {
                if (onesRun == 5)
                {
                    onesRun = 0;
                    continue; // stuffed zero
                }

                onesRun = 0;
            }

            byteShift = (byteShift >> 1) | (bit << 7);
            if (++bitCount == 8)
            {
                bitCount = 0;
                if (length == assembled.Length)
                {
                    return null;
                }

                assembled[length++] = (byte)byteShift;
                byteShift = 0;
            }
        }

        // the closing flag's first seven bits must again leave exactly 7 residual bits
        if (bitCount != 7 || length < MinFrameBytes + 2)
        {
            return null;
        }

        var content = assembled.AsSpan(0, length - 2);
        ushort fcs = (ushort)(assembled[length - 2] | (assembled[length - 1] << 8));
        if (Crc16X25.Compute(content) != fcs)
        {
            return null;
        }

        return content.ToArray();
    }

    private bool FcsVerifies(ReadOnlySpan<byte> candidate)
    {
        var content = candidate[..(_length - 2)];
        ushort fcs = (ushort)(candidate[_length - 2] | (candidate[_length - 1] << 8));
        return Crc16X25.Compute(content) == fcs;
    }

    /// <summary>Gates and delivers a repaired whole-frame candidate (FCS included).</summary>
    private void DeliverRepaired(Span<byte> candidate, int editedBits)
    {
        var content = candidate[..(_length - 2)];
        if (!RepairedFrameGates.Ax25Structure(content) || !RepairedFrameGates.PayloadConsistent(content))
        {
            return;
        }

        RepairedFrames++;
        FrameRepaired!.Invoke(content.ToArray(), editedBits);
    }

    /// <summary>Gates and delivers a repaired content array (FCS already stripped).</summary>
    private void DeliverRepaired(byte[] content, int editedBits)
    {
        if (!RepairedFrameGates.Ax25Structure(content) || !RepairedFrameGates.PayloadConsistent(content))
        {
            return;
        }

        RepairedFrames++;
        FrameRepaired!.Invoke(content, editedBits);
    }
}
