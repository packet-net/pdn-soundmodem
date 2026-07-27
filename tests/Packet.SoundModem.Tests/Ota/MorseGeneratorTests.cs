using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// Morse station identification: PARIS timing, the exact keying of the callsign we will
/// actually send, and a click-free envelope.
/// </summary>
/// <remarks>
/// The spectral assertions matter as much as the timing. Hard-keying a carrier produces
/// sidebands that decay at only 6 dB/octave — key clicks — and we are about to publish
/// spectral measurements of our own transmitter, so the ID must not be the dirtiest thing
/// we send.
/// </remarks>
public class MorseGeneratorTests
{
    private const int Rate = 24000;

    [Fact]
    public void Dot_length_follows_the_paris_standard()
    {
        // 50 dot units per PARIS word → 1.2/wpm seconds per dot. At 30 wpm, 40 ms.
        MorseGenerator.DotSeconds(30).Should().BeApproximately(0.040, 1e-9);
        MorseGenerator.DotSeconds(20).Should().BeApproximately(0.060, 1e-9);
        MorseGenerator.DotSeconds(12).Should().BeApproximately(0.100, 1e-9);
    }

    [Fact]
    public void Paris_takes_exactly_one_minute_at_one_word_per_minute()
    {
        // The definition of the standard, and the sharpest available check that the gap
        // arithmetic is right: PARIS is 50 units including the trailing word space, and
        // KeyingPattern emits 43 of them (it stops at the final key-up).
        double duration = MorseGenerator.DurationSeconds("PARIS", 1);

        duration.Should().BeApproximately(43 * 1.2, 1e-6);
    }

    [Fact]
    public void The_callsign_keys_exactly_as_morse_specifies()
    {
        // M0LTE, symbol by symbol: M(--) 0(-----) L(.-..) T(-) E(.)
        (string, string)[] expected =
        [
            ('M'.ToString(), "--"),
            ('0'.ToString(), "-----"),
            ('L'.ToString(), ".-.."),
            ('T'.ToString(), "-"),
            ('E'.ToString(), "."),
        ];

        foreach ((string c, string symbols) in expected)
        {
            MorseGenerator.Symbols(c[0]).Should().Be(symbols);
        }
    }

    [Fact]
    public void The_keying_pattern_alternates_and_starts_and_ends_key_down()
    {
        IReadOnlyList<(bool KeyDown, double Seconds)> pattern = MorseGenerator.KeyingPattern("M0LTE", 30);

        pattern.Should().NotBeEmpty();
        pattern[0].KeyDown.Should().BeTrue("a message begins with a mark, never a gap");
        pattern[^1].KeyDown.Should().BeTrue("trailing silence is the caller's business, not the pattern's");
        for (int k = 1; k < pattern.Count; k++)
        {
            pattern[k].KeyDown.Should().Be(!pattern[k - 1].KeyDown, "intervals must alternate");
            pattern[k].Seconds.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Element_and_gap_lengths_are_one_or_three_dots()
    {
        double dot = MorseGenerator.DotSeconds(30);

        foreach ((bool keyDown, double seconds) in MorseGenerator.KeyingPattern("M0LTE MS110D", 30))
        {
            double units = seconds / dot;
            double nearest = Math.Round(units);
            units.Should().BeApproximately(nearest, 1e-9, "element lengths are whole dot units");
            if (keyDown)
            {
                nearest.Should().BeOneOf(1.0, 3.0);
            }
            else
            {
                // 1 = intra-character, 3 = between characters, 7 = between words.
                nearest.Should().BeOneOf(1.0, 3.0, 7.0);
            }
        }
    }

    [Fact]
    public void A_full_session_id_is_short_enough_to_be_worth_sending_often()
    {
        // "M0LTE MS110D" at 30 wpm — the airtime cost of identifying every ten minutes.
        double seconds = MorseGenerator.DurationSeconds(MorseGenerator.IdText("M0LTE", "MS110D"), 30);

        seconds.Should().BeInRange(2.0, 6.0);
    }

    [Fact]
    public void The_rendered_carrier_sits_where_asked_and_is_keyed()
    {
        float[] iq = MorseGenerator.Complex("M0LTE", toneHz: 1000, amplitude: 0.8,
            wordsPerMinute: 30, sampleRate: Rate);

        int n = iq.Length / 2;
        var i = new float[n];
        var q = new float[n];
        for (int k = 0; k < n; k++)
        {
            i[k] = iq[2 * k];
            q[k] = iq[(2 * k) + 1];
        }

        IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate, 4096);
        (double hz, double _) = spectrum.FindPeak(800, 1200);
        hz.Should().BeApproximately(1000.0, 5.0);

        // Actually keyed: some samples at full amplitude, some at silence.
        double peak = ToneGenerator.PeakMagnitude(iq);
        peak.Should().BeApproximately(0.8, 0.01);
        Array.Exists(i, v => Math.Abs(v) < 1e-6).Should().BeTrue("there must be key-up intervals");
    }

    [Fact]
    public void Keying_edges_are_shaped_so_the_identification_does_not_click()
    {
        // The point of the raised-cosine edge. Compare the shaped envelope against a
        // hard-keyed one: sidebands well away from the carrier must be markedly lower.
        float[] shaped = MorseGenerator.Complex("TTT", 1000, 0.8, 30, Rate, edgeSeconds: 0.005);
        float[] hard = MorseGenerator.Complex("TTT", 1000, 0.8, 30, Rate, edgeSeconds: 1e-6);

        double Sideband(float[] iq)
        {
            int n = iq.Length / 2;
            var i = new float[n];
            var q = new float[n];
            for (int k = 0; k < n; k++)
            {
                i[k] = iq[2 * k];
                q[k] = iq[(2 * k) + 1];
            }

            IqSpectrum s = IqAnalysis.Welch(i, q, Rate, 1024);
            // Energy 1.5–4 kHz away from the carrier — where clicks live.
            double worst = 0;
            for (double f = 2500; f <= 5000; f += 100)
            {
                worst = Math.Max(worst, s.TonePower(f));
            }

            return IqAnalysis.Db(worst) - IqAnalysis.Db(s.TonePower(1000));
        }

        double shapedDbc = Sideband(shaped);
        double hardDbc = Sideband(hard);

        shapedDbc.Should().BeLessThan(hardDbc - 15,
            "shaping must buy at least 15 dB of click suppression to be worth doing");
        shapedDbc.Should().BeLessThan(-55);
    }

    [Fact]
    public void An_edge_that_would_swallow_a_dot_is_refused()
    {
        Action tooSlow = () => MorseGenerator.Complex("T", 1000, 0.8, 30, Rate, edgeSeconds: 0.025);

        tooSlow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Unsendable_text_is_refused_rather_than_silently_dropped()
    {
        MorseGenerator.CanEncode("M0LTE MS110D").Should().BeTrue();
        MorseGenerator.CanEncode("M0LTE #1").Should().BeFalse();

        Action bad = () => MorseGenerator.Complex("M0LTE #1", 1000, 0.8, 30, Rate);
        bad.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_id_text_is_the_callsign_then_the_mode()
    {
        MorseGenerator.IdText("m0lte", "ms110d").Should().Be("M0LTE MS110D");
        MorseGenerator.IdText("M0LTE", null).Should().Be("M0LTE");
    }
}
