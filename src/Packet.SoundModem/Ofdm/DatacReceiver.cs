using Packet.SoundModem.Fec;
using Packet.SoundModem.Fec.Ldpc;

namespace Packet.SoundModem.Ofdm;

/// <summary>One decoded FreeDV datac packet.</summary>
/// <param name="Bytes">All recovered frame bytes (payload followed by the 2-byte CRC).</param>
/// <param name="CrcOk">Whether the trailing CRC-16 matches the payload.</param>
/// <param name="Iterations">LDPC decoder iterations used.</param>
/// <param name="ParityChecks">LDPC parity checks satisfied at termination.</param>
public readonly record struct DatacRxResult(byte[] Bytes, bool CrcOk, int Iterations, int ParityChecks)
{
    /// <summary>The payload bytes (everything but the trailing 2-byte CRC).</summary>
    public ReadOnlySpan<byte> Payload => Bytes.AsSpan(0, Bytes.Length - 2);
}

/// <summary>
/// End-to-end FreeDV datac receiver: 8&#160;kHz real audio → decoded packets. It drives
/// <see cref="OfdmDemodulator"/> + <see cref="OfdmPacketAssembler"/> exactly as codec2's reference
/// RX loop does (<c>ofdm_demod.c</c>: sync-search while searching, demod while trial/synced,
/// accumulate a packet, extract the UW, and at the last frame of a packet de-interleave →
/// <c>symbols_to_llrs</c> → LDPC decode → CRC check), then advances the sync state machine.
/// Streaming by default; pass <c>packetsPerBurst&#160;≥&#160;1</c> for <b>burst</b> mode — the
/// preamble/postamble acquisition that codec2's <c>freedv_data_raw_tx/rx</c> tools and FreeDATA
/// use (<c>freedv_set_frames_per_burst</c>). codec2 1.2.0 (git 310777b), LGPL-2.1 — see
/// PROVENANCE.md.
/// </summary>
/// <remarks>
/// This is the demodulator's validation harness and the substrate for a future <c>IModem</c>
/// (Phase 3). Supported/validated modes are datac0/1/3 (no RX band-pass filter). Not thread-safe.
/// </remarks>
public sealed class DatacReceiver
{
    private readonly OfdmDemodConfig _cfg;
    private readonly OfdmDemodulator _demod;
    private readonly OfdmPacketAssembler _assembler;
    private readonly LdpcFrameCodec _ldpc;
    private readonly float[] _llr;
    private readonly byte[] _dataBits;
    private readonly byte[] _rxUw;
    private readonly int _frameBytes;
    private readonly int _maxNin;

    /// <summary>Creates a receiver for <paramref name="mode"/> (datac0/1/3 supported).</summary>
    /// <param name="mode">The datac mode to receive.</param>
    /// <param name="packetsPerBurst">0 (default) selects streaming mode. ≥&#160;1 selects burst
    /// mode with that many packets expected per burst, mirroring codec2's
    /// <c>freedv_set_frames_per_burst</c> (the standard CLI tools and FreeDATA use 1).</param>
    public DatacReceiver(OfdmMode mode, int packetsPerBurst = 0)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentOutOfRangeException.ThrowIfNegative(packetsPerBurst);
        _cfg = new OfdmDemodConfig(mode);
        _demod = new OfdmDemodulator(_cfg);
        _assembler = new OfdmPacketAssembler(_cfg);
        _ldpc = DatacLdpc.Create(ToDatacMode(mode.Name));

        if (_ldpc.CodedBits != _assembler.PayloadBitsPerPacket)
        {
            throw new InvalidOperationException(
                $"LDPC coded-bit count {_ldpc.CodedBits} != payload LLR count {_assembler.PayloadBitsPerPacket} for {mode.Name}");
        }

        _llr = new float[_assembler.PayloadBitsPerPacket];
        _dataBits = new byte[_ldpc.DataBits];
        _rxUw = new byte[_cfg.Nuwbits];
        _frameBytes = _ldpc.DataBits / 8;

        if (packetsPerBurst > 0)
        {
            // The known sequences burst acquisition correlates against are the TX side's raw
            // preamble/postamble frames (ofdm_create generates ofdm->tx_preamble/tx_postamble via
            // the modulator, ofdm.c:531-538).
            var modulator = new OfdmModulator(mode);
            _demod.SetPacketsPerBurst(packetsPerBurst, modulator.PreambleRaw.Span, modulator.PostambleRaw.Span);
        }

        // Burst preamble detection can skip nin ahead by up to two frames (codec2
        // max_samplesperframe, ofdm.c:304-310); streaming moves at most ±SamplesPerSymbol/4.
        _maxNin = packetsPerBurst > 0
            ? (2 * _cfg.SamplesPerFrame) + 2
            : _cfg.SamplesPerFrame + (_cfg.SamplesPerSymbol / 4) + 1;
    }

    /// <summary>The demodulator (for inspecting sync state / intermediate values in tests).</summary>
    public OfdmDemodulator Demod => _demod;

    /// <summary>Total frame bytes per packet (payload + 2-byte CRC).</summary>
    public int FrameBytes => _frameBytes;

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

    /// <summary>Feeds a block of raw 8&#160;kHz signed 16-bit samples and returns every packet
    /// decoded from it (matching codec2's <c>short/32767</c> scaling).</summary>
    public IReadOnlyList<DatacRxResult> Process(ReadOnlySpan<short> samples)
    {
        var results = new List<DatacRxResult>();
        var frame = new Cf[_maxNin];

        int pos = 0;
        int nin = _demod.Nin;
        while (pos + nin <= samples.Length)
        {
            for (int i = 0; i < nin; i++)
            {
                frame[i] = new Cf(samples[pos + i] / 32767.0f, 0.0f);
            }

            ReadOnlySpan<Cf> input = frame.AsSpan(0, nin);

            if (_demod.State == SyncState.Search)
            {
                _demod.SyncSearch(input);
            }
            else
            {
                _demod.Demod(input);
                _assembler.PushFrame(_demod.RxNp, _demod.RxAmp);
                _assembler.ExtractUw(_rxUw);

                if (_demod.ModemFrame == _cfg.Np - 1)
                {
                    results.Add(DecodePacket());
                }
            }

            pos += nin;
            _demod.SyncStateMachine(_rxUw);
            nin = _demod.Nin;
        }

        return results;
    }

    private DatacRxResult DecodePacket()
    {
        _assembler.ToLlrs(_llr, _demod.MeanAmp);
        int iter = _ldpc.Decode(_llr, _dataBits, out int parityChecks);

        var bytes = new byte[_frameBytes];
        for (int i = 0; i < _ldpc.DataBits; i++)
        {
            if (_dataBits[i] != 0)
            {
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }

        ushort crc = FreedvCrc16.Compute(bytes.AsSpan(0, _frameBytes - 2));
        bool crcOk = bytes[_frameBytes - 2] == (byte)(crc >> 8) && bytes[_frameBytes - 1] == (byte)(crc & 0xff);
        return new DatacRxResult(bytes, crcOk, iter, parityChecks);
    }
}
