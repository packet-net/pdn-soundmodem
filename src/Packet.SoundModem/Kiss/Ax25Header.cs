using Packet.Ax25;
using Packet.Core;

namespace Packet.SoundModem.Kiss;

/// <summary>
/// Questions about an AX.25 frame's header that the polyglot router and the host-interface
/// adapters ask: which station a frame was heard from, which it is for next, whether it is a UI
/// frame for everyone, and whether it is expedited. Shared by <see cref="PolyglotRouter"/> and
/// anything else that must read a header the same way.
/// </summary>
/// <remarks>
/// <para><b>It parses nothing itself.</b> <see cref="Ax25Frame"/> from packet.net reads the frame;
/// what is left here is the policy that picks an answer out of it. A frame that does not parse
/// yields null or false from every member, never a guess.</para>
/// <para><b>Parse options.</b> The library's lenient defaults, except that a blank callsign is
/// refused (<see cref="Ax25ParseOptions.AllowEmptyCallsignBase"/> off), as the router's own reader
/// always refused it. The other two leniencies stay on: trailing bytes after a supervisory frame
/// (a modulo-128 control field's second octet arrives that way, since the width is not known
/// here) and a SABM, SABME or DISC whose command/response bits say response (a legacy peer's;
/// routing does not care). Modulo 8 is assumed for the same reason: which class a control field
/// is in shows in its first octet at either modulo.</para>
/// </remarks>
internal static class Ax25Header
{
    private static readonly Ax25ParseOptions Options = new() { AllowEmptyCallsignBase = false };

    /// <summary>The station whose transmitter a received frame came from: the last digipeater
    /// that has repeated it, else the source. Null when the frame does not parse.</summary>
    internal static string? HeardStation(ReadOnlySpan<byte> frame)
    {
        if (Parse(frame) is not { } parsed)
        {
            return null;
        }

        // Latest repeater first: the last digipeater with its repeated bit set is the one on the air.
        for (int n = parsed.Digipeaters.Count - 1; n >= 0; n--)
        {
            if (parsed.Digipeaters[n].CrhBit)
            {
                return Station(parsed.Digipeaters[n]);
            }
        }

        return Station(parsed.Source);
    }

    /// <summary>The station a frame to be sent must reach first: the first digipeater that has not
    /// repeated it, else the destination. Null when the frame does not parse.</summary>
    internal static string? NextHop(ReadOnlySpan<byte> frame)
    {
        if (Parse(frame) is not { } parsed)
        {
            return null;
        }

        foreach (Ax25Address digipeater in parsed.Digipeaters)
        {
            if (!digipeater.CrhBit)
            {
                return Station(digipeater);
            }
        }

        return Station(parsed.Destination);
    }

    /// <summary>True for a UI frame with no digipeater still to repeat it, so it is for everyone.</summary>
    internal static bool IsUiWithNoDigipeaterPending(ReadOnlySpan<byte> frame) =>
        Parse(frame) is { IsUi: true } parsed && parsed.Digipeaters.All(d => d.CrhBit);

    /// <summary>True for a frame that should not queue behind data: RR, RNR, REJ, SREJ, UA and DM.
    /// False for everything else, and for a frame that does not parse.</summary>
    internal static bool IsExpedited(ReadOnlySpan<byte> frame) =>
        Parse(frame)?.FrameType is Ax25FrameType.Rr or Ax25FrameType.Rnr or Ax25FrameType.Rej
            or Ax25FrameType.Srej or Ax25FrameType.Ua or Ax25FrameType.Dm;

    /// <summary>An address as text: upper case <c>CALL-SSID</c>, SSID 0 written bare.</summary>
    internal static string Station(Ax25Address address) => address.Callsign.ToString();

    private static Ax25Frame? Parse(ReadOnlySpan<byte> frame) =>
        Ax25Frame.TryParse(frame, Options, out Ax25Frame? parsed) ? parsed : null;
}
