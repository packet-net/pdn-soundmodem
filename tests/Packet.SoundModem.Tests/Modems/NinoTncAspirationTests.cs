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
    /// This class's own scoreboard is EMPTY; the live aspiration corpus now lives in
    /// <see cref="NinoTncMissCorpusAspirationTests"/> — real off-air BPSK300 frames from the
    /// 2026-07-18/19 GB7RDG 40 m benchmark that we do not yet copy. Graduated so far: the
    /// idle-noise qpsk2400 acquisition criterion (2026-07-16, passed on first run) and the C4FSK
    /// coverage criterion for NinoTNC modes 1/3 (2026-07-16, when <c>C4fskModem</c> landed —
    /// bench-proven 8/8 us→NinoTNC on both modes at first live attempt). This placeholder keeps
    /// the category discoverable; add the next unmet single-criterion aspiration here.
    /// </summary>
    [Fact]
    public void Scoreboard_Is_Empty()
    {
    }
}
