namespace Packet.SoundModem.Ofdm;

/// <summary>
/// Immutable per-mode OFDM parameters for a FreeDV datac mode, with the derived sizes computed
/// exactly as codec2's <c>ofdm_create</c> does (docs/ofdm-design.md §2; codec2 <c>ofdm_mode.c</c>
/// + <c>ofdm.c</c>, LGPL-2.1 — see PROVENANCE.md). All six modes are QPSK (bps 2) at 8000 Hz,
/// 1500 Hz nominal centre, Ns = 5 symbols/frame, no edge pilots, no text bits.
/// </summary>
public sealed record OfdmMode
{
    /// <summary>Mode name, "datac0"…"datac14".</summary>
    public required string Name { get; init; }

    /// <summary>Number of data carriers.</summary>
    public required int Nc { get; init; }

    /// <summary>OFDM symbols per frame (a pilot symbol + Ns−1 data symbols).</summary>
    public required int Ns { get; init; }

    /// <summary>Frames per modem packet.</summary>
    public required int Np { get; init; }

    /// <summary>Symbol duration, seconds.</summary>
    public required double Ts { get; init; }

    /// <summary>Cyclic-prefix duration, seconds.</summary>
    public required double Tcp { get; init; }

    /// <summary>Unique-word bits per packet.</summary>
    public required int Nuwbits { get; init; }

    /// <summary>TX amplitude scale (codec2 amp_scale).</summary>
    public required double AmpScale { get; init; }

    /// <summary>First clipper gain (before the hard clip).</summary>
    public required double ClipGain1 { get; init; }

    /// <summary>Second clipper gain (after the BPF).</summary>
    public required double ClipGain2 { get; init; }

    /// <summary>Sample rate — 8000 Hz for every datac mode.</summary>
    public double Fs { get; init; } = 8000.0;

    /// <summary>Nominal TX carrier centre — 1500 Hz for every datac mode.</summary>
    public double TxCentre { get; init; } = 1500.0;

    /// <summary>Bits per symbol — 2 (QPSK) for every datac mode.</summary>
    public int Bps { get; init; } = 2;

    /// <summary>Symbol rate 1/Ts (Hz).</summary>
    public double Rs => 1.0 / Ts;

    /// <summary>DFT length = samples per OFDM symbol body (ofdm.c:248).</summary>
    public int M => (int)(Fs / Rs);

    /// <summary>Cyclic-prefix length in samples (ofdm.c:249).</summary>
    public int Ncp => (int)(Tcp * Fs);

    /// <summary>Samples per OFDM symbol including the cyclic prefix.</summary>
    public int SamplesPerSymbol => M + Ncp;

    /// <summary>Samples per frame = Ns · (M+Ncp).</summary>
    public int SamplesPerFrame => Ns * SamplesPerSymbol;

    /// <summary>Samples per packet = Np · samples-per-frame.</summary>
    public int SamplesPerPacket => Np * SamplesPerFrame;

    /// <summary>Payload+UW coded bits per frame = (Ns−1)·Nc·bps (ofdm.c:297).</summary>
    public int BitsPerFrame => (Ns - 1) * Nc * Bps;

    /// <summary>Coded bits per packet = Np · bits-per-frame (ofdm.c:299).</summary>
    public int BitsPerPacket => Np * BitsPerFrame;

    /// <summary>QPSK symbols per packet = bits-per-packet / bps.</summary>
    public int SymsPerPacket => BitsPerPacket / Bps;

    /// <summary>Radian bin spacing 2π/M (ofdm.c:374).</summary>
    public double Doc => 2.0 * Math.PI / (Fs / Rs);

    /// <summary>Lowest occupied IFFT bin index (ofdm.c:376). C <c>roundf</c> is
    /// half-away-from-zero, and the half-integer cases are load-bearing.</summary>
    public int TxNlower =>
        (int)MathF.Round((float)(TxCentre / Rs) - (Nc / 2.0f), MidpointRounding.AwayFromZero) - 1;

    /// <summary>The six datac modes.</summary>
    public static OfdmMode ForName(string name) => name switch
    {
        "datac0" => Datac0,
        "datac1" => Datac1,
        "datac3" => Datac3,
        "datac4" => Datac4,
        "datac13" => Datac13,
        "datac14" => Datac14,
        _ => throw new ArgumentException($"unknown datac mode '{name}'", nameof(name)),
    };

    /// <summary>datac0 — 14-byte payload, 500 Hz OBW.</summary>
    public static OfdmMode Datac0 { get; } = new()
    {
        Name = "datac0", Nc = 9, Ns = 5, Np = 4, Ts = 0.016, Tcp = 0.006, Nuwbits = 32,
        AmpScale = 300e3, ClipGain1 = 2.2, ClipGain2 = 0.85,
    };

    /// <summary>datac1 — 510-byte payload, the workhorse, 1700 Hz OBW.</summary>
    public static OfdmMode Datac1 { get; } = new()
    {
        Name = "datac1", Nc = 27, Ns = 5, Np = 38, Ts = 0.016, Tcp = 0.006, Nuwbits = 16,
        AmpScale = 145e3, ClipGain1 = 2.7, ClipGain2 = 0.8,
    };

    /// <summary>datac3 — 126-byte payload, low-SNR, 500 Hz OBW.</summary>
    public static OfdmMode Datac3 { get; } = new()
    {
        Name = "datac3", Nc = 9, Ns = 5, Np = 29, Ts = 0.016, Tcp = 0.006, Nuwbits = 40,
        AmpScale = 300e3, ClipGain1 = 2.2, ClipGain2 = 0.8,
    };

    /// <summary>datac4 — 54-byte payload, 250 Hz OBW.</summary>
    public static OfdmMode Datac4 { get; } = new()
    {
        Name = "datac4", Nc = 4, Ns = 5, Np = 47, Ts = 0.016, Tcp = 0.006, Nuwbits = 32,
        AmpScale = 2 * 300e3, ClipGain1 = 1.2, ClipGain2 = 1.0,
    };

    /// <summary>datac13 — 14-byte payload, 200 Hz OBW.</summary>
    public static OfdmMode Datac13 { get; } = new()
    {
        Name = "datac13", Nc = 3, Ns = 5, Np = 18, Ts = 0.016, Tcp = 0.006, Nuwbits = 48,
        AmpScale = 2.5 * 300e3, ClipGain1 = 1.2, ClipGain2 = 1.0,
    };

    /// <summary>datac14 — 3-byte payload, 250 Hz OBW (M = 144, the non-power-of-two mode).</summary>
    public static OfdmMode Datac14 { get; } = new()
    {
        Name = "datac14", Nc = 4, Ns = 5, Np = 4, Ts = 0.018, Tcp = 0.005, Nuwbits = 32,
        AmpScale = 2.0 * 300e3, ClipGain1 = 2.0, ClipGain2 = 1.0,
    };
}
