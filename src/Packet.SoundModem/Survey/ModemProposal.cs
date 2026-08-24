namespace Packet.SoundModem.Survey;

/// <summary>What a station would have to change to read the traffic behind a proposal.</summary>
public enum ProposalKind
{
    /// <summary>Nothing is listening on that frequency. Add a modem.</summary>
    NewModem,

    /// <summary>A modem already covers the frequency and cannot read the framing - it runs
    /// IL2P+CRC and the station sends plain AX.25, or the reverse. The frequency is not the
    /// problem and moving anything would make it worse.</summary>
    FramingChange,
}

/// <summary>
/// A modem this station could run, and the traffic that says so.
/// </summary>
/// <param name="Mode">The catalogue mode that read the captures.</param>
/// <param name="AudioCentreHz">Where the traffic sits in the passband, averaged over the
/// captures that carried it.</param>
/// <param name="RfFrequencyHz">The same as a band frequency, where the station knows its dial -
/// which is what goes in a config file. Null on a station running in audio frequencies only.</param>
/// <param name="Kind">Whether this is a frequency nobody is listening to or a framing an
/// existing modem cannot read.</param>
/// <param name="Captures">Captures this proposal was read out of.</param>
/// <param name="Frames">Frames read across them, in total. Not distinct frames: the traffic
/// this finds is largely beacons, and a beacon's bytes never change.</param>
/// <param name="Stations">Source callsigns seen, most-heard first.</param>
/// <param name="MeanSnrDb">Mean of the captures' own peak SNR - how well the station hears it,
/// which decides whether a modem there would copy or merely detect.</param>
/// <param name="FirstHeard">When the earliest contributing capture was written.</param>
/// <param name="LastHeard">The latest.</param>
/// <param name="Conflicts">Sub-channels of the configured modems whose bands this overlaps -
/// empty for a clear frequency. On a <see cref="ProposalKind.FramingChange"/> this is the modem
/// that is already there and cannot read it.</param>
public sealed record ModemProposal(
    string Mode,
    double AudioCentreHz,
    double? RfFrequencyHz,
    ProposalKind Kind,
    int Captures,
    int Frames,
    IReadOnlyList<string> Stations,
    double MeanSnrDb,
    DateTimeOffset FirstHeard,
    DateTimeOffset LastHeard,
    IReadOnlyList<int> Conflicts)
{
    /// <summary>
    /// The proposal in a line an operator can act on, naming what to do and what says so.
    /// </summary>
    /// <remarks>
    /// Deliberately states the evidence rather than a confidence: "34 captures, 31 frames,
    /// PD4R-12" is checkable and "high confidence" is not, and the operator is the one who knows
    /// whether a station heard twice a day for three weeks is worth one of four modem slots.
    /// </remarks>
    public string Summary()
    {
        string where = RfFrequencyHz is double rf
            ? $"{rf / 1e6:F6} MHz"
            : $"{AudioCentreHz:F0} Hz audio";
        string who = Stations.Count switch
        {
            0 => "no readable callsigns",
            1 => Stations[0],
            2 => $"{Stations[0]} and {Stations[1]}",
            _ => $"{Stations[0]}, {Stations[1]} and {Stations.Count - 2} more",
        };

        string what = Kind == ProposalKind.FramingChange
            ? $"modem {string.Join("/", Conflicts)} already covers {where} and cannot read this "
                + $"framing; {Mode} can"
            : $"add {Mode} at {where}";

        return $"{what} - {Frames} frame(s) in {Captures} capture(s), {who}, "
            + $"{MeanSnrDb:F0} dB, {FirstHeard:yyyy-MM-dd} to {LastHeard:yyyy-MM-dd}";
    }
}
