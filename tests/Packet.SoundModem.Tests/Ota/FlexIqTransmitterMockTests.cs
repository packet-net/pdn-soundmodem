using M0LTE.Flex;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The transmit path end to end against <see cref="MockFlexRadio"/> — waveform bring-up, key,
/// reflect, drain, unkey — with no radio.
/// </summary>
/// <remarks>
/// <para>This is the gate that has to be green before the tone goes on the air
/// (ota-execution-plan §E0.5). A waveform is <em>reflection-driven</em>: the radio streams TX
/// buffers while keyed and expects exactly one back per buffer, so the failure mode that
/// matters is a starve — the ring not having samples when the radio asks. Offline that shows
/// up as a non-zero <c>SamplesStarved</c>; on the air it is a phase discontinuity and
/// therefore spectral splatter around what should be a pure carrier.</para>
/// <para>The assertion is not merely "it ran": the samples the mock captured are spectrally
/// analysed and must contain the tone we asked for, at the right frequency and level, with no
/// image — i.e. what the radio was handed is what we intended to transmit.</para>
/// </remarks>
public sealed class FlexIqTransmitterMockTests
{
    private const int Rate = FlexIqTransmitter.SampleRate;

    /// <summary>The declared band width — the samples' 0..obw span is what reaches the air.</summary>
    private const int Obw = 6000;

    private static FlexTransmitterOptions Options() => new()
    {
        Radio = "mock",
        FrequencyMHz = "18.098000",
        OccupiedBandwidthHz = Obw,
        Antenna = "ANT1",
        RfPower = 1,
        // Left at the default (true): the mock now serves `meter list`, so the interlock's
        // subscription path is exercised offline rather than switched off.
        LeadInSeconds = 0.1,
        LeadOutSeconds = 0.05,
    };

    private static async Task<(MockFlexRadio Mock, FlexClient Client, FlexIqTransmitter Tx)> OpenAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexIqTransmitter tx = await FlexIqTransmitter.AttachAsync(client, Options());

        // Deterministic in-process transport for the reflection loop, mirroring
        // FlexModemLoopTests: a dropped loopback datagram would look exactly like a starve,
        // and the point under test is the reflection logic, not kernel UDP buffering.
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;
        return (mock, client, tx);
    }

    [Fact]
    public async Task A_tone_reaches_the_radio_intact_with_no_starved_samples()
    {
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            float[] tone = ToneGenerator.Complex([2000.0], amplitudePerTone: 0.5, seconds: 0.5, Rate);

            TransmitReport report = await tx.TransmitAsync(tone);

            report.Aborted.Should().BeFalse();
            report.SamplesStarved.Should().Be(0, "a starve is a phase discontinuity on the air");
            report.PacketsReflected.Should().BeGreaterThan(0);

            // 0.1 s lead-in + 0.5 s tone + 0.05 s lead-out, then padded up to whole waveform
            // buffers so the radio is never handed a partial one.
            report.Samples.Should().BeGreaterThanOrEqualTo((int)Math.Round(0.65 * Rate));
            (report.Samples % FlexIqTransmitter.PacketSamples).Should().Be(0);
            report.Samples.Should().BeLessThan((int)Math.Round(0.65 * Rate) + (2 * FlexIqTransmitter.PacketSamples));
        }
    }

    [Fact]
    public async Task What_the_radio_was_handed_is_the_tone_we_asked_for()
    {
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            await tx.TransmitAsync(ToneGenerator.Complex([2000.0], 0.5, 0.5, Rate));

            float[] captured = [.. mock.CapturedWaveformIq];
            captured.Should().NotBeEmpty();

            int n = captured.Length / 2;
            var i = new float[n];
            var q = new float[n];
            for (int k = 0; k < n; k++)
            {
                i[k] = captured[2 * k];
                q[k] = captured[(2 * k) + 1];
            }

            // What the radio is handed is the PLACED baseband: only the negative half of a
            // waveform's baseband is transmitted, so the library shifts the declared 0..obw
            // span down by obw. The +2000 Hz tone must therefore arrive at 2000 − obw =
            // −4000 Hz, which the derived slice (top edge of the band) carries back to
            // --freq + 2000 on air. A tone still at +2000 here would be in the half that
            // never transmits.
            IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate, fftSize: 4096);
            (double hz, double power) = spectrum.FindPeak(2000 - Obw - 200, 2000 - Obw + 200);

            hz.Should().BeApproximately(2000.0 - Obw, 2.0);
            // Amplitude 0.5 → mean-square 0.25 while the tone is on. The captured buffer also holds
            // the lead-in/lead-out silence and the cosine ramps, and the reflection loop drains a
            // load-dependent amount of trailing silence, so the Welch-averaged tone power sits below
            // 0.25 by a duty cycle that varies with how much silence was captured (observed to reach
            // ~−9.3 dB under a loaded parallel suite). The sharp gates own correctness: A_tone asserts
            // SamplesStarved == 0 (no phase discontinuity), the −60 dBc check below owns image
            // rejection, and the ±2 Hz check above owns placement. This is only a coarse "the tone is
            // present at roughly the right level" band, so its lower edge is generous enough to
            // survive the capture-duty-cycle variance. The placement shift is a pure frequency
            // translation, so the level is unchanged.
            IqAnalysis.Db(power).Should().BeInRange(IqAnalysis.Db(0.25) - 6.0, IqAnalysis.Db(0.25) + 0.5);
            (IqAnalysis.Db(spectrum.TonePower(-hz)) - IqAnalysis.Db(power)).Should().BeLessThan(-60);
        }
    }

    [Fact]
    public async Task The_waveform_is_brought_up_in_raw_mode_on_the_placed_band()
    {
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            IReadOnlyList<string> log = mock.CommandLog;

            // RAW is the one underlying mode band placement uses: every mode is single-sideband
            // (only the negative half of the baseband transmits), and the alternatives either
            // mirror the spectrum (IQ/USB/DIGU) or route through a full audio mode whose
            // processing is uncharacterised (LSB/DIGL).
            log.Should().Contain(c => c.Contains("waveform create") && c.Contains("underlying_mode=RAW"));

            // The slice is DERIVED, not the requested frequency: with the band's lower edge at
            // 18.098000 and a 6000 Hz width, the slice belongs at the band's top edge —
            // 18.104000 — because RF = slice + (negative) baseband. The band-persistence fix
            // (`slice create freq=` alone is ignored by a real 6500) must land there too.
            log.Should().Contain(c => c.StartsWith("slice t ", StringComparison.Ordinal)
                && c.Contains("18.104000"));

            // And the GLOBAL transmit filter — the setting that actually caps occupied
            // bandwidth, 3 kHz from the factory — must have been opened to the declared width.
            log.Should().Contain(c => c.Contains("transmit set filter_high=6000"));
            mock.TransmitFilter.High.Should().Be(Obw);
        }
    }

    [Fact]
    public async Task A_burst_that_would_clip_is_refused_before_it_reaches_the_radio()
    {
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            // Two tones at 0.7 each peak at 1.4 — over full scale.
            float[] hot = ToneGenerator.Complex([1500.0, 2500.0], 0.7, 0.2, Rate);

            Func<Task> transmit = () => tx.TransmitAsync(hot);

            await transmit.Should().ThrowAsync<ArgumentException>().WithMessage("*peak*");
        }
    }

    [Fact]
    public async Task A_burst_longer_than_the_ceiling_is_refused()
    {
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            float[] tooLong = new float[2 * 61 * Rate];

            Func<Task> transmit = () => tx.TransmitAsync(tooLong);

            await transmit.Should().ThrowAsync<ArgumentException>().WithMessage("*ceiling*");
        }
    }

    [Fact]
    public async Task Rf_power_outside_the_legal_range_is_refused_at_bring_up()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                Func<Task> open = () => FlexIqTransmitter.AttachAsync(
                    client, Options() with { RfPower = 101 });

                await open.Should().ThrowAsync<ArgumentOutOfRangeException>();
            }
        }
    }
}
