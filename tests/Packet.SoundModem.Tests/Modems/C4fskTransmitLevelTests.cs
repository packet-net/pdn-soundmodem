using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// What the C4FSK modulator puts on the wire, in level terms. This is the one amplitude-coded
/// mode family in the tree (<see cref="FrameLevelLimits.ClipSensitive"/>), so both properties
/// here are ones a station can lose frames to without anything in the logs saying so.
/// </summary>
public class C4fskTransmitLevelTests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    private static C4fskModem Make(string mode) => mode == "c4fsk19200"
        ? C4fskModem.C4fsk19200(Rate, _ => { })
        : C4fskModem.C4fsk9600(Rate, _ => { });

    private static byte[] Frame(int bytes, int seed)
    {
        var frame = new byte[bytes];
        new Random(seed).NextBytes(frame);
        return frame;
    }

    /// <summary>
    /// The modulator stays inside full scale. <c>Pcm16.FromFloat</c> clamps at full scale
    /// <em>before</em> the ALSA mixer attenuates, so anything the modulator emits above 1.0 is
    /// clipped identically at every mixer setting, and winding the transmit level down does not
    /// remove it. Measured on the shipping tree before this test existed, at 48 kHz with a
    /// 200-byte frame and 250 ms of TXDELAY, c4fsk9600 peaked at 1.0562 with 1265 of 21608
    /// samples over full scale (5.9 %) and c4fsk19200 at 1.0261 with 26 of 16828 (0.2 %).
    /// Clipping compresses the outer levels into the inner ones and no envelope tracker
    /// downstream can undo it, which is exactly what this mode family cannot afford.
    /// </summary>
    [Theory]
    [InlineData("c4fsk9600")]
    [InlineData("c4fsk19200")]
    public void The_Modulator_Stays_Inside_Full_Scale(string mode)
    {
        float worst = 0;
        string worstCase = string.Empty;
        foreach (int bytes in new[] { 16, 60, 200, 500, 1000 })
        {
            foreach (int txDelay in new[] { 0, 50, 250, 300, 500 })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    float[] audio = Make(mode).Modulate(Frame(bytes, seed), txDelay);
                    float peak = 0;
                    int over = 0;
                    foreach (float sample in audio)
                    {
                        peak = Math.Max(peak, Math.Abs(sample));
                        over += Math.Abs(sample) > 1f ? 1 : 0;
                    }

                    if (peak > worst)
                    {
                        worst = peak;
                        worstCase = $"{bytes} bytes, TXDELAY {txDelay} ms, seed {seed}";
                    }

                    peak.Should().BeLessThanOrEqualTo(
                        1f,
                        $"{mode} at {bytes} bytes, TXDELAY {txDelay} ms, seed {seed} put {over} of "
                        + $"{audio.Length} samples past full scale (peak {peak:0.0000}); the pulse "
                        + "shaper overshoots the symbol amplitude and the headroom has to cover it");
                }
            }
        }

        output.WriteLine($"{mode}: worst peak {worst:0.0000} at {worstCase}");
    }

    /// <summary>
    /// The ratio between the two modes' outer levels, which is what issue #516 set out to create
    /// and what nothing guarded until now.
    /// </summary>
    /// <remarks>
    /// <para><b>It is 1:1, and that is not what #516 wanted.</b> The per-mode scaling it added
    /// computes <c>HeadroomFraction * PeakDeviationHz / FullDeviationHz(ChannelSpacingHz)</c>
    /// from each mode's own profile, and both C4FSK modes sit at exactly 100 % of the channel
    /// class they declare: c4fsk9600 is 2500 Hz on 12.5 kHz spacing, where full deviation is
    /// 2500, and c4fsk19200 is 5000 Hz on 25 kHz, where it is 5000. Normalising each mode
    /// against its own nominal channel cancels, both come out at the same figure, and the 1:2
    /// the change was written to produce is still 1:1. Issue #517 has the on-air evidence: on one
    /// 25 kHz channel the two modes measured 6.92 and 6.73 kHz peak deviation, the same
    /// deviation for two modes specified a factor of two apart.</para>
    /// <para><b>Why it is still 1:1 here.</b> Fixing it means choosing a single reference the
    /// station actually transmits against, and every candidate reaches past these two modes.
    /// C4fskModem is the only modem in the tree that scales its transmit amplitude by a profile
    /// at all; every other one emits at a fixed 0.8, so the tree is level-consistent today and
    /// c4fsk9600 sits where fsk9600 does. Referencing a fixed 5000 Hz (the widest class, so every
    /// mode is a fraction of one scale) is right on a 25 kHz channel and is the reference this
    /// file would recommend, but applied to C4FSK alone it drops c4fsk9600 6 dB below fsk9600,
    /// which wants 2.4 kHz of deviation against c4fsk9600's 2.5 kHz. That is a new inconsistency
    /// at least as bad as the one it removes, so the reference has to move for the whole tree at
    /// once, and every station re-levels when it does. Taking it from the station's configured
    /// channel spacing instead is the other honest answer and needs plumbing the modem does not
    /// have. Either is a decision with an on-air cost, not a tidy-up, so the ratio stays where it
    /// is and this test makes the next change to it deliberate rather than silent.</para>
    /// </remarks>
    [Fact]
    public void The_Two_Modes_Transmit_At_A_Pinned_Outer_Level_Ratio()
    {
        // The 0x77 preamble is an outer alternation in both modes, shaped by the same filter at
        // the same fraction of each mode's symbol rate, so a window well inside it carries the
        // outer level and nothing else. Its power is the cleanest read on the amplitude the
        // modulator chose.
        double narrow = PreamblePower("c4fsk9600");
        double wide = PreamblePower("c4fsk19200");
        double ratio = Math.Sqrt(wide / narrow);
        output.WriteLine(
            $"c4fsk19200 outer level / c4fsk9600 outer level = {ratio:0.0000} "
            + $"({20 * Math.Log10(ratio):+0.00;-0.00} dB)");

        ratio.Should().BeApproximately(
            1.0,
            0.01,
            "the two modes transmit at the same outer level; see this test's remarks for why that "
            + "is not what issue #516 intended and what changing it would cost");
    }

    /// <summary>Mean power of a window taken well inside the preamble.</summary>
    private static double PreamblePower(string mode)
    {
        float[] audio = Make(mode).Modulate(Frame(200, 1), 300);
        int from = Rate / 10;
        int to = Rate / 5;
        double total = 0;
        for (int i = from; i < to; i++)
        {
            total += audio[i] * (double)audio[i];
        }

        return total / (to - from);
    }
}
