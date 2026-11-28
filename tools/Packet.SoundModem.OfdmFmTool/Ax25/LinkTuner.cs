namespace Packet.SoundModem.OfdmFmTool.Ax25;

/// <summary>What the modem says about the link to a peer, as the station reports it.</summary>
/// <param name="Located">The station knows where the peer's address lives. Until it does, the link is at first contact.</param>
/// <param name="OutBitsPerSecond">The rate the station sends to the peer at, or null if not known.</param>
/// <param name="BackBitsPerSecond">The rate the station has told the peer it can send back at, or null if not told yet.</param>
internal readonly record struct LinkView(bool Located, double? OutBitsPerSecond, double? BackBitsPerSecond);

/// <summary>The window and the acknowledgement timer to give a link.</summary>
internal readonly record struct Tuning(int K, TimeSpan T1V);

/// <summary>
/// Sets the AX.25 window (K) and the acknowledgement timer (T1V) of a link from what the modem says about it. Pure: the numbers and nothing else.
/// </summary>
/// <remarks>
/// <para><b>K.</b> As many frames as one burst carries at the rate to the peer, at least 1 and at most <see cref="MostFrames"/>. The library gives the rate and not the number of bytes a burst packs (its ceiling of 2.5 s of audio
/// is internal; the figure it does expose, a datagram's room, is for a single datagram and runs past the ceiling at the slow rates), so the capacity is derived: the ceiling less <see cref="BurstOverhead"/> for the lead-in and the opening,
/// times the rate. A frame is the paclen plus the longest AX.25 header and the modem's own per-datagram bytes (<see cref="FrameBytes"/>). At first contact (0.25 kbit/s, about 66 bytes a burst) that is 1; at 70 kbit/s it is the cap.</para>
/// <para><b>T1V.</b> With the stack starting T1 when our burst has left, T1 has to cover the answer: the peer's response delay (T2), its wait for a clear channel, any burst of its own it is part way through (up to the
/// same ceiling, or one frame at a slow rate), its acknowledgement's airtime at the rate back, and our decode. Those are added, multiplied by 1.5 for margin, rounded up to a whole second and held to at least
/// <see cref="LeastT1"/>. Until the link is located and a rate is known each way it is <see cref="FirstContactT1"/>: a cold exchange holds two identifications with Morse. A peer that is slower than this (an identification
/// ten minutes on) costs one poll and no more: the stack asks with a poll when T1 runs out.</para>
/// </remarks>
internal static class LinkTuner
{
    public const int MostFrames = 32;

    /// <summary>One burst is at most this much audio.</summary>
    public static readonly TimeSpan BurstCeiling = TimeSpan.FromSeconds(2.5);

    /// <summary>Audio in a burst that is not payload: the lead-in and the opening.</summary>
    public static readonly TimeSpan BurstOverhead = TimeSpan.FromSeconds(0.35);

    /// <summary>The paclen the link is held to.</summary>
    public const int Paclen = LinkSettings.Paclen;

    /// <summary>A frame in a burst: the paclen, the longest header (two addresses, a two byte control field and a PID), the datagram's length and its share of the run that says where it is from.</summary>
    public const int FrameBytes = Paclen + 26;

    public static readonly TimeSpan FirstContactT1 = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LeastT1 = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan PeerResponseDelay = LinkSettings.T2;
    private static readonly TimeSpan PeerChannelWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OurDecode = TimeSpan.FromSeconds(0.5);
    private const double AcknowledgementBytes = 20;
    private const double Margin = 1.5;

    public static Tuning Compute(LinkView link)
    {
        if (!link.Located || link.OutBitsPerSecond is not { } outRate || link.BackBitsPerSecond is not { } backRate || outRate <= 0 || backRate <= 0)
        {
            return new Tuning(1, FirstContactT1);
        }

        double burstBytes = outRate * (BurstCeiling - BurstOverhead).TotalSeconds / 8;
        int k = Math.Clamp((int)(burstBytes / FrameBytes), 1, MostFrames);

        // The peer may be part way through a burst of its own: at most the ceiling, or at a slow rate the one frame that goes alone.
        TimeSpan peerBurst = TimeSpan.FromSeconds(Math.Max(BurstCeiling.TotalSeconds, (FrameBytes * 8 / backRate) + BurstOverhead.TotalSeconds));
        TimeSpan acknowledgement = TimeSpan.FromSeconds((AcknowledgementBytes * 8 / backRate) + BurstOverhead.TotalSeconds);
        TimeSpan answer = PeerResponseDelay + PeerChannelWait + peerBurst + acknowledgement + OurDecode;
        TimeSpan t1 = TimeSpan.FromSeconds(Math.Ceiling(answer.TotalSeconds * Margin));
        return new Tuning(k, t1 < LeastT1 ? LeastT1 : t1);
    }
}
