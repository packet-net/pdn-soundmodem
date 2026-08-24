namespace Packet.SoundModem.Telemetry;

/// <summary>
/// The station behind a callsign, with its SSID set aside.
/// </summary>
/// <remarks>
/// <para>
/// A station running several SSIDs is one station on the air: <c>GB7IOW-1</c>, <c>GB7IOW-2</c>
/// and <c>GB7IOW-9</c> are one transmitter, one antenna and one path, and a chart that draws
/// them as three series is drawing one signal three times. So the base callsign is what a metric
/// is keyed by, and the SSID travels as a detail of the individual frame.
/// </para>
/// <para>
/// It also happens to bound a label: a busy node cycling SSIDs would otherwise multiply its own
/// series without transmitting anything new.
/// </para>
/// </remarks>
public static class StationCallsign
{
    /// <summary>
    /// The base callsign - everything before the SSID separator - and the SSID itself.
    /// </summary>
    /// <param name="callsign">A callsign as the address parser delivers it, e.g. <c>GB7IOW-1</c>.</param>
    /// <returns>The base, and the SSID text (empty when there is none).</returns>
    /// <remarks>
    /// Deliberately textual rather than a parse of the AX.25 SSID nibble: what arrives here has
    /// already been through <see cref="Waterfall.Ax25AddressParser"/>, which is the one place
    /// that knows the shifted-ASCII encoding, and re-deriving it from a string would be a second
    /// opinion about a question already answered.
    /// </remarks>
    public static (string Base, string Ssid) Split(string callsign)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        int dash = callsign.IndexOf('-', StringComparison.Ordinal);
        return dash < 0
            ? (callsign, "")
            : (callsign[..dash], callsign[(dash + 1)..]);
    }

    /// <summary>The base callsign alone - what a series is keyed by.</summary>
    public static string BaseOf(string callsign) => Split(callsign).Base;
}
