using M0LTE.Ardop;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// End-to-end controls for the ARDOP campaign's A1 cutter (`sm-ota ardop-cut`): a baseline
/// CSV produced by the monitor over synthetic chunks must cut to a window that decodes the
/// same frames again, including a frame that straddles a chunk boundary (the stitch) and a
/// capture gap inside the window (the silence fill keeps manifest offsets truthful).
/// </summary>
/// <remarks>Console.Out redirection is process-global, so every test class that captures
/// command output shares the "console-capture" collection to stay serialized.</remarks>
[Collection("console-capture")]
public class ArdopCutTests
{
    private const int Rate = 12000;

    private static (float[] Id, float[] Data) Frames()
    {
        ArdopStationId.TryParse("G7AAA", out ArdopStationId station).Should().BeTrue();
        var modulator = new ArdopModulator(100);
        byte[] payload = new byte[32];
        new Random(31).NextBytes(payload);
        float[] id = [.. modulator.Modulate(ArdopFrameCodec.EncodeIdFrame(station, "IO91"), 240, 20)
            .Select(Pcm16.ToFloat)];
        float[] data = [.. modulator.Modulate(ArdopFrameCodec.EncodeDataFrame(0x4A, payload, 0xFF), 240, 20)
            .Select(Pcm16.ToFloat)];
        return (id, data);
    }

    private static string Stage(params (string Name, float[] Audio)[] chunks)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ardop-cut-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach ((string name, float[] audio) in chunks)
        {
            WavFile.WriteMono(Path.Combine(dir, name), audio, Rate);
        }

        return dir;
    }

    private static string RunToString(string[] argv, Func<string[], int> command)
    {
        var output = new StringWriter();
        TextWriter real = Console.Out;
        Console.SetOut(output);
        try
        {
            command(argv).Should().Be(0);
        }
        finally
        {
            Console.SetOut(real);
        }

        return output.ToString();
    }

    [Fact]
    public void A_Cut_Spanning_A_Chunk_Boundary_Decodes_The_Same_Frames_Again()
    {
        (float[] id, float[] data) = Frames();

        // Chunk 1: 1 s pad + ID frame, filled to 4 s; the data frame starts in chunk 1
        // and finishes in chunk 2 - the stitch is load-bearing for the decode.
        float[] all = [.. new float[Rate], .. id, .. new float[Rate / 2], .. data, .. new float[Rate]];
        int split = 4 * Rate;
        string dir = Stage(
            ("raw-20260807T120000Z.wav", all[..split]),
            ("raw-20260807T120004Z.wav", all[split..]));

        string csv = Path.Combine(dir, "baseline.csv");
        RunToString(["--raw", dir, "--centre", "1500", "--quiet", "--csv", csv],
            ArdopMonitorCommand.Run).Should().Contain("2 decoded ok");

        string outDir = Path.Combine(dir, "cuts");
        RunToString(["--csv", csv, "--raw", dir, "--out", outDir, "--all", "--pre", "6", "--post", "1"],
            ArdopCutCommand.Run);

        string[] cuts = Directory.GetFiles(outDir, "cut-*.wav");
        cuts.Should().HaveCount(1);
        File.ReadLines(Path.Combine(outDir, "manifest.csv")).Skip(1).Should().HaveCount(2);

        RunToString(["--wav", cuts[0], "--centre", "1500", "--quiet"], ArdopMonitorCommand.Run)
            .Should().Contain("2 decoded ok");
    }

    [Fact]
    public void A_Capture_Gap_Inside_The_Window_Is_Silence_Filled_And_Offsets_Stay_Truthful()
    {
        (float[] id, float[] data) = Frames();

        // Chunk 1 carries the ID frame; the capture then breaks for 2 s; chunk 2 carries
        // the data frame. The fill lands in the inter-frame silence, so both frames must
        // still decode, and the manifest offsets must reflect wall time, not spliced time.
        string dir = Stage(
            ("raw-20260807T120000Z.wav", [.. new float[Rate], .. id, .. new float[Rate / 2]]),
            ("raw-20260807T120006Z.wav", [.. new float[Rate / 2], .. data, .. new float[Rate]]));

        string csv = Path.Combine(dir, "baseline.csv");
        RunToString(["--raw", dir, "--centre", "1500", "--quiet", "--csv", csv],
            ArdopMonitorCommand.Run).Should().Contain("2 decoded ok");

        string outDir = Path.Combine(dir, "cuts");
        string report = RunToString(
            ["--csv", csv, "--raw", dir, "--out", outDir, "--all", "--pre", "6", "--post", "1"],
            ArdopCutCommand.Run);
        report.Should().Contain("gap-fill");

        string[] rows = [.. File.ReadLines(Path.Combine(outDir, "manifest.csv")).Skip(1)];
        rows.Should().HaveCount(2);
        double[] offsets = [.. rows.Select(r => double.Parse(r.Split(',')[1]))];
        DateTimeOffset[] stamps = [.. rows.Select(r => DateTimeOffset.Parse(r.Split(',')[2]))];
        (offsets[1] - offsets[0]).Should().BeApproximately(
            (stamps[1] - stamps[0]).TotalSeconds, 0.3);

        string[] cuts = Directory.GetFiles(outDir, "cut-*.wav");
        RunToString(["--wav", cuts[0], "--centre", "1500", "--quiet"], ArdopMonitorCommand.Run)
            .Should().Contain("2 decoded ok");
    }
}
