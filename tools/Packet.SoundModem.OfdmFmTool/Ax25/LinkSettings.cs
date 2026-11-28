using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.SoundModem.OfdmFmTool.Ax25;

/// <summary>
/// The AX.25 link's timers and window for the trial, fixed. The first exchange from cold holds two identifications with Morse (about 25 s), so the acknowledgement timer starts high and the stack's own smoothing brings it down
/// from measured round trips. It is started from <c>Transmitted</c>, not from the moment a frame was handed to the modem. The window is four frames of 256 bytes, modulo 8, which the modem carries at any rate it has.
/// </summary>
internal static class LinkSettings
{
    public static readonly TimeSpan T1 = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan T2 = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan T3 = TimeSpan.FromSeconds(120);

    public const int Retries = 10;

    public const int Window = 4;

    /// <summary>The largest information field the stack is held to, and so the most one I frame carries. It stays well inside the 331 bytes one burst carries at the slowest rate.</summary>
    public const int Paclen = 256;

    public static Ax25ListenerOptions Options(Callsign call, Action<Ax25Session> configure) => new()
    {
        MyCall = call,
        PortName = "ofdm-fm",
        T1V = T1,
        T2 = T2,
        T3 = T3,
        N2 = Retries,
        K = Window,
        PreferExtendedConnect = false,
        PreConnectXidNegotiatesSrej = false,
        RestartT1OnTxComplete = true,
        ConfigureSession = configure,
    };
}
