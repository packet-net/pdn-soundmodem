namespace Packet.SoundModem.CarrierSense;

/// <summary>
/// What a carrier-sense detector last measured, against the level at which it calls the channel
/// busy. Both in dB on the detector's own scale, so the two are only comparable with each other.
/// </summary>
/// <remarks>
/// For diagnostics only: nothing decides on this. It exists so that a station asked "why did you
/// say busy" can answer with a number rather than a verdict (packet-net/pdn-soundmodem#723). The
/// scale is the detector's: dB over its own noise floor for <see cref="Modems.EnergyBusyDetector"/>,
/// the shape ratio for <see cref="FmShapeBusyDetector"/>, dBm for a radio's RSSI.
/// </remarks>
/// <param name="LevelDb">The last measurement.</param>
/// <param name="ThresholdDb">Where the detector asserts busy on the same scale.</param>
public readonly record struct BusyReading(double LevelDb, double ThresholdDb)
{
    /// <summary>How far over its threshold the reading is, negative when under.</summary>
    public double MarginDb => LevelDb - ThresholdDb;

    /// <summary>The one of two readings nearer to firing, either of which may be absent.</summary>
    public static BusyReading? Nearer(BusyReading? a, BusyReading? b) =>
        a is not { } x ? b
        : b is not { } y ? a
        : y.MarginDb > x.MarginDb ? y : x;
}

/// <summary>
/// A modem or carrier-sense source that can say what its busy detector last measured.
/// </summary>
/// <remarks>Optional: a modem without it simply reports no reading.</remarks>
public interface IBusyReadingSource
{
    /// <summary>The detector's last reading, or null before it has one.</summary>
    BusyReading? BusyReading { get; }
}
