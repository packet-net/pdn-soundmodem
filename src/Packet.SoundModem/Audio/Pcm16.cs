namespace Packet.SoundModem.Audio;

/// <summary>
/// The one float/16-bit PCM conversion policy, shared by every path that crosses that
/// boundary (ALSA, WAV files, the codec2 feed, the waterfall's browser audio, the ARDOP
/// bridge). It grew four independent spellings that quietly disagreed - round versus
/// truncate, ±32767 versus a −32768-reaching clamp - and a policy that lives in one place
/// cannot drift again.
/// </summary>
/// <remarks>
/// <para><b>Out</b> (<see cref="FromFloat"/>): clamp to ±1, scale by 32767, round to
/// nearest. Rounding beats truncation because truncation carries a half-LSB DC bias;
/// the symmetric ±32767 clamp gives up the one extra negative code in exchange for a
/// converter whose transfer curve is odd-symmetric, which is what audio wants.</para>
/// <para><b>In</b> (<see cref="ToFloat"/>): divide by 32768, the codec convention every
/// reader here already used - full-scale negative input maps exactly to −1.0.</para>
/// <para>Both are single-expression and branch-light on purpose: they sit inside
/// per-sample loops on the device paths, and the JIT inlines them.</para>
/// </remarks>
public static class Pcm16
{
    /// <summary>One float sample (nominal ±1) to 16-bit PCM: clamp, scale by 32767, round.</summary>
    public static short FromFloat(float sample) =>
        (short)MathF.Round(Math.Clamp(sample, -1f, 1f) * 32767f);

    /// <summary>One 16-bit PCM sample to float: divide by 32768 (−32768 maps to −1.0).</summary>
    public static float ToFloat(short sample) => sample / 32768f;
}
