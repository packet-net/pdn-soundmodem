using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The transmit lease: one sub-channel holds the transmitter, everyone else's transmissions are
/// refused (never queued), and a holder that stops renewing loses it on its own. Every clock here
/// is fake, so expiry is reached by advancing it rather than by waiting.
/// </summary>
public class TransmitLeaseTests
{
    private const int SampleRate = 12000;

    private static byte[] Frame(byte marker)
    {
        byte[] frame = new byte[24];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        frame.AsSpan(16).Fill(marker);
        return frame;
    }

    private static (SoundModemChannel Channel, FakeTimeProvider Time) Station()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var channel = new SoundModemChannel(SampleRate, time, randomSeed: 42);
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink));
        channel.AddModem(3, sink => new Afsk1200Modem(SampleRate, sink));
        channel.Csma.Persistence = 255;
        return (channel, time);
    }

    [Fact]
    public void A_Lease_Is_Taken_Renewed_Refused_To_Others_And_Released()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        TransmitLease lease = channel.TransmitLease;
        var changes = new List<TransmitLeaseEvent>();
        lease.Changed += changes.Add;

        lease.Holder.Should().BeNull("nobody holds the transmitter until asked");
        TransmitLeaseGrant taken = lease.Take(3, TimeSpan.FromSeconds(60));
        taken.Should().Be(new TransmitLeaseGrant(true, 3, time.GetUtcNow().AddSeconds(60), Renewed: false));

        time.Advance(TimeSpan.FromSeconds(30));
        TransmitLeaseGrant renewed = lease.Take(3, TimeSpan.FromSeconds(60));
        renewed.Renewed.Should().BeTrue();
        lease.Expires.Should().Be(time.GetUtcNow().AddSeconds(60));

        TransmitLeaseGrant other = lease.Take(0, TimeSpan.FromSeconds(60));
        other.Granted.Should().BeFalse();
        other.SubChannel.Should().Be(3, "the answer names who holds it");

        lease.Release().Should().BeTrue();
        lease.Holder.Should().BeNull();
        lease.Release().Should().BeFalse("there is nothing left to give back");

        changes.Select(c => c.Change).Should().Equal(
            TransmitLeaseChange.Taken, TransmitLeaseChange.Renewed, TransmitLeaseChange.Released);
        changes[^1].HeldFor.Should().Be(TimeSpan.FromSeconds(30));
        changes[^1].Renewals.Should().Be(1);
    }

    [Fact]
    public void A_Lease_Asked_For_Longer_Than_The_Cap_Gets_The_Cap()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();

        TransmitLeaseGrant grant = channel.TransmitLease.Take(3, TimeSpan.FromHours(1));

        grant.Expires.Should().Be(time.GetUtcNow() + TransmitLease.MaxDuration);
        TransmitLease.MaxDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_Holder_That_Stops_Renewing_Loses_The_Lease_When_It_Runs_Out()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var changes = new List<TransmitLeaseEvent>();
        channel.TransmitLease.Changed += changes.Add;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        time.Advance(TimeSpan.FromSeconds(59));
        channel.TransmitLease.Holder.Should().Be(3);
        changes.Should().ContainSingle("nothing has happened yet but the take");

        time.Advance(TimeSpan.FromSeconds(1));

        // Reported by the lease's own timer as the clock passes the instant, not by somebody
        // happening to ask afterwards: that is what puts the line in the journal on time.
        changes.Should().HaveCount(2);
        changes[1].Change.Should().Be(TransmitLeaseChange.Expired);
        changes[1].SubChannel.Should().Be(3);
        changes[1].HeldFor.Should().Be(TimeSpan.FromSeconds(60));
        channel.TransmitLease.Holder.Should().BeNull();
        channel.TransmitLease.Admits(0).Should().BeTrue();
    }

    [Fact]
    public void A_Renewal_Moves_The_Expiry_So_The_Old_One_Does_Not_Fire()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var changes = new List<TransmitLeaseEvent>();
        channel.TransmitLease.Changed += changes.Add;
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        for (int i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        }

        channel.TransmitLease.Holder.Should().Be(3);
        changes.Should().NotContain(c => c.Change == TransmitLeaseChange.Expired);
    }

    [Fact]
    public void Frames_From_Other_Sub_Channels_Are_Refused_Not_Queued()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<(int Sub, string Why)>();
        channel.TransmitRejected += (sub, _, why) => refused.Add((sub, why.Message));
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task other = channel.EnqueueTransmit(0, Frame(0x41));
        Task again = channel.EnqueueTransmit(0, Frame(0x42));
        Task holders = channel.EnqueueTransmit(3, Frame(0x43));

        other.IsFaulted.Should().BeTrue("refused the moment it is handed over");
        again.IsFaulted.Should().BeTrue();
        holders.IsCompleted.Should().BeFalse("the holder's frame is queued for the transmitter");
        refused.Should().HaveCount(2);
        refused.Should().OnlyContain(r => r.Sub == 0);
        refused.Select(r => r.Why).Distinct().Should().ContainSingle(
            "one sentence every time, so the station's rate-limited DROPPED line folds them together")
            .Which.Should().Be("sub-channel 3 holds the transmit lease, so other transmissions are refused until it ends");

        TransmitLeaseEvent released = default;
        channel.TransmitLease.Changed += change => released = change;
        channel.TransmitLease.Release();
        released.Refused.Should().Be(2, "the release says how much was turned away");
    }

    [Fact]
    public async Task What_Others_Had_Already_Queued_Is_Refused_When_The_Lease_Is_Taken()
    {
        (SoundModemChannel channel, _) = Station();
        var refused = new List<int>();
        channel.TransmitRejected += (sub, _, _) => refused.Add(sub);

        Task queuedBefore = channel.EnqueueTransmit(0, Frame(0x41));
        Task holdersBefore = channel.EnqueueTransmit(3, Frame(0x42));
        Task service = channel.EnqueueTransmit(_ => new float[100]);
        queuedBefore.IsCompleted.Should().BeFalse("nothing is running the transmitter yet");

        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        // A sub-channel frame's task is the announcing wrapper's, which resumes on its own
        // continuation, so it is awaited rather than read; the refusal itself was synchronous.
        Func<Task> waiting = () => queuedBefore.WaitAsync(TimeSpan.FromSeconds(10));
        await waiting.Should().ThrowAsync<InvalidOperationException>(
            "a queue would release stale frames when the lease ends, so it is emptied instead");
        service.IsFaulted.Should().BeTrue("a transmission that names no sub-channel belongs to nobody");
        holdersBefore.IsCompleted.Should().BeFalse("the holder's own frame keeps its place");
        refused.Should().Equal(0);
    }

    [Fact]
    public void A_Service_Transmission_Attributed_To_The_Holder_Goes_Through()
    {
        // The holder's Morse ident: queued through the delegate overload under the identifier,
        // which the station attributes to its modem's sub-channel.
        (SoundModemChannel channel, _) = Station();
        object holdersIdent = new();
        object othersIdent = new();
        channel.TransmitLease.Attribute(holdersIdent, 3);
        channel.TransmitLease.Attribute(othersIdent, 0);
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task mine = channel.EnqueueTransmit(_ => new float[100], source: holdersIdent);
        Task theirs = channel.EnqueueTransmit(_ => new float[100], source: othersIdent);

        mine.IsCompleted.Should().BeFalse("queued, waiting for the transmitter");
        theirs.IsFaulted.Should().BeTrue();
        channel.TransmitLease.Admits(holdersIdent).Should().BeTrue();
        channel.TransmitLease.Admits(othersIdent).Should().BeFalse();
    }

    [Fact]
    public void Normal_Service_Resumes_When_The_Lease_Runs_Out()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        channel.EnqueueTransmit(0, Frame(0x41)).IsFaulted.Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(60));

        channel.EnqueueTransmit(0, Frame(0x42)).IsCompleted.Should().BeFalse(
            "queued again like any frame, once nobody holds the transmitter");
    }

    [Fact]
    public async Task The_Holders_Frames_Go_On_The_Air_While_Everyone_Else_Is_Refused()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var sent = new List<int>();
        channel.FrameTransmitted += (sub, _) => { lock (sent) { sent.Add(sub); } };
        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));

        Task other = channel.EnqueueTransmit(0, Frame(0x41));
        Task holders = channel.EnqueueTransmit(3, Frame(0x42));

        var ptt = new RecordingPtt();
        using var cancellation = new CancellationTokenSource();
        Task transmitter = channel.RunTransmitterAsync(new FakeAudioOutput(SampleRate), ptt, cancellation.Token);
        await holders.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        other.IsFaulted.Should().BeTrue();
        sent.Should().Equal(3);
        time.GetUtcNow().Should().Be(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero),
            "nothing here needed the clock to move");
    }
}
