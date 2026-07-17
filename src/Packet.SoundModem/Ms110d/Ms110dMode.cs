namespace Packet.SoundModem.Ms110d;

/// <summary>Data-symbol modulation per waveform number (Table D-II row 3).</summary>
public enum Ms110dModulation
{
    /// <summary>WN 0: 32-chip 4-ary orthogonal Walsh channel symbols, no mini-probes.</summary>
    Walsh,

    /// <summary>WN 1–5: binary PSK (transcoding Table D-III).</summary>
    Bpsk,

    /// <summary>WN 6 and 13: quaternary PSK (transcoding Table D-IV).</summary>
    Qpsk,
}

/// <summary>Interleaver length option, signalled by WID bits d5d4 (Table D-XVI).</summary>
public enum Ms110dInterleaverKind
{
    /// <summary>d5d4 = 00.</summary>
    UltraShort,

    /// <summary>d5d4 = 01.</summary>
    Short,

    /// <summary>d5d4 = 10.</summary>
    Medium,

    /// <summary>d5d4 = 11.</summary>
    Long,
}

/// <summary>
/// One 3 kHz waveform-number row: modulation, U/K frame geometry (Tables D-XI/D-XII,
/// <c>d11-unknown-data-symbols.csv</c>/<c>d12-known-miniprobe-symbols.csv</c> row 3), code rate
/// (Table D-XLIX, <c>d49-code-rates.csv</c> row 3) and user data rate (Table D-II,
/// <c>d02-rate-modulation.csv</c> row 3).
/// </summary>
/// <param name="Wn">Waveform number (0–13).</param>
/// <param name="Modulation">Data modulation.</param>
/// <param name="BitsPerSymbol">Coded bits per data channel symbol (Walsh: per 32-chip symbol).</param>
/// <param name="U">Unknown (data) symbols per frame (0 for WN 0 — continuous).</param>
/// <param name="K">Known (mini-probe) symbols per frame (0 for WN 0 — no probes).</param>
/// <param name="CodeRate">Code rate as printed ("3/4" etc.).</param>
/// <param name="Bps">User data rate, bits/s.</param>
public sealed record Ms110dMode(
    int Wn, Ms110dModulation Modulation, int BitsPerSymbol, int U, int K, string CodeRate, int Bps)
{
    /// <summary>3 kHz mode table for the Phase A waveform numbers (0–6 and 13).
    /// 8PSK/QAM (WN 7–12) land in later phases.</summary>
    public static Ms110dMode Mode3k(int wn)
    {
        return wn switch
        {
            0 => new(0, Ms110dModulation.Walsh, 2, 0, 0, "1/2", 75),
            1 => new(1, Ms110dModulation.Bpsk, 1, 48, 48, "1/8", 150),
            2 => new(2, Ms110dModulation.Bpsk, 1, 48, 48, "1/4", 300),
            3 => new(3, Ms110dModulation.Bpsk, 1, 96, 32, "1/3", 600),
            4 => new(4, Ms110dModulation.Bpsk, 1, 96, 32, "2/3", 1200),
            5 => new(5, Ms110dModulation.Bpsk, 1, 256, 32, "3/4", 1600),
            6 => new(6, Ms110dModulation.Qpsk, 2, 256, 32, "3/4", 3200),
            13 => new(13, Ms110dModulation.Qpsk, 2, 256, 32, "9/16", 2400),
            _ => throw new ArgumentOutOfRangeException(
                nameof(wn), wn, "Phase A implements 3 kHz waveform numbers 0–6 and 13"),
        };
    }
}

/// <summary>
/// One Table D-XXXVII / D-LI row cell: the 3 kHz interleaver geometry for a (WN, length)
/// pair (<c>d37-interleaver-3khz.csv</c>, <c>d51-interleaver-increments-3khz.csv</c>).
/// </summary>
/// <param name="Frames">Data frames (WN 0: Walsh channel symbols) per interleaver block.</param>
/// <param name="SizeBits">Interleaver size in bits.</param>
/// <param name="InputBits">Info bits per interleaver block (= tail-biting block length).</param>
/// <param name="Increment">Interleaver load increment.</param>
public sealed record Ms110dInterleaverParams(int Frames, int SizeBits, int InputBits, int Increment)
{
    /// <summary>Looks up the 3 kHz interleaver parameters. WN 0 has no UltraShort option
    /// (Table D-XXXVII dash).</summary>
    public static Ms110dInterleaverParams Get3k(int wn, Ms110dInterleaverKind kind)
    {
        int i = (int)kind;
        (int[] frames, int[] size, int[] input, int[] inc) = wn switch
        {
            0 => (new[] { 0, 40, 144, 576 }, new[] { 0, 80, 288, 1152 }, new[] { 0, 40, 144, 576 }, new[] { 0, 11, 37, 145 }),
            1 => (new[] { 4, 16, 64, 256 }, new[] { 192, 768, 3072, 12288 }, new[] { 24, 96, 384, 1536 }, new[] { 25, 97, 385, 1543 }),
            2 => (new[] { 4, 16, 64, 256 }, new[] { 192, 768, 3072, 12288 }, new[] { 48, 192, 768, 3072 }, new[] { 25, 97, 385, 1543 }),
            3 => (new[] { 2, 8, 32, 128 }, new[] { 192, 768, 3072, 12288 }, new[] { 64, 256, 1024, 4096 }, new[] { 25, 97, 385, 1549 }),
            4 => (new[] { 2, 8, 32, 128 }, new[] { 192, 768, 3072, 12288 }, new[] { 128, 512, 2048, 8192 }, new[] { 25, 97, 385, 1549 }),
            5 => (new[] { 1, 4, 16, 64 }, new[] { 256, 1024, 4096, 16384 }, new[] { 192, 768, 3072, 12288 }, new[] { 33, 129, 513, 2081 }),
            6 => (new[] { 1, 4, 16, 64 }, new[] { 512, 2048, 8192, 32768 }, new[] { 384, 1536, 6144, 24576 }, new[] { 65, 257, 1025, 4161 }),
            13 => (new[] { 1, 4, 16, 64 }, new[] { 512, 2048, 8192, 32768 }, new[] { 288, 1152, 4608, 18432 }, new[] { 65, 257, 1025, 4161 }),
            _ => throw new ArgumentOutOfRangeException(
                nameof(wn), wn, "Phase A implements 3 kHz waveform numbers 0–6 and 13"),
        };

        if (size[i] == 0)
        {
            throw new ArgumentException("WN 0 has no UltraShort interleaver (Table D-XXXVII)", nameof(kind));
        }

        return new Ms110dInterleaverParams(frames[i], size[i], input[i], inc[i]);
    }
}
