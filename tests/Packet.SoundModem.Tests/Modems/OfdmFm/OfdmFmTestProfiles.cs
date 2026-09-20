using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The carrier layouts the OFDM-FM tests and probes measure on: the shipped presets, stripped back
/// to bare layouts for a test to dress as it needs.
/// </summary>
/// <remarks>
/// <para><b>A measurement is only worth reading if you know what it was taken on.</b> These used to
/// be looked up by name in a station's own untracked geometry file and fell back to
/// <see cref="OfdmFmParameters.Synthetic"/> when there was no such file, so the same probe could
/// measure a real span on one machine and a toy waveform on another, print a table either way and
/// pass either way. Naming the layout here is the fix: what a test measured is in the test. A test
/// that wants the toy asks for <see cref="OfdmFmParameters.Synthetic"/> by name and says why.</para>
/// <para>Taken from <see cref="OfdmFmPresets"/> rather than restated, so the numbers live in one
/// place and a test measures what the repository actually ships. What is stripped is everything a
/// measurement varies for itself: the coding, the constellation, the peak limit and the burst-form
/// options. A test applies its own with <c>with { }</c>, which is what makes what it varied visible
/// at the measurement.</para>
/// <para>None of them carries a <c>GeometryId</c>, because no shipped preset does: each runs alone
/// and acquires on its own layout. The geometry table has its own tests, on invented layouts (see
/// <see cref="GeometrySignallingTests"/>).</para>
/// </remarks>
internal static class OfdmFmTestProfiles
{
    /// <summary>
    /// The voice-bandwidth layout, bare. The narrowest span this modem ships, and where a test
    /// whose thresholds were measured on a narrow profile belongs.
    /// </summary>
    public static OfdmFmParameters Narrow { get; } = Bare(OfdmFmPresets.Narrow);

    /// <summary>The 6 kHz span, bare.</summary>
    public static OfdmFmParameters SixKhz { get; } = Bare(OfdmFmPresets.Robust6k);

    /// <summary>
    /// The 8 kHz span, bare: the default preset's layout, the widest that has been on the air, and
    /// what a ladder or a campaign measures on when it is meant to say something about what a
    /// station runs.
    /// </summary>
    public static OfdmFmParameters EightKhz { get; } = Bare(OfdmFmPresets.Default8k);

    /// <summary>
    /// The profile named by <c>OFDMFM_PROFILE</c> in a station's own local geometry file, or
    /// <paramref name="otherwise"/> when the variable is not set.
    /// </summary>
    /// <param name="name">The profile name, from the environment; null or empty asks for nothing.
    /// </param>
    /// <param name="otherwise">What to measure on when no profile was named.</param>
    /// <exception cref="InvalidOperationException">A profile was named and is not there.</exception>
    /// <remarks>
    /// Throwing is the point. A probe that quietly measured something else when the name did not
    /// resolve would still print a full table, and nothing in it would say which waveform it
    /// describes.
    /// </remarks>
    public static OfdmFmParameters Named(string? name, OfdmFmParameters otherwise)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return otherwise;
        }

        IReadOnlyDictionary<string, OfdmFmParameters>? local = OfdmFmParameters.LoadLocal();
        return local is not null && local.TryGetValue(name, out OfdmFmParameters? found)
            ? found
            : throw new InvalidOperationException(
                $"OFDMFM_PROFILE names '{name}', which no local geometry file on this machine "
                + "holds. Unset it to measure on a preset stated in the tests.");
    }

    /// <summary>A preset's carrier layout with every transmit choice taken back off it.</summary>
    private static OfdmFmParameters Bare(OfdmFmParameters preset) => preset with
    {
        Coding = null,
        Constellation = OfdmFmConstellation.Qpsk,
        PeakToAverageLimitDb = null,
        ContiguousBursts = false,
        FollowOnFrames = false,
        AdaptiveRate = false,
    };
}
