using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Performance criteria we have NOT met yet, expressed as tests that fail. Category
/// <c>Aspiration</c>: excluded from the blocking test run and executed in a non-blocking
/// CI step, so they are a visible scoreboard rather than a broken build.
/// </summary>
/// <remarks>
/// The discipline: when one of these starts passing, move it into
/// <see cref="NinoTncParityTests"/> (or the relevant suite) so it becomes a regression
/// guard — the whole point is that goals graduate into floors. Do not weaken an
/// aspiration to make it pass, and do not let this category grow stale: an aspiration
/// nobody intends to meet should be deleted, with the reasoning recorded in the issue it
/// references.
/// </remarks>
[Trait("Category", "Aspiration")]
public class NinoTncAspirationTests
{
    /// <summary>
    /// Full NinoTNC mode coverage (issue #1 scope, plan §Coverage). The catalog has 15
    /// operating modes; we implement 13. The gap is C4FSK — coherent 4-level FSK, modes
    /// 1 (19200) and 3 (9600, 10 kHz OBW) — a genuinely new modem, not a
    /// reparameterisation of anything here.
    /// </summary>
    [Theory]
    [InlineData(1, "19200 C4FSK IL2P+CRC")]
    [InlineData(3, "9600 C4FSK IL2P+CRC")]
    public void Every_NinoTnc_Mode_Has_A_Modem(byte ninoMode, string name)
    {
        TryCreate(ninoMode).Should().BeTrue(
            "NinoTNC mode {0} ({1}) should have a wire-compatible modem here", ninoMode, name);
    }

    private static bool TryCreate(byte ninoMode)
    {
        // Wire this up as modems land; the switch is the to-do list.
        return ninoMode switch
        {
            1 or 3 => false,   // C4FSK: no modem yet
            _ => true,
        };
    }
}
