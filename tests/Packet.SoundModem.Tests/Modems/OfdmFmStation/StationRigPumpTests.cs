using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>
/// The shared pump must never have two threads inside one channel's receive path. The bad interleaving is built, not hoped for: the pump thread is held inside the second rig's receive tap, while the
/// first rig has a keyup queued or running, until that keyup's own thread radiates into the same channel. If the pump feeds the second rig at such a time, the tap sees two threads at once.
/// </summary>
public class StationRigPumpTests
{
    private static void Reached(ManualResetEventSlim gate, string because)
    {
        if (!gate.Wait(Eventually.HangGuard))
        {
            throw new TimeoutException($"never happened: {because}");
        }
    }

    [Fact]
    public async Task The_pump_never_feeds_a_channel_while_the_other_rigs_keyup_radiates_into_it()
    {
        await using var a = new StationRig(seed: 11);
        await using var b = new StationRig(seed: 12);
        var timeline = new Lock();
        a.Air.Timeline = timeline;
        b.Air.Timeline = timeline;
        a.Host.SetIdentity("G4ABC").Should().Be(IdentityResult.Accepted);

        var radiating = new ThreadLocal<bool>();
        var keyupWriting = new ManualResetEventSlim();
        var overlapRecorded = new ManualResetEventSlim();
        int inside = 0;
        bool gating = true;
        bool overlap = false;
        a.Air.Radiated = chunk =>
        {
            radiating.Value = true;
            keyupWriting.Set();
            b.Channel.ProcessReceive(chunk);
            radiating.Value = false;
        };
        b.Air.Radiated = chunk => a.Channel.ProcessReceive(chunk);
        b.Channel.AddReceiveTap(_ =>
        {
            int now = Interlocked.Increment(ref inside);
            try
            {
                if (now > 1)
                {
                    // Two threads in this channel's receive path.
                    overlap = true;
                    gating = false;
                    overlapRecorded.Set();
                }
                else if (!radiating.Value && gating && (a.Channel.TransmitQueued || a.Air.Keyed))
                {
                    // The feeding thread, with the first rig's keyup queued or running: hold it here until that keyup radiates into this same channel.
                    Reached(keyupWriting, "the first rig's keyup to radiate");
                    Reached(overlapRecorded, "the keyup's thread to enter the channel while the feeding thread is in it");
                }
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        });
        a.StartTransmitter();
        b.StartTransmitter();

        a.Host.Send(new byte[40], LinkAddress.Everyone, LinkAddress.FromAscii("G4ABC-1"), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        await StationRig.PumpAsync(() => b.Received.Length >= 1, "the datagram to reach the other station", a, b);

        overlap.Should().BeFalse("the pump waits out every keyup before it feeds another channel");
        b.Received.Should().ContainSingle();
    }
}
