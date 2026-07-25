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

    private static FlexIqTransmitterOptions Options() => new()
    {
        Radio = "mock",
        FrequencyMHz = "18.098000",
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

            IqSpectrum spectrum = IqAnalysis.Welch(i, q, Rate, fftSize: 4096);
            (double hz, double power) = spectrum.FindPeak(1800, 2200);

            hz.Should().BeApproximately(2000.0, 2.0);
            // Amplitude 0.5 → mean-square 0.25 while the tone is on. The captured buffer also
            // holds the lead-in/lead-out silence and the cosine ramps, so the averaged level
            // sits a little below that; the tolerance covers the duty cycle, not sloppiness.
            IqAnalysis.Db(power).Should().BeInRange(IqAnalysis.Db(0.25) - 3.0, IqAnalysis.Db(0.25) + 0.5);
            (IqAnalysis.Db(spectrum.TonePower(-hz)) - IqAnalysis.Db(power)).Should().BeLessThan(-60);
        }
    }

    [Fact]
    public async Task The_waveform_is_brought_up_in_raw_mode_on_the_requested_frequency()
    {
        (MockFlexRadio mock, FlexClient client, FlexIqTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            IReadOnlyList<string> log = mock.CommandLog;

            // RAW is the only underlying mode that carries true wideband complex IQ to RF
            // (docs/flex-integration.md §9.5) — USB and IQ are SSB-limited, so a regression
            // here would silently halve the transmitted spectrum.
            log.Should().Contain(c => c.Contains("waveform create") && c.Contains("underlying_mode=RAW"));
            // The band-persistence fix: `slice create freq=` alone is ignored by a real 6500.
            log.Should().Contain(c => c.StartsWith("slice t ", StringComparison.Ordinal)
                && c.Contains("18.098000"));
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
