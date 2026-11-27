using Packet.Ax25;
using Packet.Core;

namespace Packet.SoundModem.Waterfall;

/// <summary>
/// Builds the one AX.25 frame shape everything here ever needs to make: a two-address UI
/// frame (control 0x03, PID 0xF0), with <see cref="Ax25Frame.Ui"/> from packet.net doing the
/// encoding. The companion to <see cref="Ax25AddressParser"/>: probe/test-grade, no digipeater
/// path and no PID choices. It exists so the band probe, the bench tools and the test fixtures
/// share one call rather than each spelling a frame out.
/// </summary>
public static class Ax25UiFrame
{
    /// <summary>
    /// A UI frame <c>source&gt;destination</c> carrying <paramref name="payload"/>.
    /// Callsigns take the same <c>CALL</c> or <c>CALL-SSID</c> forms the parser produces (any
    /// case), so a built frame round-trips through <see cref="Ax25AddressParser.TryParse"/>.
    /// Command framing per convention: the destination's C bit set, the source's clear.
    /// </summary>
    /// <param name="source">Sending callsign, up to 6 characters plus an optional -SSID (0-15).</param>
    /// <param name="destination">Destination callsign in the same form.</param>
    /// <param name="payload">Information field, appended after control and PID.</param>
    public static byte[] Build(string source, string destination, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return Ax25Frame.Ui(Callsign.Parse(destination.ToUpperInvariant()), Callsign.Parse(source.ToUpperInvariant()), payload)
            .ToBytes();
    }
}
