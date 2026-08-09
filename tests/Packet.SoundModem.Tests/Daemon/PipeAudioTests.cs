using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The virtual audio link two daemons use to hear each other: FIFOs carrying raw float samples.
/// </summary>
/// <remarks>
/// The end-to-end claim - two processes, one frame, no hardware - is made by
/// <c>scripts/two-station-pipe.py</c>, which needs two built daemons and so cannot live here.
/// These are the pieces that can be pinned in a unit test: the device string, and the property
/// that makes the whole thing work rather than deadlock.
/// </remarks>
public class PipeAudioTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-pipe").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("pipe:/tmp/in,/tmp/out", "/tmp/in", "/tmp/out", 48000)]
    [InlineData("pipe:/tmp/in,/tmp/out,12000", "/tmp/in", "/tmp/out", 12000)]
    public void A_Pipe_Device_Names_Two_Fifos_And_A_Rate(
        string device, string expectedIn, string expectedOut, int expectedRate)
    {
        PipeAudio.IsPipe(device).Should().BeTrue();

        (string inPipe, string outPipe, int rate) = PipeAudio.Parse(device);

        inPipe.Should().Be(expectedIn);
        outPipe.Should().Be(expectedOut);
        rate.Should().Be(expectedRate);
    }

    [Theory]
    [InlineData("pipe:")]
    [InlineData("pipe:/tmp/only-one")]
    [InlineData("pipe:,/tmp/out")]
    [InlineData("pipe:/tmp/in,")]
    [InlineData("pipe:/a,/b,/c,/d")]
    public void A_Malformed_Pipe_Device_Says_What_It_Should_Look_Like(string device)
    {
        Action act = () => PipeAudio.Parse(device);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*pipe:<in>,<out>*");
    }

    [Theory]
    [InlineData("plughw:1,0")]
    [InlineData("null")]
    [InlineData("flex:radio")]
    [InlineData(null)]
    public void Nothing_Else_Is_A_Pipe_Device(string? device)
    {
        PipeAudio.IsPipe(device).Should().BeFalse();
    }

    [Fact]
    public void Either_Station_Can_Start_First()
    {
        // The property the whole arrangement rests on. A FIFO opened read-only blocks until
        // somebody opens the write end, and write-only blocks until somebody opens the read end -
        // so two daemons each opening their own side first would deadlock, and which one started
        // first would decide whether the station came up. Both ends are opened read-write, which
        // on a FIFO blocks for nobody and never reports end of file.
        string alone = Path.Combine(_dir, "nobody-at-the-far-end");

        Action openInputFirst = () =>
        {
            using var input = new PipeAudioInput(alone, 48000);
        };

        openInputFirst.Should().NotThrow("a station must come up before its neighbour exists");

        Action openOutputFirst = () =>
        {
            using var output = new PipeAudioOutput(Path.Combine(_dir, "also-alone"), 48000);
        };

        openOutputFirst.Should().NotThrow();
    }

    [Fact]
    public void What_One_Station_Writes_The_Other_Reads()
    {
        string air = Path.Combine(_dir, "air");
        using var output = new PipeAudioOutput(air, 48000);
        using var input = new PipeAudioInput(air, 48000);

        float[] sent = [.. Enumerable.Range(0, 480).Select(n => (float)Math.Sin(n * 0.1))];
        output.Write(sent);
        output.Drain();

        var heard = new List<float>();
        var buffer = new float[4800];
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (heard.Count < sent.Length && DateTime.UtcNow < deadline)
        {
            int read = input.Read(buffer);
            heard.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        // The samples are in there, exactly as written, behind however much silence the pacing
        // decided to hand over first.
        float[] all = [.. heard];
        int at = Array.IndexOf(all, sent[0]);
        at.Should().BeGreaterThanOrEqualTo(0, "the written samples have to turn up");
        all.AsSpan(at, sent.Length).ToArray().Should().Equal(sent);
    }

    [Fact]
    public void A_Quiet_Link_Delivers_Silence_Rather_Than_Blocking_Forever()
    {
        // A capture device that blocked between transmissions would stall the receive loop: the
        // DCD would freeze at whatever it last saw, and the dead-feed watch would eventually call
        // an input dead that is merely quiet. A sound card hands up silence, so this does too.
        using var input = new PipeAudioInput(Path.Combine(_dir, "quiet"), 48000);
        var buffer = new float[480];

        int read = input.Read(buffer);

        read.Should().BeGreaterThan(0, "a quiet band still produces samples");
        buffer.AsSpan(0, read).ToArray().Should().OnlyContain(s => s == 0f);
    }
}
