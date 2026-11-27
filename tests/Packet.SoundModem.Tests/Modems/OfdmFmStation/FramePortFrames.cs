using Packet.Ax25;
using Packet.Core;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>AX.25 frames for the frame port's tests, all made by the packet.net library.</summary>
internal static class FramePortFrames
{
    public static readonly Callsign Dest = Callsign.Parse("M0XYZ-1");
    public static readonly Callsign Src = Callsign.Parse("G4ABC-1");

    public static Callsign Call(string text) => Callsign.Parse(text);

    public static byte[] Info(int seed, int length)
    {
        var info = new byte[length];
        new Random(seed).NextBytes(info);
        return info;
    }

    /// <summary>
    /// The same frame with its first <paramref name="count"/> digipeaters marked as repeated. The library writes each address slot, so the has-been-repeated bit is set by it and not here.
    /// A digipeater slot starts after the two 7-byte addresses of destination and source.
    /// </summary>
    public static byte[] Repeated(byte[] frame, int count)
    {
        if (!Ax25Frame.TryParse(frame, out Ax25Frame? parsed))
        {
            throw new ArgumentException("not a frame", nameof(frame));
        }

        byte[] marked = (byte[])frame.Clone();
        for (int n = 0; n < count; n++)
        {
            (parsed.Digipeaters[n] with { CrhBit = true }).Write(marked.AsSpan((2 + n) * Ax25Address.EncodedLength, Ax25Address.EncodedLength));
        }

        return marked;
    }

    public static byte[] I(string to, string from, byte ns, byte nr, int infoLength = 20, string[]? via = null) =>
        Ax25Frame.I(Call(to), Call(from), nr, ns, Info(ns, infoLength), digipeaters: via?.Select(Call)).ToBytes();

    public static byte[] Ui(string to, string from, int infoLength = 20, string[]? via = null) =>
        Ax25Frame.Ui(Call(to), Call(from), Info(5, infoLength), digipeaters: via?.Select(Call)).ToBytes();

    public static byte[] Sabm(string to, string from) => Ax25Frame.Sabm(Call(to), Call(from)).ToBytes();

    public static byte[] Disc(string to, string from) => Ax25Frame.Disc(Call(to), Call(from)).ToBytes();

    public static byte[] Ua(string to, string from) => Ax25Frame.Ua(Call(to), Call(from)).ToBytes();

    public static byte[] Dm(string to, string from) => Ax25Frame.Dm(Call(to), Call(from)).ToBytes();

    public static byte[] Rr(string to, string from) => Ax25Frame.Rr(Call(to), Call(from), 3, isCommand: false).ToBytes();

    public static byte[] Rnr(string to, string from) => Ax25Frame.Rnr(Call(to), Call(from), 3, isCommand: false).ToBytes();

    public static byte[] Rej(string to, string from) => Ax25Frame.Rej(Call(to), Call(from), 3, isCommand: false).ToBytes();

    public static byte[] Srej(string to, string from) => Ax25Frame.Srej(Call(to), Call(from), 3, isCommand: false).ToBytes();
}
