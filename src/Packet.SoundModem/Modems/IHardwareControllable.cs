namespace Packet.SoundModem.Modems;

/// <summary>
/// A modem with runtime-settable hardware parameters, addressed by the KISS SETHW command
/// (type 6) on its port. The payload's meaning is the modem's own - SETHW is defined as
/// hardware-specific - and each implementation documents its layout. The one shipped
/// implementation is <see cref="Ms110d.Ms110dModem"/>: <c>payload[0]</c> = transmit waveform
/// number, optional <c>payload[1]</c> = interleaver (0 short, 1 long).
/// </summary>
/// <remarks>
/// The KISS server discovers the capability by type-testing the modem, applies the raw bytes,
/// and echoes the frame back to the sender on success (KISS has no error channel, so an
/// invalid payload is ignored and journalled, never answered). In-process hosts - the pdn
/// node's <c>kind: soundmodem</c> transport - should prefer the implementation's typed method
/// (<c>Ms110dModem.SetTxWaveform</c>) over the byte layout; a modem wrapped by
/// <see cref="FrequencyShiftedModem"/> forwards this interface and exposes the wrapped
/// instance as <see cref="FrequencyShiftedModem.Inner"/> for the typed path.
/// </remarks>
public interface IHardwareControllable
{
    /// <summary>
    /// Applies a SETHW payload. Returns true and describes what was applied, or returns false
    /// with the reason the payload was refused - in either case <paramref name="outcome"/> is
    /// a printable one-liner for the journal.
    /// </summary>
    bool TrySetHardware(ReadOnlySpan<byte> payload, out string outcome);
}
