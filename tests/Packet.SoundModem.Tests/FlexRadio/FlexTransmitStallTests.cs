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
/// <para>Real time, about a second each, because the sink paces off the wall clock. The
/// assertions leave room for a loaded machine's own late wake-ups.</para>
/// <para>Each test sets the lead variable itself, so a value in the environment the tests run in
/// changes nothing, and runs on its own, because the environment is the whole process's.</para>
/// </remarks>
[Collection(nameof(FlexTransmitStallTests))]
public sealed class FlexTransmitStallTests
{
    [Fact]
    public async Task A_Hold_The_Lead_Covers_Is_Not_Reported()
    {
        await using FlexRuntime runtime = await OpenAsync(lead: "80");
        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;
        HoldAt(runtime, packet: 60, TimeSpan.FromMilliseconds(30));

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        runtime.TransmitLead.Should().Be(TimeSpan.FromMilliseconds(80));
        reports.Should().BeEmpty("the radio still had most of the 80 ms lead in hand");
    }

    [Fact]
    public async Task A_Sender_Held_Up_For_Longer_Than_The_Lead_Is_Reported_With_The_Keyup()
    {
        await using FlexRuntime runtime = await OpenAsync(lead: "80");
        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;
        HoldAt(runtime, packet: 60, TimeSpan.FromMilliseconds(150));

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        // 150 ms held, 80 of it covered: the radio went about 70 ms without audio.
        TransmitStallReport report = reports.Should().ContainSingle().Subject;
        report.Stalls.Should().BeGreaterThanOrEqualTo(1);
        report.Worst.Should().BeGreaterThan(TimeSpan.FromMilliseconds(55));
        runtime.TransmitStalls.Snapshot.Stalls.Should().Be(report.Stalls);
    }

    [Fact]
    public async Task With_The_Lead_Turned_Off_A_Short_Hold_Is_A_Gap_Again()
    {
        // The rollback: one packet at a time, as up to M0LTE.Flex 0.15.1, where a 50 ms hold was
        // a 50 ms hole on the air.
        await using FlexRuntime runtime = await OpenAsync(lead: "off");
        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;
        HoldAt(runtime, packet: 60, TimeSpan.FromMilliseconds(50));

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        runtime.TransmitLead.Should().Be(TimeSpan.Zero);
        reports.Should().ContainSingle().Which.Worst.Should().BeGreaterThan(TimeSpan.FromMilliseconds(40));
    }

    private static async Task<FlexRuntime> OpenAsync(string lead)
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
                tuning: new FlexTuning(), cancellation: CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(M0LTE.Flex.FlexAudioOutput.TransmitLeadVariable, before);
        }
    }

    private static void HoldAt(FlexRuntime runtime, int packet, TimeSpan hold)
    {
        int sent = 0;
        Action<byte[]> deliver = runtime.Station.Client.VitaSendHook!;
        runtime.Station.Client.VitaSendHook = bytes =>
        {
            if (sent++ == packet)
            {
                Thread.Sleep(hold);
            }

            deliver(bytes);
        };
    }
}

/// <summary>Runs <see cref="FlexTransmitStallTests"/> on their own: they set the process
/// environment.</summary>
[CollectionDefinition(nameof(FlexTransmitStallTests), DisableParallelization = true)]
public sealed class FlexTransmitStallTestsCollection;
