namespace Packet.SoundModem.Modems;

/// <summary>
/// Builds the pre-flag training run for classic-HDLC transmissions: zero bits, which NRZI
/// turns into a level change every bit — both tones, maximum transition density.
/// </summary>
/// <remarks>
/// An all-flags TXDELAY fill trains a receiver poorly: a flag is 87.5 % one tone, so the
/// opposite tone appears as isolated 1-bit excursions that barely emerge from the receive
/// low-pass, and a cold slicer/clock mis-reads exactly those bits — measured here as
/// periodic errors on every flag boundary for the first ~40 bits, which is why bare-HDLC
/// AFSK needed ~100 ms of preamble while the IL2P modes (whose framer already sends an
/// alternating preamble) acquired from 16 bits. Zeros before the opening flag are free:
/// nothing in HDLC can mistake a run of zeros for a flag, and deframers ignore everything
/// before the first flag. The NinoTNC interoperates with either fill.
/// </remarks>
internal static class TrainingPreamble
{
    /// <summary>Prepends zero bits sized to <paramref name="txDelayMilliseconds"/>
    /// (minus the two opening flags already in <paramref name="framedBits"/>), with an
    /// 8-bit minimum so even TXDELAY 0 carries some training.</summary>
    internal static byte[] Prepend(byte[] framedBits, int txDelayMilliseconds, int baud)
    {
        int trainingBits = Math.Max(8, (int)((long)txDelayMilliseconds * baud / 1000) - 16);
        var bits = new byte[trainingBits + framedBits.Length];
        framedBits.CopyTo(bits, trainingBits);   // leading zeros are the training run
        return bits;
    }
}
