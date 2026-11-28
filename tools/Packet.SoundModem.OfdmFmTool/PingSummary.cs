using System.Globalization;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>The end of a ping run: how many went, how many came back, and the rates the modem last reported.</summary>
/// <param name="Sent">Datagrams sent.</param>
/// <param name="Answered">Echoes received.</param>
/// <param name="OutBitsPerSecond">The rate this station sends to the other at, as last reported, or null if never.</param>
/// <param name="BackBitsPerSecond">The rate the other station can send back at, as last recommended, or null if never.</param>
internal sealed record PingSummary(int Sent, int Answered, double? OutBitsPerSecond, double? BackBitsPerSecond)
{
    /// <summary>The rate that works both ways: the lower of the two, or null if either is unknown.</summary>
    public double? BothWayBitsPerSecond =>
        OutBitsPerSecond is { } outward && BackBitsPerSecond is { } back ? Math.Min(outward, back) : null;

    public override string ToString()
    {
        string line = $"{Sent} sent, {Answered} answered";
        return BothWayBitsPerSecond is { } both ? $"{line}, {Rate(both)} kbit/s both ways" : line;
    }

    /// <summary>A rate in kbit/s as the modem's own trace writes it: two places under 1, one above.</summary>
    public static string Rate(double bitsPerSecond)
    {
        double kbit = bitsPerSecond / 1000.0;
        return kbit.ToString(kbit < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
    }

    public static string RateOrUnknown(double? bitsPerSecond) => bitsPerSecond is { } rate ? $"{Rate(rate)} kbit/s" : "not known yet";
}
