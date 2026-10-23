using Packet.SoundModem.CarrierSense;

namespace Packet.SoundModem.Channel;

/// <summary>
/// One carrier-sense answer and its reasons, from
/// <see cref="SoundModemChannel.ExplainCarrierSense"/>.
/// </summary>
/// <param name="Busy">The answer: what a transmission from <paramref name="SubChannel"/> would
/// have been held by.</param>
/// <param name="Transmitting">Whether the station was transmitting, which is busy by itself.</param>
/// <param name="SubChannel">Whose view this is, or null for the whole station.</param>
/// <param name="DecidedBy">What made it busy: <c>own transmission</c>, the source's name
/// (<c>radio</c>, <c>fm-shape</c>), <c>dcd subN</c> or <c>energy subN</c>, joined with <c>+</c>
/// when more than one did. Null when clear.</param>
/// <param name="Reading">When busy, the deciding detector's reading; when clear, the counted
/// detector nearest to firing. Null when no detector involved reports a reading.</param>
/// <param name="RadioSignalDbm">The radio's own signal reading, where the station has a radio that
/// reports one; null otherwise.</param>
/// <param name="Detectors">Every detector the station has, whether or not it counted.</param>
public sealed record CarrierSenseExplanation(
    bool Busy,
    bool Transmitting,
    int? SubChannel,
    string? DecidedBy,
    BusyReading? Reading,
    double? RadioSignalDbm,
    IReadOnlyList<CarrierSenseDetector> Detectors);

/// <summary>One detector's state inside a <see cref="CarrierSenseExplanation"/>.</summary>
/// <param name="Name">The source's name, or <c>subN mode</c> for a modem.</param>
/// <param name="Busy">Its own answer; null for a source that does not know.</param>
/// <param name="CarrierDetect">A modem's packet DCD; null for a source.</param>
/// <param name="Counts">Whether its answer counted towards this explanation's verdict.</param>
/// <param name="Reading">What it measured against its threshold, where it says.</param>
public sealed record CarrierSenseDetector(
    string Name, bool? Busy, bool? CarrierDetect, bool Counts, BusyReading? Reading);
