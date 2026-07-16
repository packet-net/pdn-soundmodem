using Packet.SoundModem.Fec;

namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Assembles decoded modem frames into a packet's worth of QPSK symbols and produces the payload
/// LLRs — a port of the packet-buffer logic in codec2's reference RX driver
/// (<c>ofdm_demod.c:457-506</c>) plus <c>ofdm_extract_uw</c> / <c>ofdm_disassemble_qpsk_modem_packet</c>
/// (<c>ofdm.c:2455-2565</c>) and the golden-prime de-interleave. codec2 1.2.0 (git 310777b),
/// LGPL-2.1 — see PROVENANCE.md. Not thread-safe.
/// </summary>
public sealed class OfdmPacketAssembler
{
    private readonly OfdmDemodConfig _c;
    private readonly Cf[] _rxSyms;      // NsymsPerPacket
    private readonly float[] _rxAmps;   // NsymsPerPacket
    private readonly Cf[] _payloadSyms; // NpayloadSymsPerPacket
    private readonly float[] _payloadAmps;
    private readonly Cf[] _payloadSymsDe;
    private readonly float[] _payloadAmpsDe;
    private readonly int _nuwsyms;

    /// <summary>Creates an assembler for <paramref name="config"/>.</summary>
    public OfdmPacketAssembler(OfdmDemodConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _c = config;
        _rxSyms = new Cf[config.NsymsPerPacket];
        _rxAmps = new float[config.NsymsPerPacket];
        _nuwsyms = config.Nuwbits / 2;
        int payload = config.NsymsPerPacket - _nuwsyms;   // Ntxtsyms == 0 for datac
        _payloadSyms = new Cf[payload];
        _payloadAmps = new float[payload];
        _payloadSymsDe = new Cf[payload];
        _payloadAmpsDe = new float[payload];
        PayloadSymsPerPacket = payload;
    }

    /// <summary>QPSK payload symbols per packet (packet symbols minus UW symbols).</summary>
    public int PayloadSymsPerPacket { get; }

    /// <summary>Payload LLRs per packet = <c>2·PayloadSymsPerPacket</c> — the count the LDPC frame
    /// codec consumes.</summary>
    public int PayloadBitsPerPacket => 2 * PayloadSymsPerPacket;

    /// <summary>Slides the rolling packet buffer left by one frame and appends the newly decoded
    /// frame's symbols/amps at the tail (codec2 <c>ofdm_demod.c:458-465</c>).</summary>
    public void PushFrame(ReadOnlySpan<Cf> rxNp, ReadOnlySpan<float> rxAmp)
    {
        int spp = _c.NsymsPerPacket;
        int spf = _c.NsymsPerFrame;
        Array.Copy(_rxSyms, spf, _rxSyms, 0, spp - spf);
        Array.Copy(_rxAmps, spf, _rxAmps, 0, spp - spf);
        rxNp[..spf].CopyTo(_rxSyms.AsSpan(spp - spf));
        rxAmp[..spf].CopyTo(_rxAmps.AsSpan(spp - spf));
    }

    /// <summary>Extracts the unique-word bits from the frames currently in the packet buffer
    /// (codec2 <c>ofdm_extract_uw</c>). Writes <c>Nuwbits</c> bits.</summary>
    public void ExtractUw(Span<byte> rxUw)
    {
        int spf = _c.NsymsPerFrame;
        int stUw = _c.NsymsPerPacket - (_c.Nuwframes * spf);
        int u = 0;
        for (int s = 0; s < spf * _c.Nuwframes; s++)
        {
            if (u < _nuwsyms && s == _c.UwIndSym[u])
            {
                OfdmDemodulator.QpskDemod(_rxSyms[stUw + s], out int bit1, out int bit0);
                rxUw[2 * u] = (byte)bit1;
                rxUw[(2 * u) + 1] = (byte)bit0;
                u++;
            }
        }
    }

    /// <summary>Disassembles the full packet buffer into payload symbols/amps (dropping the
    /// UW-carrying symbols by <c>uw_ind_sym</c>), de-interleaves them (golden-prime), and produces
    /// the payload LLRs (codec2 <c>ofdm_disassemble_qpsk_modem_packet</c> +
    /// <c>gp_deinterleave</c> + <c>symbols_to_llrs</c>). <paramref name="llr"/> must hold
    /// <see cref="PayloadBitsPerPacket"/> values.</summary>
    public void ToLlrs(Span<float> llr, float meanAmp, float esNo = 3.0f)
    {
        // disassemble: drop UW symbols (ofdm.c:2473-2481)
        int p = 0;
        int u = 0;
        for (int s = 0; s < _c.NsymsPerPacket; s++)
        {
            if (u < _nuwsyms && s == _c.UwIndSym[u])
            {
                u++;
            }
            else
            {
                _payloadSyms[p] = _rxSyms[s];
                _payloadAmps[p] = _rxAmps[s];
                p++;
            }
        }

        // golden-prime de-interleave of both symbols and amplitudes
        GpInterleaver.Deinterleave<Cf>(_payloadSyms, _payloadSymsDe, PayloadSymsPerPacket);
        GpInterleaver.Deinterleave<float>(_payloadAmps, _payloadAmpsDe, PayloadSymsPerPacket);

        OfdmSoftDemap.SymbolsToLlrs(llr, _payloadSymsDe, _payloadAmpsDe, esNo, meanAmp, PayloadSymsPerPacket);
    }
}
