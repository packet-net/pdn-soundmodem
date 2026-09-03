using AwesomeAssertions;
using M0LTE.Radio.Audio;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

// The daemon's Station type and the library's Packet.SoundModem.Station NAMESPACE (the
// frequency-matching policy and friends) share a name, and from inside Packet.SoundModem.Tests
// the enclosing Packet.SoundModem is searched before this file's using directives are - so the
// bare name would find the namespace. The alias says which of the two is meant, once.
using DaemonStation = Packet.SoundModem.Daemon.Station;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// One station: an audio input, the channel its samples feed, the watches that decide the feed
/// has died, and the loop that turns between them. Carved out of the daemon's top-level
/// statements so a process can hold more than one of it. What is pinned here is what the live
/// stations depend on staying exactly as it was, plus the one thing that did change: a station
/// reports a fault and never ends the process itself.
/// </summary>
public class StationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-station").FullName;
    private readonly List<string> _out = [];
    private readonly List<string> _errors = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>An input the test drives: it delivers a block of one level, or nothing at all,
    /// and counts how many times it was asked. Paced like the real inputs, every one of which
    /// waits inside <c>Read</c> rather than returning 0 in a spin.</summary>
    private sealed class TestInput(int sampleRate, float level, bool deliver = true) : IAudioInput
    {
        private int _reads;

        public int SampleRate { get; } = sampleRate;

        public int Reads => Volatile.Read(ref _reads);

        /// <summary>The USB-card-unplugged family: <c>Read</c> throws instead of returning.</summary>
        public Exception? Death { get; set; }

        public int Read(Span<float> buffer)
        {
            Interlocked.Increment(ref _reads);
            if (Death is Exception death)
            {
                throw death;
            }

            Thread.Sleep(1);
            if (!deliver)
            {
                return 0;
            }

            buffer.Fill(level);
            return buffer.Length;
        }
    }

    private StationJournal Journal(string tag = "") => new(tag, _out.Add, _errors.Add);

    /// <summary>The CT 146 shape: a receive-only UberSDR station at 12 kHz, dead-feed thresholds
    /// left at the device family's own defaults, which is how the live one runs.</summary>
    private StationOptions UberSdrOptions(
        IAudioInput input, StationJournal journal, TimeProvider? clock = null) => new()
        {
            Channel = new SoundModemChannel(12000),
            Input = input,
            DspRate = 12000,
            Journal = journal,
            DeviceKind = DeadFeedDevice.UberSdr,
            TimeProvider = clock ?? TimeProvider.System,
        };

    /// <summary>Runs the station on a thread of its own, as a host with more than one would.</summary>
    private static Task RunAsync(DaemonStation station, CancellationToken token)
        => Task.Factory.StartNew(
            () => station.Run(token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    [Fact]
    public async Task A_Station_Runs_Its_Receive_Loop_Until_Its_Token_Is_Cancelled()
    {
        var input = new TestInput(12000, level: 0.1f);
        using var station = new DaemonStation(UberSdrOptions(input, Journal()));
        using var stopping = new CancellationTokenSource();

        Task running = RunAsync(station, stopping.Token);

        // The loop is turning: it keeps asking the input for audio.
        while (input.Reads < 5)
        {
            await Task.Delay(5);
        }

        running.IsCompleted.Should().BeFalse("nothing has asked the station to stop");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        _errors.Should().BeEmpty("a station stopped by its host has nothing to report");
        _out.Should().BeEmpty("a receive loop that is working says nothing at all");
    }

    [Fact]
    public async Task A_Starved_Station_Faults_Instead_Of_Exiting_The_Process()
    {
        var clock = new FakeTimeProvider();
        // An input that never delivers: the hung-WebSocket case, where Read returns 0 forever
        // and the silence watch has nothing to look at.
        var input = new TestInput(12000, level: 0, deliver: false);
        using var station = new DaemonStation(UberSdrOptions(input, Journal(), clock));
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;
        using var stopping = new CancellationTokenSource();

        Task running = RunAsync(station, stopping.Token);
        while (input.Reads < 2)
        {
            await Task.Delay(5);
        }

        clock.Advance(TimeSpan.FromSeconds(30));

        faults.Should().ContainSingle().Which.Stalled.Should().BeFalse(
            "the loop can still be brought down tidily");

        // The station stopped itself, without the host having to ask - and without ending the
        // process, which is what this whole extraction exists to make possible.
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        // The grace period passing is a second, different fault: the one a host answers by
        // ending the process. The station still does not do it.
        clock.Advance(TimeSpan.FromSeconds(15));
        faults.Should().HaveCount(2);
        faults[1].Stalled.Should().BeTrue("nothing short of ending the process recovers this");
    }

    [Fact]
    public async Task A_Dead_Feed_Faults_The_Station_And_Says_Which_One()
    {
        // Exact zeros at full rate: an UberSDR whose own SDR feed has died while it goes on
        // streaming. One second of them is a dead feed for the purposes of this test.
        var input = new TestInput(12000, level: 0);
        StationJournal journal = Journal("m9psy-1");
        StationOptions options = UberSdrOptions(input, journal) with
        {
            DeadFeed = new DeadFeedConfig { SilenceSeconds = 1, StarvationSeconds = 0 },
        };
        using var station = new DaemonStation(options);

        // Journalling a fault is the host's, which is what puts the station's tag on it - and
        // is how fifty stations in one journal stay readable.
        station.Faulted += fault => journal.WriteError(fault.Reason);
        using var stopping = new CancellationTokenSource();

        await RunAsync(station, stopping.Token).WaitAsync(TimeSpan.FromSeconds(10));

        _errors.Should().ContainSingle().Which.Should().Be(
            "m9psy-1: receive feed dead: 1 s of unbroken digital silence from the receiver's IQ "
            + "stream - its SDR feed has likely died - restarting to reconnect afresh");
    }

    [Fact]
    public async Task A_Stations_Journal_Lines_Carry_Its_Tag_When_It_Has_One()
    {
        const string Excuse = "receive feed silent because this station stood down from a "
            + "contested slice, which is deliberate - not restarting.";

        // Silence the station itself asked for: journalled, and the loop keeps turning. Run
        // twice, once for a station with a tag and once for the only station in the process,
        // which is the case every live node station is in.
        foreach (string tag in new[] { "m9psy-1", "" })
        {
            _errors.Clear();
            var input = new TestInput(12000, level: 0);
            StationOptions options = UberSdrOptions(input, Journal(tag)) with
            {
                DeadFeed = new DeadFeedConfig { SilenceSeconds = 1, StarvationSeconds = 0 },
                SilenceExcuse = () => Excuse,
            };
            using var station = new DaemonStation(options);
            station.Faulted += fault => throw new InvalidOperationException(
                $"an excused silence is not a fault: {fault.Reason}");
            using var stopping = new CancellationTokenSource();

            Task running = RunAsync(station, stopping.Token);
            while (_errors.Count == 0)
            {
                await Task.Delay(5);
            }

            await stopping.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            _errors[0].Should().Be(tag.Length == 0 ? Excuse : $"m9psy-1: {Excuse}");
        }
    }

    [Fact]
    public async Task An_On_Demand_Station_With_No_Session_Is_Not_Starved()
    {
        // The 40 m monitor's whole idea: with nobody watching there is no session, so there is
        // nothing to be starved of. Without the stand-down the 30 s starvation default would
        // restart an idle daemon every half minute and hammer somebody else's receiver.
        var clock = new FakeTimeProvider();
        var input = new TestInput(12000, level: 0, deliver: false);
        StationOptions options = UberSdrOptions(input, Journal(), clock) with
        {
            SessionLive = () => false,
        };
        using var station = new DaemonStation(options);
        var faults = new List<StationFault>();
        station.Faulted += faults.Add;
        using var stopping = new CancellationTokenSource();

        Task running = RunAsync(station, stopping.Token);
        while (input.Reads < 2)
        {
            await Task.Delay(5);
        }

        clock.Advance(TimeSpan.FromMinutes(10));
        faults.Should().BeEmpty("quiet with nobody watching is the arrangement, not a fault");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_Single_Station_Writes_The_Same_Lines_As_Before()
    {
        // The CT 146 configuration's shape - "device": "ubersdr:...", one station in the
        // process, dead-feed thresholds at the UberSDR family's 30 s defaults - and every
        // sentence it can write. Byte for byte what Program.cs printed before there was a
        // Station type, and with no tag, because the operator reading this journal has learned
        // what these lines look like and the tests that pin them are the proof they have not
        // moved.
        var clock = new FakeTimeProvider();

        // A feed that stops delivering at all, and then a shutdown that cannot get out of a
        // blocked Read.
        var hung = new TestInput(12000, level: 0, deliver: false);
        await RunUntilStoppedAsync(UberSdrOptions(hung, Journal(), clock), running: async () =>
        {
            while (hung.Reads < 2)
            {
                await Task.Delay(5);
            }

            clock.Advance(TimeSpan.FromSeconds(30));
        });

        clock.Advance(TimeSpan.FromSeconds(15));

        // A feed that keeps delivering exact zeros.
        var silent = new TestInput(12000, level: 0);
        await RunUntilStoppedAsync(UberSdrOptions(silent, Journal(), clock));

        // And the input dying under the loop.
        var dead = new TestInput(12000, level: 0.1f)
        {
            Death = new InvalidOperationException("snd_pcm_readi: No such device"),
        };
        await RunUntilStoppedAsync(UberSdrOptions(dead, Journal(), clock));

        _errors.Should().Equal(
        [
            "receive feed starved: an open session delivered no audio for 30 s - a hung stream "
                + "- restarting to reconnect afresh",
            "receive feed starved: the orderly shutdown stalled (the input's Read is blocked) - "
                + "exiting hard so the service restarts",
            "receive feed dead: 30 s of unbroken digital silence from the receiver's IQ stream "
                + "- its SDR feed has likely died - restarting to reconnect afresh",
            "receive feed dead: the input device failed (snd_pcm_readi: No such device) - "
                + "restarting to reopen it",
        ]);
        _out.Should().BeEmpty("none of this belongs on stdout");
    }

    /// <summary>Runs one station to its fault, journalling it the way the daemon's host does.</summary>
    private async Task RunUntilStoppedAsync(StationOptions options, Func<Task>? running = null)
    {
        using var station = new DaemonStation(options);
        station.Faulted += fault => options.Journal.WriteError(fault.Reason);
        using var stopping = new CancellationTokenSource();

        Task loop = RunAsync(station, stopping.Token);
        if (running is not null)
        {
            await running();
        }

        await loop.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Two_Stations_In_One_Process_Keep_Separate_Frame_Logs()
    {
        // Flavour B writes frames-<slug>.db per station under one directory. The daemon wires
        // each station's channel to its own log, so what a receiver heard is filed under that
        // receiver and nothing else.
        string firstPath = Path.Combine(_dir, "frames-m9psy-1.db");
        string secondPath = Path.Combine(_dir, "frames-g4eyr.db");
        FrameLog first = FrameLog.Open(firstPath);
        FrameLog second = FrameLog.Open(secondPath);

        using var stationOne = new DaemonStation(
            UberSdrOptions(new TestInput(12000, level: 0.1f), Journal("m9psy-1")));
        using var stationTwo = new DaemonStation(
            UberSdrOptions(new TestInput(12000, level: 0.1f), Journal("g4eyr")));
        stationOne.Channel.Should().NotBeSameAs(stationTwo.Channel);

        first.Record(0, Frame("M0LTE"), Quality(), audioHz: 850, rfHz: 7_050_300);
        second.Record(0, Frame("G4EYR"), Quality(), audioHz: 2150, rfHz: 7_051_600);

        // One station closing must not reach across and shut every other station's connection
        // pool, which is what a process-wide ClearAllPools did.
        await first.DisposeAsync();

        second.Record(0, Frame("M0LTE-2"), Quality(), audioHz: 2150, rfHz: 7_051_600);
        await second.DisposeAsync();

        ReadSources(firstPath).Should().Equal(["M0LTE"]);
        ReadSources(secondPath).Should().Equal(["G4EYR", "M0LTE-2"]);
    }

    /// <summary>An AX.25 UI frame, so the log has a real callsign to file the row under.</summary>
    private static byte[] Frame(string from) =>
        Packet.SoundModem.Waterfall.Ax25UiFrame.Build(from, "GB7RDG", new byte[8]);

    private static FrameQuality Quality() =>
        new("bpsk300-il2pc", FrameBytes: 32, CorrectedBytes: 0, CrcValid: true,
            FrequencyOffsetHz: null, EmphasisDb: null);

    private static List<string> ReadSources(string path)
    {
        var sources = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT source FROM frames ORDER BY id";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                sources.Add(reader.GetString(0));
            }

            SqliteConnection.ClearPool(connection);
        }

        return sources;
    }
}
