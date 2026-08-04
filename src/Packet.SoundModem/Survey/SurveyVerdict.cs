namespace Packet.SoundModem.Survey;

/// <summary>Why a burst is (or is not) worth keeping.</summary>
public enum SurveyVerdict
{
    /// <summary>Not a packet: too wide, too narrow, too long, or it never stopped. An SSB over,
    /// a carrier, a tuning whistle. Dropped.</summary>
    NotAPacket,

    /// <summary>Inside a configured modem's band, and something decoded on it. Normal traffic -
    /// the station already has this frame. Dropped.</summary>
    Decoded,

    /// <summary>Packet-shaped, and outside every configured modem's band. Nobody was listening
    /// there, so nothing can say what it was without keeping it.</summary>
    Unclaimed,

    /// <summary>Packet-shaped, inside a configured band, and nothing decoded. The station was
    /// listening and could not read it - the most useful capture there is, because it is a
    /// receiver problem rather than a coverage one.</summary>
    Missed,

    /// <summary>A frame decoded but carried no readable AX.25 addresses. The bytes are already
    /// in the frame log; the audio is kept too so the modulation can be re-examined alongside
    /// them.</summary>
    Unattributed,
}
