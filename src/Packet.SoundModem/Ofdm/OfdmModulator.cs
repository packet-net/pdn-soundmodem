namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Interop-exact codec2 <c>datac</c> OFDM modulator. Ports the TX half of codec2 1.2.0
/// (git 310777b, LGPL-2.1, © David Rowe): modem-packet symbol assembly
/// (<c>ofdm_assemble_qpsk_modem_packet_symbols</c>, <c>ofdm.c:2412</c>), the direct inverse-DFT
/// + cyclic-prefix frame builder (<c>ofdm_txframe</c>/<c>idft</c>, <c>ofdm.c:1004,642</c>), the
/// Hilbert-clipper / band-pass / scaling chain (<c>ofdm_hilbert_clipper</c>, <c>ofdm.c:1072</c>),
/// the deterministic preamble/postamble (<c>ofdm_generate_preamble</c>, <c>ofdm.c:2592</c>), and
/// burst assembly (<c>freedv_data_raw_tx</c>). It owns already-encoded, already-interleaved
/// payload symbols and produces complex audio at <c>Fs = 8000 Hz</c> on codec2's amplitude scale
/// (peak ≈ <c>OFDM_PEAK = 16384</c>); LDPC, the golden interleaver, CRC-16 and KISS/IL2P framing
/// are sibling components. See PROVENANCE.md and docs/ofdm-design.md §3.
/// </summary>
/// <remarks>
/// The inverse transform is codec2's hand-rolled <c>idft</c>, NOT an FFT: datac14 has
/// <c>M = 144</c> (not a power of two) so a radix-2 IFFT cannot do it, and even at <c>M = 128</c>
/// the per-row <c>c *= delta</c> phasor recurrence produces a different float rounding sequence
/// than a butterfly cascade. Interop-exactness is therefore in algorithm, constants and ordering,
/// validated against generated codec2 audio within a tolerance (§1.3) — the C <c>cosf/sinf/cabsf</c>
/// boundary makes literal IEEE-754 equality unattainable.
/// </remarks>
public sealed class OfdmModulator
{
    private const float OfdmPeak = 16384f; // codec2_ofdm.h:45

    private readonly float[] _bpfProto;
    private readonly float _bpfFreq;
    private readonly ComplexBandpassFir _txBpf; // persistent — carries across a burst (§3.9)

    private readonly Cf[] _pilots;      // length Nc+2, edge pilots zeroed
    private readonly Cf[] _txUwSyms;    // length Nuwbits/2
    private readonly int[] _uwIndSym;   // length Nuwbits/2
    private readonly float _docF;       // codec2 ofdm->doc (float)
    private readonly float _invM;       // codec2 ofdm->inv_m (float)

    /// <summary>Creates a modulator for one datac mode, precomputing the pilot row, the unique-word
    /// symbols and their placement indices, the raw preamble/postamble frames, and the tuned
    /// persistent TX band-pass filter.</summary>
    public OfdmModulator(OfdmMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        Mode = mode;

        _docF = (float)mode.Doc;
        _invM = 1f / mode.M;

        // Pilot row: pilots[i] = pilotvalues[i], then edge pilots (cols 0 and Nc+1) zeroed.
        _pilots = new Cf[mode.Nc + 2];
        for (int i = 0; i < _pilots.Length; i++)
        {
            _pilots[i] = new Cf(OfdmTxTables.PilotValues[i], 0f);
        }

        _pilots[0] = Cf.Zero;
        _pilots[mode.Nc + 1] = Cf.Zero;

        byte[] txUw = OfdmTxTables.ResolveTxUw(mode.Name);
        _txUwSyms = OfdmTxTables.TxUwSymbols(txUw);
        _uwIndSym = OfdmTxTables.UwIndexSymbols(mode.Nuwbits, mode.Nc, mode.Ns, mode.Np);

        // TX band-pass: real prototype heterodyned to the carrier centre. datac4/13/14 centre on
        // the mean carrier freq (find_carrier_centre); the rest on the nominal 1500 Hz.
        _bpfProto = mode.Name switch
        {
            "datac0" or "datac3" => OfdmTxBpfPrototypes.filtP400S600,
            "datac1" => OfdmTxBpfPrototypes.filtP900S1100,
            "datac4" or "datac13" or "datac14" => OfdmTxBpfPrototypes.filtP200S400,
            _ => throw new ArgumentException($"unknown datac mode '{mode.Name}'", nameof(mode)),
        };

        float centreHz = mode.Name is "datac4" or "datac13" or "datac14"
            ? FindCarrierCentre()
            : (float)mode.TxCentre;
        _bpfFreq = (float)(centreHz / mode.Fs);
        BpfCentreHz = centreHz;
        _txBpf = NewBpf();

        // Deterministic preamble/postamble: raw idft+CP of LCG-random QPSK, one frame, no clip/bpf.
        PreambleRaw = RawModulate(QpskMapBits(OfdmTxTables.LcgBits(mode.BitsPerFrame, 2)), np: 1);
        PostambleRaw = RawModulate(QpskMapBits(OfdmTxTables.LcgBits(mode.BitsPerFrame, 3)), np: 1);
    }

    /// <summary>The mode this modulator serves.</summary>
    public OfdmMode Mode { get; }

    /// <summary>The TX band-pass filter's centre frequency (Hz) — 1500 for datac0/1/3, the mean
    /// carrier frequency for the narrow datac4/13/14.</summary>
    public float BpfCentreHz { get; }

    /// <summary>Raw preamble frame (amp_scale=1, no clip, no BPF), length
    /// <c>SamplesPerFrame</c> — the LCG seed-2 pattern before the clipper.</summary>
    public ReadOnlyMemory<Cf> PreambleRaw { get; }

    /// <summary>Raw postamble frame (LCG seed 3), length <c>SamplesPerFrame</c>.</summary>
    public ReadOnlyMemory<Cf> PostambleRaw { get; }

    /// <summary>Unique-word symbol-placement indices (<c>uw_ind_sym[]</c>) — exposed for tests.</summary>
    internal IReadOnlyList<int> UwIndexSymbols => _uwIndSym;

    /// <summary>Unique-word symbols (<c>tx_uw_syms[]</c>) — exposed for tests.</summary>
    internal IReadOnlyList<Cf> TxUwSymbols => _txUwSyms;

    /// <summary>Assembles a full modem packet of symbols from caller-supplied payload symbols,
    /// scattering the unique-word symbols at their fixed indices
    /// (<c>ofdm_assemble_qpsk_modem_packet_symbols</c>). Payload length must be
    /// <c>SymsPerPacket − Nuwbits/2</c>; the result is <c>SymsPerPacket</c> symbols.</summary>
    public Cf[] AssembleModemPacket(ReadOnlySpan<Cf> payloadSyms)
    {
        int nSyms = Mode.SymsPerPacket;
        int nUwSyms = _txUwSyms.Length;
        if (payloadSyms.Length != nSyms - nUwSyms)
        {
            throw new ArgumentException(
                $"expected {nSyms - nUwSyms} payload symbols, got {payloadSyms.Length}",
                nameof(payloadSyms));
        }

        var packet = new Cf[nSyms];
        int p = 0, u = 0;
        for (int s = 0; s < nSyms; s++)
        {
            if (u < nUwSyms && s == _uwIndSym[u])
            {
                packet[s] = _txUwSyms[u++];
            }
            else
            {
                packet[s] = payloadSyms[p++];
            }
        }

        return packet;
    }

    /// <summary>Modulates one assembled modem packet (<c>SymsPerPacket</c> symbols) to audio,
    /// running the Hilbert clipper through the <b>persistent</b> band-pass filter — use this
    /// inside a burst. Returns <c>SamplesPerPacket</c> complex samples.</summary>
    public Cf[] ModulatePacket(ReadOnlySpan<Cf> modemPacketSyms)
    {
        Cf[] tx = RawModulate(modemPacketSyms, Mode.Np);
        HilbertClipper(tx, _txBpf);
        return tx;
    }

    /// <summary>Convenience test path (<c>ofdm_mod</c>): maps <c>BitsPerPacket</c> raw bits to
    /// QPSK, assembles the frame grid and runs the full clipper chain through a <b>fresh</b>
    /// band-pass filter — matching a single <c>ofdm_mod</c> call on a newly created codec2
    /// struct. Returns <c>SamplesPerPacket</c> complex samples.</summary>
    public Cf[] ModulatePacketBits(ReadOnlySpan<byte> packetBits)
    {
        RequirePacketBits(packetBits);
        Cf[] tx = RawModulate(QpskMapBits(packetBits), Mode.Np);
        HilbertClipper(tx, NewBpf());
        return tx;
    }

    /// <summary>Same as <see cref="ModulatePacketBits"/> but with the band-pass stage disabled —
    /// the amp-scale → clip_gain1 → clip → final-clip path only. Matches <c>ofdm_mod</c> on a
    /// struct with <c>tx_bpf_en=false</c>; the exactly-reproducible pre-BPF stage-2 checkpoint.</summary>
    internal Cf[] ModulatePacketBitsNoBpf(ReadOnlySpan<byte> packetBits)
    {
        RequirePacketBits(packetBits);
        Cf[] tx = RawModulate(QpskMapBits(packetBits), Mode.Np);
        HilbertClipper(tx, bpf: null);
        return tx;
    }

    /// <summary>Raw frame builder (idft + CP, no clipper) for the assembled packet — the
    /// pre-clip checkpoint. Length <c>SamplesPerPacket</c>.</summary>
    internal Cf[] RawTxFrame(ReadOnlySpan<Cf> modemPacketSyms) => RawModulate(modemPacketSyms, Mode.Np);

    /// <summary>Assembles a full burst: <c>[preamble][data packet]×N[postamble]</c>, every segment
    /// pushed through the one shared band-pass filter in order (<c>freedv_data_raw_tx</c>). With
    /// <paramref name="resetFilter"/> true the filter starts from zeros (a fresh single-burst
    /// codec2 process); false continues a running filter (burst N&gt;1 of a continuous run). No
    /// trailing silence is added — that belongs to the burst/IModem layer.</summary>
    public Cf[] EmitBurst(IReadOnlyList<Cf[]> assembledPackets, bool resetFilter = true)
    {
        ArgumentNullException.ThrowIfNull(assembledPackets);
        if (resetFilter)
        {
            _txBpf.Reset();
        }

        int frameLen = Mode.SamplesPerFrame;
        int total = frameLen + (assembledPackets.Count * Mode.SamplesPerPacket) + frameLen;
        var outp = new Cf[total];
        int pos = 0;

        Cf[] pre = PreambleRaw.ToArray();
        HilbertClipper(pre, _txBpf);
        pre.CopyTo(outp, pos);
        pos += frameLen;

        foreach (Cf[] packet in assembledPackets)
        {
            if (packet.Length != Mode.SymsPerPacket)
            {
                throw new ArgumentException(
                    $"each packet needs {Mode.SymsPerPacket} symbols, got {packet.Length}",
                    nameof(assembledPackets));
            }

            Cf[] tx = RawModulate(packet, Mode.Np);
            HilbertClipper(tx, _txBpf);
            tx.CopyTo(outp, pos);
            pos += Mode.SamplesPerPacket;
        }

        Cf[] post = PostambleRaw.ToArray();
        HilbertClipper(post, _txBpf);
        post.CopyTo(outp, pos);

        return outp;
    }

    /// <summary>Clears the persistent band-pass filter — call before an independent burst so its
    /// state does not carry over.</summary>
    public void ResetFilter() => _txBpf.Reset();

    private void RequirePacketBits(ReadOnlySpan<byte> packetBits)
    {
        if (packetBits.Length != Mode.BitsPerPacket)
        {
            throw new ArgumentException(
                $"expected {Mode.BitsPerPacket} packet bits, got {packetBits.Length}",
                nameof(packetBits));
        }
    }

    private ComplexBandpassFir NewBpf() => new(_bpfProto, _bpfFreq);

    private Cf[] QpskMapBits(ReadOnlySpan<byte> bits)
    {
        var syms = new Cf[bits.Length / 2];
        for (int i = 0; i < syms.Length; i++)
        {
            // ofdm_mod bps=2: index (bits[2i]<<1)|bits[2i+1] (ofdm.c:1194-1197).
            syms[i] = OfdmTxTables.Qpsk[((bits[2 * i] & 1) << 1) | (bits[(2 * i) + 1] & 1)];
        }

        return syms;
    }

    /// <summary>Places symbols on the pilot/data grid and up-converts each OFDM symbol via the
    /// direct <see cref="Idft"/>, prepending the cyclic prefix (<c>ofdm_txframe:1023-1064</c>).
    /// No scaling/clipping/BPF — that is the caller's <see cref="HilbertClipper"/>.</summary>
    private Cf[] RawModulate(ReadOnlySpan<Cf> modemSyms, int np)
    {
        int nc2 = Mode.Nc + 2;
        int m = Mode.M;
        int ncp = Mode.Ncp;
        int sps = Mode.SamplesPerSymbol;
        int rows = np * Mode.Ns;

        if (modemSyms.Length != np * (Mode.Ns - 1) * Mode.Nc)
        {
            throw new ArgumentException(
                $"expected {np * (Mode.Ns - 1) * Mode.Nc} symbols, got {modemSyms.Length}",
                nameof(modemSyms));
        }

        var outp = new Cf[rows * sps];
        var aframe = new Cf[nc2];
        var sym = new Cf[m];
        int s = 0;

        for (int r = 0; r < rows; r++)
        {
            if (r % Mode.Ns == 0)
            {
                // pilot row: whole row of pilots (edge cols already zero in _pilots)
                _pilots.CopyTo(aframe, 0);
            }
            else
            {
                // data row: [0 | Nc data symbols | 0]
                aframe[0] = Cf.Zero;
                aframe[nc2 - 1] = Cf.Zero;
                for (int j = 1; j <= Mode.Nc; j++)
                {
                    aframe[j] = modemSyms[s++];
                }
            }

            Idft(aframe, sym);

            int baseIdx = r * sps;
            // cyclic prefix: last Ncp samples copied to the front
            for (int k = 0; k < ncp; k++)
            {
                outp[baseIdx + k] = sym[m - ncp + k];
            }

            for (int k = 0; k < m; k++)
            {
                outp[baseIdx + ncp + k] = sym[k];
            }
        }

        return outp;
    }

    /// <summary>Direct inverse DFT over the occupied bins (<c>idft</c>, <c>ofdm.c:642-667</c>):
    /// column <c>col</c> sits at bin <c>tx_nlower+col</c> via the per-row phasor recurrence
    /// <c>c *= delta</c>. The accumulation order is preserved for interop.</summary>
    private void Idft(ReadOnlySpan<Cf> vector, Span<Cf> result)
    {
        int m = Mode.M;
        int nc2 = Mode.Nc + 2;
        int nlower = Mode.TxNlower;
        float doc = _docF;
        float invM = _invM;

        // row 0: cexp(j0) == 1 for every bin, so it is the scaled bin sum.
        Cf acc0 = Cf.Zero;
        for (int col = 0; col < nc2; col++)
        {
            acc0 += vector[col];
        }

        result[0] = acc0 * invM;

        for (int row = 1; row < m; row++)
        {
            float angleC = nlower * doc * row;
            float angleDelta = doc * row;
            var c = new Cf(MathF.Cos(angleC), MathF.Sin(angleC));
            var delta = new Cf(MathF.Cos(angleDelta), MathF.Sin(angleDelta));

            Cf acc = Cf.Zero;
            for (int col = 0; col < nc2; col++)
            {
                acc += vector[col] * c;
                c *= delta;
            }

            result[row] = acc * invM;
        }
    }

    /// <summary>The Hilbert-clipper / scaling / band-pass chain (<c>ofdm_hilbert_clipper</c>,
    /// <c>ofdm.c:1072-1100</c>), in place: amp_scale → (clip_gain1 → clip) → BPF →
    /// (clip_gain2) → final clip. <paramref name="bpf"/> null skips the band-pass stage (and its
    /// paired clip_gain2), matching <c>tx_bpf_en=false</c>. All datac modes have clip enabled.</summary>
    private void HilbertClipper(Span<Cf> tx, ComplexBandpassFir? bpf)
    {
        float ampScale = (float)Mode.AmpScale;
        for (int i = 0; i < tx.Length; i++)
        {
            tx[i] *= ampScale;
        }

        // clip_en is true for every datac mode
        float clipGain1 = (float)Mode.ClipGain1;
        for (int i = 0; i < tx.Length; i++)
        {
            tx[i] *= clipGain1;
        }

        Clip(tx, OfdmPeak);

        if (bpf is not null)
        {
            bpf.Filter(tx, tx);

            float clipGain2 = (float)Mode.ClipGain2;
            for (int i = 0; i < tx.Length; i++)
            {
                tx[i] *= clipGain2;
            }
        }

        Clip(tx, OfdmPeak);
    }

    /// <summary>Soft magnitude clip (<c>ofdm_clip</c>, <c>ofdm.c:2683</c>): samples exceeding the
    /// threshold are scaled back to it, keeping their phase.</summary>
    private static void Clip(Span<Cf> tx, float threshold)
    {
        for (int i = 0; i < tx.Length; i++)
        {
            float mag = tx[i].Magnitude;
            if (mag > threshold)
            {
                tx[i] *= threshold / mag;
            }
        }
    }

    /// <summary>Mean carrier frequency (<c>find_carrier_centre</c>, <c>ofdm.c:570-575</c>) — the
    /// datac4/13/14 band-pass tuning centre.</summary>
    private float FindCarrierCentre()
    {
        int nlower = Mode.TxNlower;
        float acc = 0f;
        for (int c = 0; c < Mode.Nc + 2; c++)
        {
            acc += (nlower + c) * _docF;
        }

        return (float)((Mode.Fs / (2.0 * Math.PI)) * acc / (Mode.Nc + 2));
    }
}
