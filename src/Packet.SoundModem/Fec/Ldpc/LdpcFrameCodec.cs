namespace Packet.SoundModem.Fec.Ldpc;

/// <summary>The six FreeDV datac modes, each binding an <see cref="LdpcCode"/> and a
/// per-mode <c>data_bits_per_frame</c> (three are shortened — see
/// <see cref="DatacLdpc"/>).</summary>
public enum DatacMode
{
    /// <summary>256/128, 14-byte payload.</summary>
    Datac0,

    /// <summary>8192/4096, 510-byte payload — the workhorse.</summary>
    Datac1,

    /// <summary>2048/1024, 126-byte payload.</summary>
    Datac3,

    /// <summary>2048/1024 shortened to 448 data bits, 54-byte payload.</summary>
    Datac4,

    /// <summary>512/256 shortened to 128 data bits, 14-byte payload.</summary>
    Datac13,

    /// <summary>112/56 shortened to 40 data bits, 3-byte payload.</summary>
    Datac14,
}

/// <summary>
/// The FreeDV frame LDPC codec — a port of codec2's <c>ldpc_encode_frame</c> /
/// <c>ldpc_decode_frame</c> <c>LDPC_PROT_2020</c> path (<c>interldpc.c</c>). It adds
/// code-shortening on top of the raw <see cref="LdpcEncoder"/>/<see cref="LdpcDecoder"/>: modes
/// whose payload is smaller than the mother code's K bits stuff the unused data positions with
/// known 1s (never transmitted), so only <c>data_bits + parity</c> bits go on air. Decode
/// rebuilds the full codeword — received data LLRs, a strong <c>−100</c> for each known-1 bit,
/// then the parity LLRs reindexed by the shortening amount — and returns the payload bits. The
/// non-shortened modes are the degenerate <c>unused == 0</c> case. Not thread-safe.
/// </summary>
public sealed class LdpcFrameCodec
{
    private readonly LdpcCode _c;
    private readonly int _dataBits;
    private readonly LdpcDecoder _decoder;
    private readonly byte[] _padded;   // K
    private readonly byte[] _pbits;    // M
    private readonly float[] _full;    // N
    private readonly byte[] _hard;     // N

    /// <summary>Creates a codec for <paramref name="code"/> carrying
    /// <paramref name="dataBits"/> payload bits (≤ <c>code.NumberRowsHcols</c>).</summary>
    public LdpcFrameCodec(LdpcCode code, int dataBits)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataBits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataBits, code.NumberRowsHcols);
        _c = code;
        _dataBits = dataBits;
        _decoder = new LdpcDecoder(code);
        _padded = new byte[code.NumberRowsHcols];
        _pbits = new byte[code.NumberParityBits];
        _full = new float[code.CodeLength];
        _hard = new byte[code.CodeLength];
    }

    /// <summary>Payload bits carried per frame.</summary>
    public int DataBits => _dataBits;

    /// <summary>On-air coded bits per frame: <see cref="DataBits"/> + parity.</summary>
    public int CodedBits => _dataBits + _c.NumberParityBits;

    private int Unused => _c.NumberRowsHcols - _dataBits;

    /// <summary>Encodes <paramref name="data"/> (<see cref="DataBits"/> bits) to
    /// <paramref name="codeword"/> (<see cref="CodedBits"/> bits): the data bits followed by
    /// the parity bits (stuffed known bits are not transmitted).</summary>
    public void Encode(ReadOnlySpan<byte> data, Span<byte> codeword)
    {
        data[.._dataBits].CopyTo(_padded);
        for (int i = _dataBits; i < _c.NumberRowsHcols; i++)
        {
            _padded[i] = 1;
        }

        LdpcEncoder.Encode(_c, _padded, _pbits);
        data[.._dataBits].CopyTo(codeword);
        _pbits.CopyTo(codeword[_dataBits..]);
    }

    /// <summary>Decodes <paramref name="llr"/> (<see cref="CodedBits"/> LLRs, positive ⇒ bit 0)
    /// to <paramref name="outData"/> (<see cref="DataBits"/> payload bits). Returns the decoder
    /// iteration count.</summary>
    public int Decode(ReadOnlySpan<float> llr, Span<byte> outData, out int parityCheckCount)
    {
        int n = _c.CodeLength, k = _c.NumberRowsHcols, unused = Unused;
        for (int i = 0; i < _dataBits; i++)
        {
            _full[i] = llr[i];
        }

        for (int i = _dataBits; i < k; i++)
        {
            _full[i] = -100.0f;     // known stuffed bit = 1 ⇒ strong negative LLR
        }

        for (int i = k; i < n; i++)
        {
            _full[i] = llr[i - unused];   // parity, reindexed past the un-sent knowns
        }

        int iter = _decoder.Decode(_full, _hard, out parityCheckCount);
        _hard.AsSpan(0, _dataBits).CopyTo(outData);
        return iter;
    }
}

/// <summary>Builds the <see cref="LdpcFrameCodec"/> for a <see cref="DatacMode"/> — the
/// mode → (code, data_bits) map from codec2's <c>ofdm_mode.c</c> /
/// <c>ldpc_mode_specific_setup</c> (docs/ofdm-design.md §5).</summary>
public static class DatacLdpc
{
    /// <summary>Creates the frame codec for <paramref name="mode"/>.</summary>
    public static LdpcFrameCodec Create(DatacMode mode) => mode switch
    {
        DatacMode.Datac0 => new LdpcFrameCodec(LdpcCodes.H_128_256_5, 128),
        DatacMode.Datac1 => new LdpcFrameCodec(LdpcCodes.H_4096_8192_3d, 4096),
        DatacMode.Datac3 => new LdpcFrameCodec(LdpcCodes.H_1024_2048_4f, 1024),
        DatacMode.Datac4 => new LdpcFrameCodec(LdpcCodes.H_1024_2048_4f, 448),
        DatacMode.Datac13 => new LdpcFrameCodec(LdpcCodes.H_256_512_4, 128),
        DatacMode.Datac14 => new LdpcFrameCodec(LdpcCodes.HRA_56_56, 40),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
