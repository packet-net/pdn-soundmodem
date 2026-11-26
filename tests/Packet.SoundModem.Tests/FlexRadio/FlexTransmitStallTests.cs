using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// The DAX transmit path's stall, reproduced offline through the real <c>FlexAudioOutput</c>
/// against <c>flex:mock</c> (issue #748).
/// </summary>
/// <remarks>
/// <para>The sending thread is held up once, inside the packet send. Up to M0LTE.Flex 0.15.1 the
/// sink kept about one packet at the radio, so a 50 ms hold was a 50 ms hole on the air. It now
/// keeps a lead (80 ms unless <c>M0LTE_FLEX_TX_LEAD_MS</c> says otherwise), so a hold the lead
/// covers is no gap and the watch says nothing, and a longer one is a gap the size of what the
/// lead did not cover.</para>
/// <para>The stall watch reads only the clock it is given, a fake one here (issue #804). The
/// radio's playout is modelled on that clock in the send hook: after each packet it has played
/// everything but the lead, and a hold is the test advancing the clock by that much inside the
/// send. The real <c>FlexAudioOutput</c> still sleeps in real time to pace itself, which this
/// project cannot inject; nothing asserted here depends on how long that takes, so a loaded
/// machine only makes the test slower.</para>
/// <para>Each test sets the lead variable itself, so a value in the environment the tests run in
/// changes nothing, and runs on its own, because the environment is the whole process's.</para>
/// </remarks>
[Collection(nameof(FlexTransmitStallTests))]
public sealed class FlexTransmitStallTests
{
    [Fact]
    public async Task A_Hold_The_Lead_Covers_Is_Not_Reported()
    {
        var clock = new FakeTimeProvider();
        await using FlexRuntime runtime = await OpenAsync(lead: "80", clock);
        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;
        HoldAt(runtime, clock, packet: 60, TimeSpan.FromMilliseconds(30));

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        runtime.TransmitLead.Should().Be(TimeSpan.FromMilliseconds(80));
        reports.Should().BeEmpty("the radio still had most of the 80 ms lead in hand");
    }

    [Fact]
    public async Task A_Sender_Held_Up_For_Longer_Than_The_Lead_Is_Reported_With_The_Keyup()
    {
        var clock = new FakeTimeProvider();
        await using FlexRuntime runtime = await OpenAsync(lead: "80", clock);
        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;
        HoldAt(runtime, clock, packet: 60, TimeSpan.FromMilliseconds(150));

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        // 150 ms held, 80 of it covered: the radio went 70 ms without audio, to within a packet.
        TransmitStallReport report = reports.Should().ContainSingle().Subject;
        report.Stalls.Should().Be(1);
        report.Worst.Should().BeCloseTo(TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(6));
        runtime.TransmitStalls.Snapshot.Stalls.Should().Be(report.Stalls);
    }

    [Fact]
    public async Task With_The_Lead_Turned_Off_A_Short_Hold_Is_A_Gap_Again()
    {
        // The rollback: one packet at a time, as up to M0LTE.Flex 0.15.1, where a 50 ms hold was
        // a 50 ms hole on the air.
        var clock = new FakeTimeProvider();
        await using FlexRuntime runtime = await OpenAsync(lead: "off", clock);
        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;
        HoldAt(runtime, clock, packet: 60, TimeSpan.FromMilliseconds(50));

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        runtime.TransmitLead.Should().Be(TimeSpan.Zero);
        reports.Should().ContainSingle().Which.Worst.Should().BeCloseTo(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(6));
    }

    private static async Task<FlexRuntime> OpenAsync(string lead, TimeProvider clock)
    {
        // Read once, when the sink is made, so it only has to be set for the open.
        string? before = Environment.GetEnvironmentVariable(M0LTE.Flex.FlexAudioOutput.TransmitLeadVariable);
        Environment.SetEnvironmentVariable(M0LTE.Flex.FlexAudioOutput.TransmitLeadVariable, lead);
        try
        {
            // 12 kHz DSP: the reduced-bandwidth 24 kHz DAX stream, upsampled, as the MS110D
            // modes run.
            return await FlexDevice.OpenAsync(
                "flex:mock", dspRate: 12000, packetBuffer: 3,
                tuning: new FlexTuning(), cancellation: CancellationToken.None, time: clock);
        }
        finally
        {
            Environment.SetEnvironmentVariable(M0LTE.Flex.FlexAudioOutput.TransmitLeadVariable, before);
        }
    }

    /// <summary>Models the radio's playout on <paramref name="clock"/>: when a packet reaches the
    /// radio, the clock stands where the radio has played everything but the lead, and the packet
    /// numbered <paramref name="packet"/> is first held up for <paramref name="hold"/>.</summary>
    private static void HoldAt(FlexRuntime runtime, FakeTimeProvider clock, int packet, TimeSpan hold)
    {
        const int packetSamples = 128;
        TimeSpan packetLength = TimeSpan.FromTicks(
            packetSamples * TimeSpan.TicksPerSecond / runtime.TransmitStalls.SampleRate);
        DateTimeOffset start = clock.GetUtcNow();
        TimeSpan held = TimeSpan.Zero;
        int sent = 0;
        Action<byte[]> deliver = runtime.Station.Client.VitaSendHook!;
        runtime.Station.Client.VitaSendHook = bytes =>
        {
            if (sent == packet)
            {
                held += hold;
                clock.Advance(hold);
            }

            // The radio has played all this has sent but the lead, plus what the hold cost it.
            TimeSpan played = (packetLength * (sent + 1)) - runtime.TransmitLead + held;
            TimeSpan behind = played - (clock.GetUtcNow() - start);
            if (behind > TimeSpan.Zero)
            {
                clock.Advance(behind);
            }

            sent++;
            deliver(bytes);
        };
    }
}

/// <summary>Runs <see cref="FlexTransmitStallTests"/> on their own: they set the process
/// environment.</summary>
[CollectionDefinition(nameof(FlexTransmitStallTests), DisableParallelization = true)]
public sealed class FlexTransmitStallTestsCollection;
