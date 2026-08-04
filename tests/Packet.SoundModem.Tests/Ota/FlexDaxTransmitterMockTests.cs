using M0LTE.Flex;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The DAX-audio transmit path end to end against <see cref="MockFlexRadio"/> - headless DIGU
/// bring-up, DAX-as-transmit-source selection, transmit-filter open, key, push audio, drain,
/// unkey - with no radio. The deployment-route counterpart of <see cref="FlexIqTransmitterMockTests"/>.
/// </summary>
/// <remarks>
/// <para>Unlike the reflection-driven waveform path, DAX is a <b>push</b> sink: the transmitter
/// writes audio and the sink meters it out at the DAX sample rate, so there is no ring, no starve,
/// and the failure modes worth an offline gate are the bring-up ones - a transmitter left pointed
/// at the mic (which keys and sends silence) and a transmit filter left truncating the top of the
/// MS110D band.</para>
/// <para>The mock models the headless DAX path, the <c>transmit set dax=1</c> selection and its
/// read-back, the transmit filter, the meter surface and byte-exact DAX-TX capture. It does NOT
/// model a transmitter that refuses the DAX selection, so the "bring-up throws on
/// <c>TransmitSourceWarning</c>" path is not exercisable here - the mock always reports
/// <c>dax=1</c> once told. That throw is covered by construction (it mirrors the main repo's
/// <c>FlexDevice.OpenAsync</c>) and by the positive read-back asserted below.</para>
/// </remarks>
public sealed class FlexDaxTransmitterMockTests
{
    private const int Rate = FlexDaxTransmitter.SampleRate;

    private static FlexTransmitterOptions Options() => new()
    {
        Radio = "mock",
        FrequencyMHz = "18.098000",
        Antenna = "ANT1",
        RfPower = 1,
        // The mock reports no radio callsign, so identification needs an explicit one; a fast
        // Morse speed and short lead-in/out keep the real-time-paced push short in the test.
        Callsign = "M0LTE",
        IdMode = "MS110D",
        IdWpm = 60,
        DaxTransmitFilterHighHz = 3450,
        LeadInSeconds = 0.05,
        LeadOutSeconds = 0.03,
        // Don't wait the 2 s production inter-burst settle between paced test bursts.
        InterBurstSettle = TimeSpan.FromMilliseconds(1),
        // Left at the default (true): the mock serves `meter list`, so the interlock's
        // subscription path is exercised offline rather than switched off.
    };

    private static async Task<(MockFlexRadio Mock, FlexClient Client, FlexDaxTransmitter Tx)> OpenAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.ReducedBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexDaxTransmitter tx = await FlexDaxTransmitter.AttachAsync(client, Options());

        // Deterministic in-process transport, mirroring FlexModemLoopTests / the IQ tests: the
        // DAX-TX packets are delivered straight to the mock's capture rather than self-looping
        // over UDP, so the capture is lossless on a loaded box.
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;
        return (mock, client, tx);
    }

    [Fact]
    public async Task Bring_up_points_the_transmitter_at_dax()
    {
        (MockFlexRadio mock, FlexClient client, FlexDaxTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            // The step whose absence produced a fully successful-looking transmission with no
            // modulation on it (Flex 0.7.0): the transmitter must be pointed at DAX, and the radio
            // must agree - the transmitter surfaces the read-back.
            mock.CommandLog.Should().Contain("transmit set dax=1");
            mock.TransmitSourceIsDax.Should().BeTrue();
            tx.TransmitSourceIsDax.Should().BeTrue("the transmitter surfaces the radio's read-back");
        }
    }

    [Fact]
    public async Task The_transmit_filter_is_opened_to_the_configured_high_cut()
    {
        (MockFlexRadio mock, FlexClient client, FlexDaxTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            // The GLOBAL transmit filter - 3 kHz from the factory - is what caps transmitted audio
            // bandwidth, so the DAX route always states it to clear the MS110D band (skirts to
            // ≈3450 Hz on the 1800 Hz sub-carrier) rather than inherit whatever a previous session left.
            mock.CommandLog.Should().Contain(c => c.Contains("transmit set filter_high=3450"));
            mock.TransmitFilter.High.Should().Be(3450);
            tx.TransmitFilter.Should().NotBeNull();
            tx.TransmitFilter!.Value.High.Should().Be(3450);
        }
    }

    [Fact]
    public async Task A_tone_reaches_the_radio_intact_as_real_audio()
    {
        (MockFlexRadio mock, FlexClient client, FlexDaxTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            float[] tone = ToneGenerator.Real([1500.0], amplitudePerTone: 0.5, seconds: 0.3, Rate);

            TransmitReport report = await tx.TransmitAsync(tone);

            report.Aborted.Should().BeFalse();
            report.SamplesStarved.Should().Be(0, "nothing pulls on the push route");
            report.PacketsReflected.Should().BeGreaterThan(0, "DAX packets were pushed");

            float[] captured = [.. mock.CapturedTxSamples];
            captured.Should().NotBeEmpty();
            // 0.05 s lead-in + 0.3 s tone + 0.03 s lead-out of mono audio, rounded up to a whole
            // DAX packet by the drain.
            captured.Length.Should().BeGreaterThanOrEqualTo((int)Math.Round(0.35 * Rate));

            // What the radio was handed is a 1500 Hz real tone: its energy at 1500 Hz must
            // dominate an off-tone bin (DIGU places it at dial + 1500, but that is the radio's job,
            // not ours - here we only assert the audio itself is intact).
            double onTone = Goertzel(captured, 1500.0, Rate);
            double offTone = Goertzel(captured, 600.0, Rate);
            onTone.Should().BeGreaterThan(offTone * 10.0, "the transmitted audio is a 1500 Hz tone");
        }
    }

    [Fact]
    public async Task Identification_runs_and_is_transmitted_as_audio()
    {
        (MockFlexRadio mock, FlexClient client, FlexDaxTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            tx.IdentificationDue.Should().BeTrue("nothing has identified yet this session");

            await tx.EnsureIdentifiedAsync();

            tx.IdentificationDue.Should().BeFalse("the station has just identified");
            tx.LastIdentifiedUtc.Should().NotBeNull();
            mock.CapturedTxSamples.Should().NotBeEmpty("the Morse identification went out as audio");
        }
    }

    [Fact]
    public async Task A_burst_that_would_clip_is_refused_before_it_reaches_the_radio()
    {
        (MockFlexRadio mock, FlexClient client, FlexDaxTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            // Two tones at 0.7 each sum to a peak near 1.4 - over the DAX s16 full scale.
            float[] hot = ToneGenerator.Real([1500.0, 2000.0], 0.7, 0.1, Rate);

            Func<Task> transmit = () => tx.TransmitAsync(hot);

            await transmit.Should().ThrowAsync<ArgumentException>().WithMessage("*peak*");
        }
    }

    [Fact]
    public async Task A_burst_longer_than_the_ceiling_is_refused()
    {
        (MockFlexRadio mock, FlexClient client, FlexDaxTransmitter tx) = await OpenAsync();
        await using (mock)
        await using (client)
        await using (tx)
        {
            float[] tooLong = new float[61 * Rate]; // 61 s of mono audio

            Func<Task> transmit = () => tx.TransmitAsync(tooLong);

            await transmit.Should().ThrowAsync<ArgumentException>().WithMessage("*ceiling*");
        }
    }

    [Fact]
    public async Task Rf_power_outside_the_legal_range_is_refused_at_bring_up()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.ReducedBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                Func<Task> open = () => FlexDaxTransmitter.AttachAsync(
                    client, Options() with { RfPower = 101 });

                await open.Should().ThrowAsync<ArgumentOutOfRangeException>();
            }
        }
    }

    /// <summary>Single-frequency energy of a real signal (a Goertzel), for asserting a captured
    /// tone is where it should be without a full FFT.</summary>
    private static double Goertzel(ReadOnlySpan<float> x, double freqHz, int rate)
    {
        double c = 2.0 * Math.Cos(2.0 * Math.PI * freqHz / rate);
        double s1 = 0, s2 = 0;
        for (int n = 0; n < x.Length; n++)
        {
            double s0 = x[n] + (c * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        return (s1 * s1) + (s2 * s2) - (c * s1 * s2);
    }
}
