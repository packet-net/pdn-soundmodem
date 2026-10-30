using Packet.SoundModem.Channel;
using Packet.SoundModem.FlexRadio;

namespace Packet.SoundModem.Tests.FlexRadio;

/// <summary>
/// The DAX transmit path's stall, reproduced offline through the real <c>FlexAudioOutput</c>
/// against <c>flex:mock</c> (issue #748).
/// </summary>
/// <remarks>
/// <para>The sending thread is held up once, 50 ms, inside the packet send. What reaches the radio
/// is then a 50 ms gap in the packets followed by the late ones sent back to back, which is how the
/// sink catches up. A radio that plays its DAX packets in order, with only a few milliseconds held
/// ahead, has nothing to send for the gap and then plays everything after it late by the gap: band
/// noise, then the rest of the burst shifted, which is what Sweden heard.</para>
/// <para>Real time, about a second, because the sink paces off the wall clock. The assertions
/// leave room for a loaded machine's own late wake-ups: at least the one injected stall, and at
/// least most of its size.</para>
/// </remarks>
public sealed class FlexTransmitStallTests
{
    [Fact]
    public async Task A_Sender_Held_Up_Mid_Burst_Is_Reported_With_The_Keyup()
    {
        // 12 kHz DSP: the reduced-bandwidth 24 kHz DAX stream, upsampled, as the MS110D modes run.
        await using FlexRuntime runtime = await FlexDevice.OpenAsync(
            "flex:mock", dspRate: 12000, packetBuffer: 3,
            tuning: new FlexTuning(), cancellation: CancellationToken.None);

        var reports = new List<TransmitStallReport>();
        runtime.TransmitStalls.KeyupStalled += reports.Add;

        var sentAt = new List<long>();
        Action<byte[]> deliver = runtime.Station.Client.VitaSendHook!;
        runtime.Station.Client.VitaSendHook = packet =>
        {
            if (sentAt.Count == 60)
            {
                Thread.Sleep(50);
            }

            sentAt.Add(TimeProvider.System.GetTimestamp());
            deliver(packet);
        };

        runtime.Output.Write(new float[12000]);
        runtime.Output.Drain();

        TransmitStallReport report = reports.Should().ContainSingle().Subject;
        report.Stalls.Should().BeGreaterThanOrEqualTo(1);
        report.Worst.Should().BeGreaterThan(TimeSpan.FromMilliseconds(40));
        runtime.TransmitStalls.Snapshot.Stalls.Should().Be(report.Stalls);

        // What the radio saw: a gap at the held packet, then the late packets in a rush.
        double gapMs = TimeProvider.System.GetElapsedTime(sentAt[59], sentAt[60]).TotalMilliseconds;
        gapMs.Should().BeGreaterThan(45);
        double rushMs = TimeProvider.System.GetElapsedTime(sentAt[60], sentAt[64]).TotalMilliseconds;
        rushMs.Should().BeLessThan(4 * 128 * 1000.0 / 24000, "the sink sends without sleeping until it has caught up");
    }
}
