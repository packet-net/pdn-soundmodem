namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Receiver-side derived constants and tables for one FreeDV datac mode, computed exactly as
/// codec2's <c>ofdm_create</c> does (codec2 1.2.0, git 310777b, <c>ofdm.c:247-540</c>,
/// <c>ofdm_mode.c</c>, <c>wval.h</c>; LGPL-2.1 — see PROVENANCE.md). This carries the pieces the
/// demodulator needs that are not on <see cref="OfdmMode"/>: the pilot BPSK values and their
/// time-domain waveform, the timing-correlation normalisation constant, the 40&#160;Hz coarse
/// frequency table, the unique-word symbol placement, and the per-mode acquisition thresholds.
/// It also owns the direct per-symbol DFT/iDFT (codec2 <c>idft</c>/<c>dft</c>), which is a
/// <c>Nc+2</c>-bin transform over <c>m</c> samples — deliberately NOT an FFT (wrong bin geometry).
/// </summary>
public sealed class OfdmDemodConfig
{
    /// <summary>The exact int8 BPSK pilot sequence (codec2 <c>ofdm.c:88-92</c>). Only the first
    /// <c>Nc+2</c> are used (≤ 29 for datac1); ported verbatim.</summary>
    private static readonly sbyte[] PilotValues =
    [
        -1, -1, 1, 1, -1, -1, -1, 1, -1, 1, -1, 1, 1, 1, 1, 1,
        1, 1, 1, -1, -1, 1, -1, 1, -1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, -1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, 1,
        -1, 1, -1, 1, -1, -1, 1, -1, 1, 1, 1, 1, -1, 1, -1, 1,
    ];

    /// <summary>The mode this config derives from.</summary>
    public OfdmMode Mode { get; }

    /// <summary>Lowest occupied DFT bin on RX (codec2 <c>ofdm.c:377</c>). C <c>roundf</c> is
    /// half-away-from-zero. Equals <see cref="OfdmMode.TxNlower"/> since RX centre = TX centre.</summary>
    public int RxNlower { get; }

    /// <summary>Radian bin spacing <c>2π/m</c> (float, matching codec2's <c>ofdm->doc</c>).</summary>
    public float Doc { get; }

    /// <summary><c>1/m</c> (float), codec2 <c>ofdm->inv_m</c>.</summary>
    public float InvM { get; }

    /// <summary>Complex BPSK pilot symbols, <c>Nc+2</c> of them, edges zeroed (edge_pilots=0).</summary>
    public Cf[] Pilots { get; }

    /// <summary>Time-domain pilot waveform, <c>SamplesPerSymbol</c> long, cyclic-prefix region
    /// zeroed (codec2 <c>ofdm.c:495-518</c>).</summary>
    public Cf[] PilotSamples { get; }

    /// <summary>Normalisation constant for the timing correlation maximum
    /// (codec2 <c>ofdm.c:522-528</c>): <c>SamplesPerSymbol · Σ|pilot_samples|²</c>.</summary>
    public float TimingNorm { get; }

    /// <summary>The 40&#160;Hz frequency-shift table <c>exp(−j·2π·40·i/fs)</c>, first
    /// <c>SamplesPerSymbol</c> entries (codec2 <c>wval.h</c>).</summary>
    public Cf[] Wval { get; }

    /// <summary>Symbol indices carrying unique-word bits, over the packet
    /// (codec2 <c>ofdm.c:455-463</c>). Length <c>Nuwbits/2</c>.</summary>
    public int[] UwIndSym { get; }

    /// <summary>How many modem frames the UW is spread over (codec2 <c>ofdm.c:467-468</c>).</summary>
    public int Nuwframes { get; }

    /// <summary>Transmitted unique-word bits (codec2 <c>ofdm_mode.c</c> per-mode), length
    /// <c>Nuwbits</c>.</summary>
    public byte[] TxUw { get; }

    /// <summary>Fine-timing search window width (codec2: 80 for every datac mode).</summary>
    public int FtWindowWidth { get; }

    /// <summary>Unique-word error tolerance for the sync state machine (codec2 per-mode).</summary>
    public int BadUwErrors { get; }

    /// <summary>Timing correlation threshold to declare <c>timing_valid</c> (codec2 per-mode).</summary>
    public float TimingMxThresh { get; }

    /// <summary>Whether the mode uses an RX band-pass filter (datac4/13/14).</summary>
    public bool RxBpfEnable { get; }

    /// <summary>Mean carrier frequency in Hz (codec2 <c>find_carrier_centre</c>,
    /// <c>ofdm.c:570-575</c>) — the RX band-pass tuning centre for the narrow modes:
    /// datac4 1468.75, datac13 1500, datac14 ≈&#160;1472.22. Computed as codec2 does — a float
    /// summation over the occupied bins, not the algebraically-equal closed form — so the tuned
    /// filter coefficients match in the low bits (docs/ofdm-design.md §3.7/§4.9). The even-Nc
    /// modes sit half a carrier below the nominal 1500&#160;Hz by design.</summary>
    public float CarrierCentreHz { get; }

    // --- derived sizes (mirrors of ofdm_create, kept here so the demod reads one object) ---

    /// <summary>Sample rate (Hz).</summary>
    public double Fs => Mode.Fs;

    /// <summary>Symbol rate (Hz).</summary>
    public double Rs => Mode.Rs;

    /// <summary>DFT length = samples per OFDM symbol body.</summary>
    public int M => Mode.M;

    /// <summary>Cyclic-prefix length in samples.</summary>
    public int Ncp => Mode.Ncp;

    /// <summary>Samples per OFDM symbol (M + Ncp).</summary>
    public int SamplesPerSymbol => Mode.SamplesPerSymbol;

    /// <summary>Samples per modem frame.</summary>
    public int SamplesPerFrame => Mode.SamplesPerFrame;

    /// <summary>Samples per packet.</summary>
    public int SamplesPerPacket => Mode.SamplesPerPacket;

    /// <summary>Data carriers.</summary>
    public int Nc => Mode.Nc;

    /// <summary>Symbols per frame.</summary>
    public int Ns => Mode.Ns;

    /// <summary>Frames per packet.</summary>
    public int Np => Mode.Np;

    /// <summary>Unique-word bits.</summary>
    public int Nuwbits => Mode.Nuwbits;

    /// <summary>Data rows per frame (Ns−1).</summary>
    public int RowsPerFrame => Mode.Ns - 1;

    /// <summary>QPSK data symbols per frame = RowsPerFrame·Nc.</summary>
    public int NsymsPerFrame => RowsPerFrame * Mode.Nc;

    /// <summary>QPSK symbols per packet = BitsPerPacket/2.</summary>
    public int NsymsPerPacket => Mode.BitsPerPacket / Mode.Bps;

    /// <summary>Extra history at the front of rxbuf so the demod can step back (streaming).</summary>
    public int NrxBufHistory { get; }

    /// <summary>Minimum working samples the rxbuf must always hold.</summary>
    public int NrxBufMin { get; }

    /// <summary>Total rxbuf length.</summary>
    public int NrxBuf { get; }

    /// <summary>Frequency-offset update gain (codec2 <c>ofdm.c:417</c>).</summary>
    public float FoffEstGain => 0.1f;

    /// <summary>Builds the RX config for <paramref name="mode"/>.</summary>
    public OfdmDemodConfig(OfdmMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        Mode = mode;

        float rs = (float)mode.Rs;
        float rxCentre = (float)mode.TxCentre;    // rx_centre == tx_centre for every datac mode
        RxNlower = (int)MathF.Round((rxCentre / rs) - (mode.Nc / 2.0f), MidpointRounding.AwayFromZero) - 1;
        Doc = MathF.Tau / (mode.M);               // 2π/m; m == fs/rs exactly for datac
        InvM = 1.0f / mode.M;

        // Per-mode RX-only scalars (codec2 ofdm_mode.c). ftwindowwidth is 80 for all datac.
        FtWindowWidth = 80;
        (BadUwErrors, TimingMxThresh, RxBpfEnable, TxUw) = mode.Name switch
        {
            // datac0/1 do a single memcpy of the seed (rest stays zero); datac3/4/13/14 copy the
            // 24-bit seed twice — the second ending at nuwbits (ofdm_mode.c per-mode blocks).
            "datac0" => (9, 0.08f, false, BuildUw(32, Uw16, 16, secondCopy: false)),
            "datac1" => (6, 0.10f, false, BuildUw(16, Uw16, 16, secondCopy: false)),
            "datac3" => (10, 0.10f, false, BuildUw(40, Uw24, 24, secondCopy: true)),
            "datac4" => (12, 0.50f, true, BuildUw(32, Uw24, 24, secondCopy: true)),
            "datac13" => (18, 0.45f, true, BuildUw(48, Uw24, 24, secondCopy: true)),
            "datac14" => (12, 0.45f, true, BuildUw(32, Uw24, 24, secondCopy: true)),
            _ => throw new ArgumentException($"unknown datac mode '{mode.Name}'", nameof(mode)),
        };

        // Complex BPSK pilots, edges zeroed because edge_pilots == 0 (ofdm.c:366-371).
        Pilots = new Cf[mode.Nc + 2];
        for (int i = 0; i < mode.Nc + 2; i++)
        {
            Pilots[i] = new Cf(PilotValues[i], 0.0f);
        }

        Pilots[0] = Cf.Zero;
        Pilots[mode.Nc + 1] = Cf.Zero;

        // Time-domain pilot waveform via iDFT, CP region zeroed (ofdm.c:495-518).
        var temp = new Cf[mode.M];
        Idft(temp, Pilots);
        PilotSamples = new Cf[mode.SamplesPerSymbol];
        for (int i = 0; i < mode.Ncp; i++)
        {
            PilotSamples[i] = Cf.Zero;
        }

        for (int i = mode.Ncp, j = 0; j < mode.M; i++, j++)
        {
            PilotSamples[i] = temp[j];
        }

        float acc = 0.0f;
        for (int i = 0; i < mode.SamplesPerSymbol; i++)
        {
            acc += PilotSamples[i].Cnorm();
        }

        TimingNorm = mode.SamplesPerSymbol * acc;

        // 40 Hz table: ofdm_wval[i] = cmplxconj(2π·40·i/fs) (wval.h).
        Wval = new Cf[mode.SamplesPerSymbol];
        for (int i = 0; i < mode.SamplesPerSymbol; i++)
        {
            Wval[i] = Cf.CmplxConj((float)(MathF.Tau * 40.0f * i / (float)mode.Fs));
        }

        // Unique-word symbol placement (ofdm.c:445-468).
        int bps = mode.Bps;
        int nuwsyms = mode.Nuwbits / bps;
        int ndatasymsperframe = (mode.Ns - 1) * mode.Nc;
        int uwStep = mode.Nc + 1;
        int lastSym = nuwsyms * uwStep / bps;   // floorf of an integer expression
        if (lastSym >= mode.Np * ndatasymsperframe)
        {
            uwStep = mode.Nc - 1;
        }

        UwIndSym = new int[nuwsyms];
        for (int i = 0; i < nuwsyms; i++)
        {
            UwIndSym[i] = (i + 1) * uwStep / bps;
        }

        int symsperframe = mode.BitsPerFrame / bps;
        Nuwframes = (int)MathF.Ceiling((UwIndSym[nuwsyms - 1] + 1) / (float)symsperframe);

        // Mean carrier frequency (find_carrier_centre, ofdm.c:570-575) — ported as the float
        // summation loop, matching codec2's rounding sequence exactly.
        float centreAcc = 0.0f;
        for (int c = 0; c < mode.Nc + 2; c++)
        {
            centreAcc += (RxNlower + c) * Doc;
        }

        CarrierCentreHz = (float)((mode.Fs / (2.0 * Math.PI)) * centreAcc / (mode.Nc + 2));

        // Buffer model (ofdm.c:311-318); data_mode is always non-empty (streaming) for datac.
        NrxBufHistory = (mode.Np + 2) * mode.SamplesPerFrame;
        NrxBufMin = (3 * mode.SamplesPerFrame) + (3 * mode.SamplesPerSymbol);
        NrxBuf = NrxBufHistory + NrxBufMin;
    }

    // The unique-word literals from ofdm_mode.c (the 16- and 24-bit seeds).
    private static readonly byte[] Uw16 = [1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0];

    private static readonly byte[] Uw24 =
        [1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0];

    /// <summary>Reproduces the per-mode <c>tx_uw</c> assembly: memset 0, copy the seed at the
    /// front, and — only when <paramref name="secondCopy"/> is set — copy it again ending at
    /// <c>nuwbits</c>, exactly as codec2's <c>ofdm_mode.c</c> does (including the deliberate overlap
    /// for datac4/14). datac0/1 do a single copy and leave the tail zeroed.</summary>
    private static byte[] BuildUw(int nuwbits, byte[] seed, int seedLen, bool secondCopy)
    {
        var uw = new byte[nuwbits];
        Array.Copy(seed, 0, uw, 0, seedLen);
        if (secondCopy)
        {
            // datac3/4/13/14: second memcpy(&tx_uw[nuwbits - seedLen], seed, seedLen)
            Array.Copy(seed, 0, uw, nuwbits - seedLen, seedLen);
        }

        return uw;
    }

    /// <summary>Frequency → time (codec2 <c>idft</c>, <c>ofdm.c:642-667</c>). <paramref name="vec"/>
    /// is <c>Nc+2</c> bins; <paramref name="result"/> is <c>m</c> samples. Uses <see cref="RxNlower"/>'s
    /// TX counterpart via the shared centre.</summary>
    public void Idft(Span<Cf> result, ReadOnlySpan<Cf> vec)
    {
        int txNlower = Mode.TxNlower;
        Cf sum = Cf.Zero;
        for (int col = 0; col < Nc + 2; col++)
        {
            sum += vec[col];
        }

        result[0] = sum * InvM;

        for (int row = 1; row < M; row++)
        {
            Cf c = Cf.Cmplx(txNlower * Doc * row);
            Cf delta = Cf.Cmplx(Doc * row);
            Cf accRow = Cf.Zero;
            for (int col = 0; col < Nc + 2; col++)
            {
                accRow += vec[col] * c;
                c *= delta;
            }

            result[row] = accRow * InvM;
        }
    }

    /// <summary>Time → frequency (codec2 <c>dft</c>, <c>ofdm.c:674-692</c>). <paramref name="vec"/>
    /// is <c>m</c> samples; <paramref name="result"/> is <c>Nc+2</c> bins.</summary>
    public void Dft(Span<Cf> result, ReadOnlySpan<Cf> vec)
    {
        for (int col = 0; col < Nc + 2; col++)
        {
            float tval = (RxNlower + col) * Doc;
            Cf c = Cf.CmplxConj(tval);
            Cf delta = c;
            Cf accCol = vec[0];   // conj(cexp(j0)) == 1
            for (int row = 1; row < M; row++)
            {
                accCol += vec[row] * c;
                c *= delta;
            }

            result[col] = accCol;
        }
    }
}
