namespace Packet.SoundModem.Modems.OfdmFm.Link;

/// <summary>What a burst is: the header's 3-bit type. Values 3 to 7 are spare and read back as whatever number was sent.</summary>
public enum BurstKind
{
    /// <summary>A burst with a payload.</summary>
    Data = 0,

    /// <summary>A header with no payload: the format the sender recommends to the destination, and whether the destination's last burst failed.</summary>
    Report = 1,

    /// <summary>A burst that carries the sender's callsign and capability word.</summary>
    Identification = 2,
}

/// <summary>
/// The fields of a burst header, version 0 of <c>docs/dev/ofdm-fm/adaptation-protocol.md</c>.
/// </summary>
/// <param name="Version">0 for this protocol. Anything else is read back with this field and the others zero (see <see cref="BurstHeaderCodec.TryRead"/>).</param>
/// <param name="Kind">The burst type.</param>
/// <param name="Sender">The sending station, 0 to 63.</param>
/// <param name="Destination">The station it is for, 0 to 63; 0 means everyone.</param>
/// <param name="Format">The standard format of the payload (see <see cref="StandardFormats"/>). In a report: the format recommended to the destination.</param>
/// <param name="PayloadSymbols">Payload symbols, a multiple of four from 0 to 252. In a report: 0.</param>
/// <param name="BurstNumber">0 to 15; a data burst's count, which seeds the payload scrambler. In a report: 8 when "failed", else 0.</param>
public readonly record struct BurstHeader(int Version, BurstKind Kind, int Sender, int Destination, int Format, int PayloadSymbols, int BurstNumber)
{
    /// <summary>The report's "failed" flag: the top bit of the burst-number field. Meaningful for a report only.</summary>
    public bool Failed => (BurstNumber & 8) != 0;

    /// <summary>The format a report recommends; the same field as <see cref="Format"/>.</summary>
    public int RecommendedFormat => Format;

    /// <summary>True for version 0, the only one this code understands.</summary>
    public bool IsKnownVersion => Version == 0;

    /// <summary>A version 0 data burst.</summary>
    public static BurstHeader Data(int sender, int destination, int format, int payloadSymbols, int burstNumber) =>
        new(0, BurstKind.Data, sender, destination, format, payloadSymbols, burstNumber);

    /// <summary>A report from <paramref name="sender"/> to <paramref name="destination"/>: the recommended format, no payload, and the failed flag.</summary>
    public static BurstHeader Report(int sender, int destination, int recommendedFormat, bool failed) =>
        new(0, BurstKind.Report, sender, destination, recommendedFormat, 0, failed ? 8 : 0);
}
