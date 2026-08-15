using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Ident;

namespace Packet.SoundModem.Tests.Ident;

/// <summary>
/// When a modem owes a Morse identification.
/// </summary>
/// <remarks>
/// <para>The rule has two halves and the interesting cases are where they disagree: time alone
/// must not make an ident due (a silent station announcing itself is QRM, not compliance), and
/// traffic alone must not either (that would key up after every frame). Both halves are asserted
/// against a moved clock rather than a real one, so a ten-minute rule costs no wall clock to
/// prove. House rule (CLAUDE.md): wall clock via TimeProvider only.</para>
/// <para>The companion is <see cref="MorseGeneratorTests"/>, which owns the rendering; this owns
/// the policy over it.</para>
/// </remarks>
public class StationIdentifierTests
{
    private const int Rate = 12000;

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

    private static StationIdentifier Identifier(
        FakeTimeProvider time, double intervalMinutes = 10, string? mode = null) =>
        new("M0LTE", mode, toneHz: 1500, wordsPerMinute: 20,
            interval: TimeSpan.FromMinutes(intervalMinutes), sampleRate: Rate, time: time);

    [Fact]
    public void A_station_that_has_not_transmitted_owes_no_identification()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time);

        id.IdentificationDue.Should().BeFalse(
            "identification is a condition on transmissions, so a modem that has sent nothing "
            + "owes nothing - keying up to announce a callsign nobody has heard transmit is QRM");

        time.Advance(TimeSpan.FromHours(3));

        id.IdentificationDue.Should().BeFalse("no amount of time turns silence into a debt");
    }

    [Fact]
    public void The_first_transmission_makes_an_identification_due_at_once()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time);

        id.NoteTransmission();

        id.IdentificationDue.Should().BeTrue(
            "the station has now transmitted and has never identified, so it is already owed");
    }

    [Fact]
    public void Identifying_clears_the_debt_until_the_modem_transmits_again()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time);
        id.NoteTransmission();

        id.NoteIdentified();

        id.IdentificationDue.Should().BeFalse("it has just identified");
        time.Advance(TimeSpan.FromHours(1));
        id.IdentificationDue.Should().BeFalse(
            "an hour of silence after an identification owes nothing - the interval gates a "
            + "further ident, it does not schedule one");
    }

    [Fact]
    public void A_further_identification_falls_due_only_after_the_interval()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time, intervalMinutes: 10);
        id.NoteTransmission();
        id.NoteIdentified();

        id.NoteTransmission();
        id.IdentificationDue.Should().BeFalse("only moments since the last identification");

        time.Advance(TimeSpan.FromMinutes(9));
        id.IdentificationDue.Should().BeFalse("still inside the ten-minute interval");

        time.Advance(TimeSpan.FromMinutes(1));
        id.IdentificationDue.Should().BeTrue(
            "ten minutes have passed and the modem has transmitted since it last identified");
    }

    [Fact]
    public void Traffic_during_the_interval_does_not_bring_the_identification_forward()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time, intervalMinutes: 10);
        id.NoteTransmission();
        id.NoteIdentified();

        for (int minute = 0; minute < 9; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            id.NoteTransmission();
            id.IdentificationDue.Should().BeFalse(
                $"a busy channel does not shorten the interval (minute {minute + 1})");
        }

        time.Advance(TimeSpan.FromMinutes(1));
        id.IdentificationDue.Should().BeTrue("the interval is up");
    }

    [Fact]
    public void The_clock_runs_from_the_identification_not_from_the_traffic()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time, intervalMinutes: 10);

        // Transmit, then leave the ident un-sent for a while - a busy channel deferring it, say.
        id.NoteTransmission();
        time.Advance(TimeSpan.FromMinutes(30));
        id.IdentificationDue.Should().BeTrue("still owed, and more so");

        id.NoteIdentified();
        id.NoteTransmission();

        time.Advance(TimeSpan.FromMinutes(9));
        id.IdentificationDue.Should().BeFalse(
            "the interval is measured from when we identified, not from the traffic that owed it");
    }

    [Fact]
    public void Last_identified_is_unset_until_the_station_has_identified()
    {
        FakeTimeProvider time = Clock();
        StationIdentifier id = Identifier(time);

        id.LastIdentifiedUtc.Should().BeNull();

        id.NoteTransmission();
        id.NoteIdentified();

        id.LastIdentifiedUtc.Should().Be(time.GetUtcNow());
    }

    [Fact]
    public void The_message_is_the_callsign_alone_unless_a_mode_is_given()
    {
        FakeTimeProvider time = Clock();

        Identifier(time).Text.Should().Be("M0LTE");
        Identifier(time, mode: "freedv-datac1").Text.Should().Be(
            "M0LTE FREEDV-DATAC1",
            "a listener who just heard something they could not read learns what it was");
    }

    [Fact]
    public void A_callsign_Morse_cannot_carry_is_refused()
    {
        FakeTimeProvider time = Clock();

        Action bad = () => new StationIdentifier(
            "M0LTE!", null, 1500, 20, TimeSpan.FromMinutes(10), Rate, time: time);

        bad.Should().Throw<ArgumentException>(
            "refusing at construction is how this reaches the operator at start-up rather than "
            + "as a silent failure to identify ten minutes into a transmission");
    }

    [Fact]
    public void An_ident_tone_above_Nyquist_is_refused_naming_both_numbers()
    {
        FakeTimeProvider time = Clock();

        Action tooHigh = () => new StationIdentifier(
            "M0LTE", null, toneHz: 7000, wordsPerMinute: 20,
            interval: TimeSpan.FromMinutes(10), sampleRate: Rate, time: time);

        tooHigh.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*6000*", "the operator needs to see the ceiling it broke");
    }

    [Fact]
    public void The_rendered_identification_is_a_keyed_tone_at_the_asked_for_frequency()
    {
        FakeTimeProvider time = Clock();
        var id = new StationIdentifier(
            "M0LTE", null, toneHz: 1500, wordsPerMinute: 20,
            interval: TimeSpan.FromMinutes(10), sampleRate: Rate, time: time);

        float[] audio = id.Render();

        audio.Length.Should().BeCloseTo((int)Math.Round(id.DurationSeconds * Rate), 2);

        // The tone is where it was asked for: correlate against 1500 Hz and against a decoy, and
        // the wanted bin must dominate. A modem's centre is where this lands on a real station,
        // so a rendering that quietly ignored toneHz would put the ident on the wrong frequency.
        static double Power(float[] x, double hz)
        {
            double re = 0, im = 0;
            for (int n = 0; n < x.Length; n++)
            {
                double phase = 2.0 * Math.PI * hz * n / Rate;
                re += x[n] * Math.Cos(phase);
                im += x[n] * Math.Sin(phase);
            }

            return (re * re) + (im * im);
        }

        Power(audio, 1500).Should().BeGreaterThan(
            Power(audio, 2500) * 100, "the ident is a 1500 Hz carrier, not a 2500 Hz one");
    }

    [Fact]
    public void Peak_amplitude_matches_the_modulators_so_the_drive_is_the_same()
    {
        FakeTimeProvider time = Clock();
        var id = new StationIdentifier(
            "M0LTE", null, 1500, 20, TimeSpan.FromMinutes(10), Rate, amplitude: 0.8, time: time);

        float peak = 0;
        foreach (float s in id.Render())
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        // Matching the modulators' own 0.8 is what keeps the ident from presenting the
        // transmitter with a different drive - and so a different ALC story - than the data.
        peak.Should().BeApproximately(0.8f, 0.01f);
    }
}
