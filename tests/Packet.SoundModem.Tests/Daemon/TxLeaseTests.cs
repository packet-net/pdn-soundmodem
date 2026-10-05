using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// <c>/api/txlease</c> and what the journal says about the lease. The request handling is tested
/// without a listener and on a fake clock; one test goes through the real HTTP route and key.
/// </summary>
public class TxLeaseTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static (SoundModemChannel Channel, FakeTimeProvider Time) Station()
    {
        var time = new FakeTimeProvider(Noon);
        var channel = new SoundModemChannel(12000, time, randomSeed: 1);
        channel.AddModem(0, sink => new Afsk1200Modem(12000, sink));
        channel.AddModem(3, sink => new Afsk1200Modem(12000, sink));
        return (channel, time);
    }

    private static (int Status, JsonObject Answer) Post(SoundModemChannel channel, string body, string? cannot = null) =>
        TxLeaseApi.Handle(channel, "POST", body, cannot);

    [Fact]
    public void A_Lease_Is_Taken_And_Renewed_With_Its_Expiry_In_The_Answer()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();

        (int status, JsonObject taken) = Post(channel, """{"subChannel": 3, "seconds": 60}""");
        status.Should().Be(200);
        taken["held"]!.GetValue<bool>().Should().BeTrue();
        taken["subChannel"]!.GetValue<int>().Should().Be(3);
        taken["expires"]!.GetValue<string>().Should().Be("2026-10-04T12:01:00Z");
        taken["renewed"]!.GetValue<bool>().Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(30));
        (status, JsonObject renewed) = Post(channel, """{"subChannel": 3, "seconds": 60}""");
        status.Should().Be(200);
        renewed["renewed"]!.GetValue<bool>().Should().BeTrue();
        renewed["expires"]!.GetValue<string>().Should().Be("2026-10-04T12:01:30Z");
    }

    [Fact]
    public void Another_Sub_Channel_Is_Refused_With_A_409_Naming_The_Holder()
    {
        (SoundModemChannel channel, _) = Station();
        Post(channel, """{"subChannel": 3}""");

        (int status, JsonObject answer) = Post(channel, """{"subChannel": 0, "seconds": 60}""");

        status.Should().Be(409);
        answer["subChannel"]!.GetValue<int>().Should().Be(3);
        answer["refused"]!.GetValue<string>().Should().Contain("sub-channel 3 holds the transmit lease");
        channel.TransmitLease.Holder.Should().Be(3);
    }

    [Fact]
    public void A_Long_Lease_Is_Capped_At_Five_Minutes_And_Says_So()
    {
        (SoundModemChannel channel, _) = Station();

        (int status, JsonObject answer) = Post(channel, """{"subChannel": 3, "seconds": 3600}""");

        status.Should().Be(200);
        answer["seconds"]!.GetValue<double>().Should().Be(300);
        answer["capped"]!.GetValue<bool>().Should().BeTrue();
        answer["expires"]!.GetValue<string>().Should().Be("2026-10-04T12:05:00Z");
    }

    [Fact]
    public void Release_Gives_It_Back_And_A_Get_Reads_The_Lease()
    {
        (SoundModemChannel channel, _) = Station();
        Post(channel, """{"subChannel": 3}""");

        (int getStatus, JsonObject held) = TxLeaseApi.Handle(channel, "GET", "", null);
        getStatus.Should().Be(200);
        held["held"]!.GetValue<bool>().Should().BeTrue();

        (int wrong, _) = Post(channel, """{"release": true, "subChannel": 0}""");
        wrong.Should().Be(409, "a caller naming another sub-channel does not hold it to release");

        (int status, JsonObject released) = Post(channel, """{"release": true}""");
        status.Should().Be(200);
        released["released"]!.GetValue<bool>().Should().BeTrue();
        released["held"]!.GetValue<bool>().Should().BeFalse();
        channel.TransmitLease.Holder.Should().BeNull();

        Post(channel, """{"release": true}""").Answer["released"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData("{}", 400)]
    [InlineData("""{"subChannel": 3, "seconds": 0}""", 400)]
    [InlineData("""{"subChannel": 7}""", 400)]
    [InlineData("""{"subChannel": "3"}""", 400)]
    [InlineData("""{"subChannel": 3, "seconds": "60"}""", 400)]
    [InlineData("not json", 400)]
    public void A_Request_That_Does_Not_Say_What_It_Wants_Is_A_400(string body, int expected)
    {
        (SoundModemChannel channel, _) = Station();

        (int status, JsonObject answer) = Post(channel, body);

        status.Should().Be(expected);
        answer["refused"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public void A_Station_That_Cannot_Transmit_Grants_No_Lease()
    {
        (SoundModemChannel channel, _) = Station();

        (int status, JsonObject answer) = Post(channel, """{"subChannel": 3}""", cannot: "this station receives only");

        status.Should().Be(409);
        answer["refused"]!.GetValue<string>().Should().Be("this station receives only");
    }

    [Fact]
    public void The_Journal_Says_Take_Release_And_Expiry_And_Only_Some_Renewals()
    {
        (SoundModemChannel channel, FakeTimeProvider time) = Station();
        var lines = new List<string>();
        var journal = new TxLeaseJournal(new StationJournal("", lines.Add, lines.Add), sub => $"mode{sub}");
        channel.TransmitLease.Changed += journal.Note;

        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        lines.Should().ContainSingle().Which.Should().Be(
            "tx lease: sub-channel 3 (mode3) holds the transmitter until 2026-10-04T12:01:00Z; "
            + "transmissions from other sub-channels are refused until it ends");

        // Fifteen minutes of renewals every 30 s: thirty renewals, and a line for every five
        // minutes of them rather than one each.
        for (int i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        }

        lines.Count(l => l.Contains("still holds")).Should().Be(3);
        lines.Should().Contain(l => l.Contains("still holds the transmitter after 5m00s (10 renewals)"));

        channel.EnqueueTransmit(0, new byte[20]);
        channel.TransmitLease.Release();
        lines[^1].Should().Be(
            "tx lease: sub-channel 3 (mode3) released the transmitter after 15m00s; "
            + "1 transmission from others refused; normal service resumes");

        channel.TransmitLease.Take(3, TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(60));
        lines[^1].Should().Be(
            "tx lease: sub-channel 3 (mode3) stopped renewing and its lease ran out at "
            + "2026-10-04T12:16:00Z, after 1m00s; its unsent frames were dropped; "
            + "0 transmissions from others refused; normal service resumes");

        lines.Should().OnlyContain(l => l.All(c => c < 0x80), "journal lines are plain ASCII");
    }

    [Fact]
    public async Task The_Endpoint_Is_Served_Under_The_Api_Key()
    {
        const string key = "test-key-not-a-secret";
        (SoundModemChannel channel, _) = Station();
        var api = new ConfigApi(
            key, "/nonexistent/soundmodem.json", "/nonexistent/pending.json",
            runningJson: () => "{}", ephemeralInForce: false, requestRestart: () => { });
        api.ServeTxLease(channel, cannot: null);

        int port = FreePorts.Next();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task serving = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                HttpListenerContext context = await listener.GetContextAsync();
                await api.HandleAsync(context, context.Request.Url!.AbsolutePath);
            }
        });

        var url = new Uri($"http://127.0.0.1:{port}/api/txlease", UriKind.Absolute);
        using var anonymous = new HttpClient();
        HttpResponseMessage refused = await anonymous.PostAsync(
            url, new StringContent("""{"subChannel": 3}""", Encoding.UTF8, "application/json"));

        using var keyed = new HttpClient();
        keyed.DefaultRequestHeaders.Add("X-API-Key", key);
        HttpResponseMessage granted = await keyed.PostAsync(
            url, new StringContent("""{"subChannel": 3, "seconds": 60}""", Encoding.UTF8, "application/json"));

        await serving.WaitAsync(TimeSpan.FromSeconds(30));
        listener.Close();

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        granted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await granted.Content.ReadAsStringAsync()).Should().Contain("\"held\": true");
        channel.TransmitLease.Holder.Should().Be(3);
    }

    [Fact]
    public void Something_Held_Back_By_A_Lease_Is_Said_Once_Then_Counted_Each_Minute()
    {
        var time = new FakeTimeProvider(Noon);
        var said = new List<string>();
        var line = new LeaseQuietLine(time, (holder, more) => said.Add($"{holder}:{more}"));

        for (int i = 0; i < 20; i++)
        {
            line.Note(3);
            time.Advance(TimeSpan.FromSeconds(5));
        }

        // 100 s of an ARQ session replying every 5 s: the first, then one line a minute later
        // carrying the eleven held back in between, and the rest still being counted.
        said.Should().Equal("3:0", "3:11");
    }

    [Fact]
    public async Task The_Holders_Own_Ident_Keys_During_Its_Lease_And_Nobody_Elses_Is_Asked()
    {
        (SoundModemChannel channel, _) = Station();
        var holders = new Packet.SoundModem.Ident.StationIdentifier("M0LTE", null, 1500, 20, TimeSpan.FromMinutes(10), 12000);
        var others = new Packet.SoundModem.Ident.StationIdentifier("M0LTE", null, 1700, 20, TimeSpan.FromMinutes(10), 12000);
        IdentTransmission.Register(channel, 3, holders);
        IdentTransmission.Register(channel, 0, others);
        holders.NoteTransmission();
        others.NoteTransmission();
        channel.TransmitLease.Take(3, TimeSpan.FromMinutes(5));

        IdentTransmission.ShouldSend(channel, holders).Should().BeTrue("the mailcast slot identifies on its own modem");
        IdentTransmission.ShouldSend(channel, others).Should().BeFalse("it stays owed until the lease ends");

        Task refused = IdentTransmission.SendAsync(channel, others);
        refused.IsFaulted.Should().BeTrue("asked anyway, it is refused rather than queued");

        var output = new Packet.SoundModem.Tests.Channel.FakeAudioOutput(12000);
        var ptt = new Packet.SoundModem.Tests.Channel.RecordingPtt();
        channel.Csma.Persistence = 255;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task sent = IdentTransmission.SendAsync(channel, holders);
        Task transmitter = channel.RunTransmitterAsync(output, ptt, cancellation.Token);
        try
        {
            await sent.WaitAsync(TimeSpan.FromMinutes(1));
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await transmitter;
            }
            catch (OperationCanceledException)
            {
            }
        }

        ptt.Events.Should().StartWith("key");
        output.WrittenCount.Should().BeGreaterThan(12000, "a CW ident of a callsign is seconds of tone");
    }

    [Fact]
    public void Drop_Queued_Drops_The_Holders_Unsent_Frames_With_Or_Without_A_Release()
    {
        (SoundModemChannel channel, _) = Station();
        Post(channel, """{"subChannel": 3}""");
        channel.EnqueueTransmit(3, new byte[20]);
        channel.EnqueueTransmit(3, new byte[20]);

        (int status, JsonObject dropped) = Post(channel, """{"dropQueued": true}""");
        status.Should().Be(200);
        dropped["dropped"]!.GetValue<int>().Should().Be(2);
        dropped["held"]!.GetValue<bool>().Should().BeTrue("dropping is not releasing");

        channel.EnqueueTransmit(3, new byte[20]);
        (status, JsonObject both) = Post(channel, """{"release": true, "dropQueued": true, "subChannel": 3}""");
        status.Should().Be(200);
        both["dropped"]!.GetValue<int>().Should().Be(1);
        both["released"]!.GetValue<bool>().Should().BeTrue();
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public void Drop_Queued_With_No_Lease_Or_For_Someone_Else_Is_Refused()
    {
        (SoundModemChannel channel, _) = Station();
        Post(channel, """{"dropQueued": true}""").Status.Should().Be(409);

        Post(channel, """{"subChannel": 3}""");
        Post(channel, """{"dropQueued": true, "subChannel": 0}""").Status.Should().Be(409);
    }

    [Fact]
    public void The_Answer_Says_Whether_The_Channel_Is_Busy_Right_Now()
    {
        (SoundModemChannel channel, _) = Station();

        JsonObject idle = TxLeaseApi.Handle(channel, "GET", "", null).Answer;
        idle["channelBusy"]!.GetValue<bool>().Should().BeFalse("nothing is on a channel that has heard no audio");
        idle["carrierDetect"]!.GetValue<bool>().Should().BeFalse();

        JsonObject taken = Post(channel, """{"subChannel": 3, "maxCarrierWaitSeconds": 30}""").Answer;
        taken.ContainsKey("channelBusy").Should().BeTrue("the head end decides on this without keying");
        taken["maxCarrierWaitSeconds"]!.GetValue<double>().Should().Be(30);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("301")]
    [InlineData("\"30\"")]
    public void A_Carrier_Wait_Out_Of_Range_Is_A_400(string wait)
    {
        (SoundModemChannel channel, _) = Station();

        Post(channel, $$"""{"subChannel": 3, "maxCarrierWaitSeconds": {{wait}}}""").Status.Should().Be(400);
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public async Task A_Released_Lease_Closes_With_The_Holders_Ident_Before_Anyone_Else_Keys()
    {
        (SoundModemChannel channel, _) = Station();
        channel.Csma.Persistence = 255;
        var holders = new Packet.SoundModem.Ident.StationIdentifier("M0LTE", null, 1500, 20, TimeSpan.FromMinutes(10), 12000);
        var quiet = new Packet.SoundModem.Ident.StationIdentifier("M0LTE", null, 1700, 20, TimeSpan.FromMinutes(10), 12000);
        var identifiers = new Dictionary<int, Packet.SoundModem.Ident.StationIdentifier> { [3] = holders, [0] = quiet };
        IdentTransmission.Register(channel, 3, holders);
        IdentTransmission.Register(channel, 0, quiet);
        holders.NoteTransmission();
        Task? identSent = null;
        channel.TransmitLease.Closing = IdentTransmission.Closing(
            identifiers, (_, owed) => identSent = IdentTransmission.SendAsync(channel, owed));

        // A modem that has not transmitted since its last ident owes no closing one.
        IdentTransmission.Closing(identifiers, (_, _) => Task.CompletedTask)(0).Should().BeNull();

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.TransmitLease.Changed += change =>
        {
            if (change.Change == TransmitLeaseChange.Released)
            {
                released.TrySetResult();
            }
        };
        channel.TransmitLease.Take(3, TimeSpan.FromMinutes(5));
        channel.TransmitLease.Release(onlyIf: 3).Should().BeTrue();
        identSent.Should().NotBeNull("the holder transmitted, so it closes with its ident");
        channel.EnqueueTransmit(0, new byte[20]).IsFaulted.Should().BeTrue("still held while the ident goes");

        var ptt = new Packet.SoundModem.Tests.Channel.RecordingPtt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task transmitter = channel.RunTransmitterAsync(new Packet.SoundModem.Tests.Channel.FakeAudioOutput(12000), ptt, cancellation.Token);
        await identSent!.WaitAsync(TimeSpan.FromMinutes(1));
        await released.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await cancellation.CancelAsync();
        try
        {
            await transmitter;
        }
        catch (OperationCanceledException)
        {
        }

        ptt.Events.Should().StartWith("key");
        channel.TransmitLease.Holder.Should().BeNull();
    }

    [Fact]
    public void A_Closing_Lease_Waits_For_A_Periodic_Ident_Already_Queued_Rather_Than_Queue_Another()
    {
        (SoundModemChannel channel, _) = Station();
        var holders = new Packet.SoundModem.Ident.StationIdentifier("M0LTE", null, 1500, 20, TimeSpan.FromMinutes(10), 12000);
        IdentTransmission.Register(channel, 3, holders);
        holders.NoteTransmission();
        Task periodic = IdentTransmission.SendAsync(channel, holders); // queued; nothing is transmitting
        int identified = 0;
        Func<int, Task?> closing = IdentTransmission.Closing(
            new Dictionary<int, Packet.SoundModem.Ident.StationIdentifier> { [3] = holders },
            (_, _) =>
            {
                identified++;
                return Task.CompletedTask;
            });

        closing(3).Should().BeSameAs(periodic, "the lease closes on the ident already on its way");
        identified.Should().Be(0);
        IdentTransmission.ShouldSend(channel, holders).Should().BeFalse("one queued ident at a time");
    }

    [Fact]
    public void Renewing_A_Lease_That_Is_Closing_Says_So()
    {
        (SoundModemChannel channel, _) = Station();
        channel.TransmitLease.Closing = _ => new TaskCompletionSource().Task;
        Post(channel, """{"subChannel": 3}""");
        Post(channel, """{"release": true, "subChannel": 3}""").Answer["closing"]!.GetValue<bool>().Should().BeTrue();

        (int status, JsonObject answer) = Post(channel, """{"subChannel": 3, "seconds": 60}""");

        status.Should().Be(409);
        answer["closing"]!.GetValue<bool>().Should().BeTrue();
        answer["refused"]!.GetValue<string>().Should().StartWith("this lease is closing");
    }

    [Fact]
    public void A_Release_With_No_Lease_Releases_Nothing()
    {
        (SoundModemChannel channel, _) = Station();

        (int status, JsonObject answer) = Post(channel, """{"release": true}""");

        status.Should().Be(200);
        answer["released"]!.GetValue<bool>().Should().BeFalse();
    }
}
