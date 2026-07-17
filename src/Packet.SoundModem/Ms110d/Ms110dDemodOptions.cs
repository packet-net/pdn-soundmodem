namespace Packet.SoundModem.Ms110d;

/// <summary>Receiver state (design §2.6).</summary>
public enum Ms110dRxState
{
    /// <summary>Matched-filtering for the preamble Fixed subsection.</summary>
    Searching,

    /// <summary>Fixed-section peak found; reading downcount + WID symbols.</summary>
    ReadingPreamble,

    /// <summary>Autobaud complete; demodulating data frames.</summary>
    Tracking,
}

/// <summary>Why a burst ended (D.5.4.5 mandatory exits).</summary>
public enum Ms110dBurstEndReason
{
    /// <summary>The EOM marker was found in the decoded bits (D.5.4.5.1 — the receiver
    /// always scans).</summary>
    Eom,

    /// <summary>The configured input-data-block limit was reached (D.5.4.5.3).</summary>
    MaxInputDataBlocks,

    /// <summary>The terminate-receive command (D.5.4.5.2 / D.5.4.6.d).</summary>
    Terminated,

    /// <summary>Probe correlation collapsed / signal disappeared mid-burst.</summary>
    SignalLost,
}

/// <summary>Autobaud result: what the WID + downcount announced.</summary>
/// <param name="WaveformNumber">Decoded waveform number.</param>
/// <param name="Interleaver">Decoded interleaver option.</param>
/// <param name="ConstraintLength">Decoded constraint length (7 or 9).</param>
/// <param name="CfoHz">Estimated carrier frequency offset at lock.</param>
public sealed record Ms110dLockInfo(
    int WaveformNumber, Ms110dInterleaverKind Interleaver, int ConstraintLength, double CfoHz);

/// <summary>One decoded input-data (interleaver) block.</summary>
/// <param name="Index">Block index within the burst, from 0.</param>
/// <param name="Bits">Decoded info bits (0/1 bytes).</param>
public sealed record Ms110dRxBlock(int Index, byte[] Bits);

/// <summary>A completed burst.</summary>
/// <param name="PayloadBits">Decoded bits up to (not including) the EOM, or all decoded
/// bits when no EOM was seen.</param>
/// <param name="Reason">Which D.5.4.5 exit ended the burst.</param>
/// <param name="Blocks">Input-data blocks decoded.</param>
public sealed record Ms110dBurst(byte[] PayloadBits, Ms110dBurstEndReason Reason, int Blocks);

/// <summary>
/// Receiver options — the RX half of the D.5.4.6 remote-control parameter list. Any
/// real-world leniency discovered later becomes a named flag here (house rule).
/// </summary>
public sealed record Ms110dDemodOptions
{
    /// <summary>Stop after this many input-data blocks; 0 = unlimited (D.5.4.5.3).</summary>
    public int MaxInputDataBlocks { get; init; }

    /// <summary>Normalized matched-filter threshold for preamble detection (house number,
    /// characterized by the acquisition tests).</summary>
    public double SyncThreshold { get; init; } = 0.32;

    /// <summary>Decision-directed NLMS step for tap tracking across data blocks; −1 selects
    /// a per-mode default (0 for WN 1/2 whose symbol SNR makes DD updates counter-productive,
    /// 0.005 for WN 3/4, 0.01 for WN 5/6/13 — house numbers, design §2.5).</summary>
    public float DecisionDirectedMu { get; init; } = -1f;
}
