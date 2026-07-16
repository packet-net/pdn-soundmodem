using Packet.SoundModem.Fec;
using Packet.SoundModem.Fec.Ldpc;

namespace Packet.SoundModem.Ofdm;

/// <summary>
/// End-to-end FreeDV datac transmitter: a payload of bytes → 8&#160;kHz complex audio. It is the
/// exact inverse of <see cref="DatacReceiver"/> and a port of codec2's TX chain
/// (<c>freedv_rawdatacomptx</c> → <c>ofdm_ldpc_interleave_tx</c>, codec2 1.2.0 git 310777b,
/// LGPL-2.1 — see PROVENANCE.md): the caller's payload has a big-endian CRC-16 appended
/// (<c>freedv_gen_crc16</c>), the 16-/… byte frame is unpacked MSB-first to data bits
/// (<c>freedv_unpack</c>), LDPC-encoded to a codeword (<c>ldpc_encode_frame</c>), QPSK-mapped to
/// payload symbols (<c>qpsk_modulate_frame</c>), golden-prime interleaved
/// (<c>gp_interleave_comp</c>), then handed to <see cref="OfdmModulator.AssembleModemPacket"/>
/// (unique-word insertion, <c>ofdm_assemble_qpsk_modem_packet_symbols</c>) and
/// <see cref="OfdmModulator.ModulatePacket"/> (idft+CP + Hilbert clipper/BPF, <c>ofdm_txframe</c>).
/// </summary>
/// <remarks>
/// Two emission shapes: successive <see cref="ModulatePacket"/> calls form a continuous
/// <b>streaming</b> transmission (as codec2's <c>freedv_rawdatacomptx</c> loop does on one reused
/// struct — no preamble/postamble; the streaming RX acquires by pilot correlation), while
/// <see cref="ModulateBurst"/> emits the <b>burst</b> framing
/// <c>[preamble][packets…][postamble]</c> that the FreeDV CLI tools and FreeDATA exchange. The
/// modulator's persistent TX band-pass filter carries across calls in both shapes. All six datac
/// modes are supported (matching <see cref="DatacReceiver"/>); the narrow datac4/13/14 shorten
/// their LDPC codes inside <see cref="LdpcFrameCodec"/>. Not thread-safe.
/// </remarks>
public sealed class DatacTransmitter
{
    private readonly OfdmModulator _mod;
    private readonly LdpcFrameCodec _ldpc;
    private readonly int _frameBytes;
    private readonly int _payloadSyms;
    private readonly byte[] _frame;         // frameBytes: payload + 2-byte CRC
    private readonly byte[] _dataBits;      // ldpc.DataBits (frameBytes·8)
    private readonly byte[] _codeword;      // ldpc.CodedBits
    private readonly Cf[] _payload;         // payloadSyms QPSK symbols (codeword order)
    private readonly Cf[] _interleaved;     // payloadSyms QPSK symbols (grid order)

    /// <summary>Creates a transmitter for <paramref name="mode"/> (all six datac modes).</summary>
    public DatacTransmitter(OfdmMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        Mode = mode;
        _mod = new OfdmModulator(mode);
        _ldpc = DatacLdpc.Create(ToDatacMode(mode.Name));

        if (_ldpc.DataBits % 8 != 0)
        {
            throw new InvalidOperationException(
                $"data bits {_ldpc.DataBits} for {mode.Name} is not a whole number of bytes");
        }

        _frameBytes = _ldpc.DataBits / 8;
        _payloadSyms = _ldpc.CodedBits / mode.Bps;

        // The interleaved payload symbols must exactly fill the modem packet's non-UW slots — the
        // same invariant the receiver checks the other way round (DatacReceiver ctor).
        int packetPayloadSyms = mode.SymsPerPacket - (mode.Nuwbits / mode.Bps);
        if (_payloadSyms != packetPayloadSyms)
        {
            throw new InvalidOperationException(
                $"LDPC payload symbol count {_payloadSyms} != modem-packet payload slots {packetPayloadSyms} for {mode.Name}");
        }

        _frame = new byte[_frameBytes];
        _dataBits = new byte[_ldpc.DataBits];
        _codeword = new byte[_ldpc.CodedBits];
        _payload = new Cf[_payloadSyms];
        _interleaved = new Cf[_payloadSyms];
    }

    /// <summary>The mode this transmitter serves.</summary>
    public OfdmMode Mode { get; }

    /// <summary>Total frame bytes per packet (payload + 2-byte CRC) — matches
    /// <see cref="DatacReceiver.FrameBytes"/>.</summary>
    public int FrameBytes => _frameBytes;

    /// <summary>Payload bytes per packet (frame bytes minus the 2-byte CRC).</summary>
    public int PayloadBytes => _frameBytes - 2;

    /// <summary>Complex audio samples produced per packet (<c>Np·Ns·(M+Ncp)</c>).</summary>
    public int SamplesPerPacket => Mode.SamplesPerPacket;

    /// <summary>Modulates one packet of <see cref="PayloadBytes"/> payload bytes to
    /// <see cref="SamplesPerPacket"/> complex audio samples (codec2 amplitude scale, peak
    /// ≈&#160;16384). The persistent TX band-pass filter carries across successive calls, so
    /// repeated calls on one instance build a continuous streaming transmission.</summary>
    public Cf[] ModulatePacket(ReadOnlySpan<byte> payload)
    {
        // idft+CP + Hilbert clipper/BPF (ofdm_txframe), persistent BPF for streaming.
        return _mod.ModulatePacket(AssembleModemPacket(payload));
    }

    /// <summary>Encodes one payload up to the assembled modem-packet symbols (steps 1-6 of the TX
    /// chain — everything before the waveform): CRC append, unpack, LDPC encode, QPSK map,
    /// golden-prime interleave, unique-word insertion.</summary>
    private Cf[] AssembleModemPacket(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadBytes)
        {
            throw new ArgumentException(
                $"expected {PayloadBytes} payload bytes, got {payload.Length}", nameof(payload));
        }

        // 1. frame = payload || big-endian CRC-16 (freedv_gen_crc16, appended by the harness).
        payload.CopyTo(_frame);
        ushort crc = FreedvCrc16.Compute(payload);
        _frame[_frameBytes - 2] = (byte)(crc >> 8);
        _frame[_frameBytes - 1] = (byte)(crc & 0xff);

        // 2. unpack frame bytes MSB-first to data bits (freedv_unpack).
        for (int i = 0; i < _dataBits.Length; i++)
        {
            _dataBits[i] = (byte)((_frame[i / 8] >> (7 - (i % 8))) & 1);
        }

        // 3. LDPC encode: codeword = [data bits | parity bits] (ldpc_encode_frame).
        _ldpc.Encode(_dataBits, _codeword);

        // 4. QPSK-map the coded bits to payload symbols (qpsk_modulate_frame): for symbol i the
        //    dibit is (codeword[2i], codeword[2i+1]) → constellation index (b0<<1)|b1, matching
        //    OfdmSoftDemap's llr[2i]=MSB, llr[2i+1]=LSB convention on the RX.
        for (int i = 0; i < _payloadSyms; i++)
        {
            int idx = ((_codeword[2 * i] & 1) << 1) | (_codeword[(2 * i) + 1] & 1);
            _payload[i] = OfdmTxTables.Qpsk[idx];
        }

        // 5. golden-prime interleave the symbols (gp_interleave_comp); the RX deinterleaves both
        //    symbols and amplitudes with the inverse permutation.
        GpInterleaver.Interleave<Cf>(_payload, _interleaved, _payloadSyms);

        // 6. insert the unique-word symbols (ofdm_assemble_qpsk_modem_packet_symbols).
        return _mod.AssembleModemPacket(_interleaved);
    }

    /// <summary>Modulates one <b>burst</b>: <c>[preamble][packet]×N[postamble]</c>, the framing
    /// codec2's <c>freedv_data_raw_tx</c> emits (one <c>freedv_rawdatapreambletx</c>, the data
    /// packets, one <c>freedv_rawdatapostambletx</c>) and that <see cref="DatacReceiver"/> in
    /// burst mode acquires. Each payload becomes one packet, so send
    /// <c>payloads.Count&#160;==&#160;packetsPerBurst</c> of the receiving side. The persistent TX
    /// band-pass filter runs across the whole burst and carries into the next call, matching one
    /// long-lived codec2 process sending successive bursts (silence between bursts bypasses the
    /// modem there, so the filter state persists). No silence is appended — codec2's tool follows
    /// each burst with ≈ two packets of it, which the burst/IModem layer owns.</summary>
    public Cf[] ModulateBurst(IReadOnlyList<byte[]> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count == 0)
        {
            throw new ArgumentException("a burst needs at least one payload", nameof(payloads));
        }

        var packets = new List<Cf[]>(payloads.Count);
        foreach (byte[] payload in payloads)
        {
            packets.Add(AssembleModemPacket(payload));
        }

        return _mod.EmitBurst(packets, resetFilter: false);
    }

    /// <summary>Modulates one packet and converts it to real signed-16-bit PCM at 8&#160;kHz — the
    /// format <see cref="DatacReceiver.Process"/> consumes and the checked-in <c>.s16</c> vectors
    /// use (real part, round-to-nearest, clamped). Convenience wrapper over
    /// <see cref="ModulatePacket"/>.</summary>
    public short[] ModulatePacketPcm16(ReadOnlySpan<byte> payload) => ToPcm16(ModulatePacket(payload));

    /// <summary>Clears the persistent TX band-pass filter — call to start an independent stream so
    /// the previous stream's filter tail does not carry over.</summary>
    public void ResetFilter() => _mod.ResetFilter();

    /// <summary>Converts complex modem audio to real signed-16-bit PCM: real part, rounded to the
    /// nearest integer (away-from-zero on ties) and clamped to the <see cref="short"/> range —
    /// codec2's <c>lround</c>/clamp on the <c>±16384</c> amplitude scale.</summary>
    public static short[] ToPcm16(ReadOnlySpan<Cf> audio)
    {
        var outp = new short[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            double v = Math.Round(audio[i].Re, MidpointRounding.AwayFromZero);
            outp[i] = v >= 32767.0 ? (short)32767 : v <= -32768.0 ? (short)-32768 : (short)v;
        }

        return outp;
    }

    private static DatacMode ToDatacMode(string name) => name switch
    {
        "datac0" => DatacMode.Datac0,
        "datac1" => DatacMode.Datac1,
        "datac3" => DatacMode.Datac3,
        "datac4" => DatacMode.Datac4,
        "datac13" => DatacMode.Datac13,
        "datac14" => DatacMode.Datac14,
        _ => throw new ArgumentException($"unknown datac mode '{name}'", nameof(name)),
    };
}
