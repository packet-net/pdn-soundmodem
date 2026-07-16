namespace Packet.SoundModem.Ardop;

/// <summary>One frame decoded by <see cref="ArdopDemodulator"/>.</summary>
public sealed record ArdopDecodedFrame
{
    /// <summary>The frame-type code.</summary>
    public required byte Type { get; init; }

    /// <summary>The frame-type name (<see cref="ArdopFrameType.Name"/>).</summary>
    public string Name => ArdopFrameType.Name(Type);

    /// <summary>True when the frame decoded cleanly (RS/CRC verified where the type
    /// carries them). A data frame with <c>Ok = false</c> still exposes its raw
    /// (uncorrected) payload bytes in <see cref="Data"/> — the FEC layer passes those
    /// to the host tagged <c>ERR</c>, and Memory-ARQ may yet recover a repeat.</summary>
    public required bool Ok { get; init; }

    /// <summary>Payload bytes: the net payload for data frames (raw payload-field bytes
    /// when <see cref="Ok"/> is false); empty for control frames.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Frame quality 0-100 (<c>Update4FSKConstellation</c>): for ACK/NAK types
    /// this reflects the type symbols; for data frames the whole frame.</summary>
    public required int Quality { get; init; }

    /// <summary>Caller station for ConReq/Ping, the station for ID frames.</summary>
    public string? Caller { get; init; }

    /// <summary>Target station for ConReq/Ping.</summary>
    public string? Target { get; init; }

    /// <summary>Grid square for ID frames.</summary>
    public string? GridSquare { get; init; }

    /// <summary>Remote-measured leader length in ms for ConAck frames.</summary>
    public int? ConAckLeaderMs { get; init; }

    /// <summary>Reported S:N in dB for PingAck frames (−10…21).</summary>
    public int? PingAckSnDb { get; init; }

    /// <summary>Reported quality for PingAck frames (30-100).</summary>
    public int? PingAckQuality { get; init; }
}
