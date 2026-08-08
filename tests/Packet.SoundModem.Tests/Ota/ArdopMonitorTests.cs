using M0LTE.Ardop;
using M0LTE.Dsp;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// Positive controls for the ARDOP campaign's A0 monitor instrument: known frames from the
/// library's own modulator must come back through `sm-ota ardop-monitor`, at the native
/// centre and through the shifted path (the wild slot sits near 1650 Hz). An instrument
/// whose zero cannot be distinguished from a broken chain measures nothing - the
/// burst-verdict campaign's lesson, applied on day one here.
/// </summary>
/// <remarks>Console.Out redirection is process-global, so every test class that captures
/// command output shares the "console-capture" collection to stay serialized.</remarks>
[Collection("console-capture")]
public class ArdopMonitorTests
{
    private const int Rate = 12000;

    private static float[] KnownFrames()
    {
        ArdopStationId.TryParse("G7AAA", out ArdopStationId station).Should().BeTrue();
        var modulator = new ArdopModulator(100);
        byte[] id = ArdopFrameCodec.EncodeIdFrame(station, "IO91");
        byte[] payload = new byte[32];
        new Random(29).NextBytes(payload);
        byte[] data = ArdopFrameCodec.EncodeDataFrame(0x4A, payload, 0xFF);

        short[] idAudio = modulator.Modulate(id, 240, 20);
        short[] dataAudio = modulator.Modulate(data, 240, 20);
        var gap = new float[Rate / 2];
        var pad = new float[Rate];
        return
        [
            .. pad,
            .. idAudio.Select(Pcm16.ToFloat),
            .. gap,
            .. dataAudio.Select(Pcm16.ToFloat),
            .. pad,
        ];
    }

    private static string StageWav(float[] audio, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ardop-monitor-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        WavFile.WriteMono(path, audio, Rate);
        return path;
    }

    [Fact]
    public void Known_Frames_At_The_Native_Centre_Come_Back()
    {
        string wav = StageWav(KnownFrames(), "raw-20260807T120000Z.wav");

        var output = new StringWriter();
        TextWriter real = Console.Out;
        Console.SetOut(output);
        try
        {
            ArdopMonitorCommand.Run(["--raw", wav, "--centre", "1500", "--quiet"])
                .Should().Be(0);
        }
        finally
        {
            Console.SetOut(real);
        }

        string report = output.ToString();
        report.Should().Contain("IDFrame").And.Contain("4FSK.500.100");
        report.Should().MatchRegex(@"IDFrame\s+1 acquired,\s+1 ok");
    }

    [Fact]
    public void Known_Frames_Shifted_To_The_Wild_Slot_Centre_Come_Back()
    {
        // Render at native, move the audio up to 1650 the way the band carries it, and
        // monitor with the instrument's shifted path (bandpass-then-unshift).
        float[] native = KnownFrames();
        var shifted = new float[native.Length];
        new FrequencyShifter(Rate, 150).Process(native, shifted);
        string wav = StageWav(shifted, "raw-20260807T120100Z.wav");

        var output = new StringWriter();
        TextWriter real = Console.Out;
        Console.SetOut(output);
        try
        {
            ArdopMonitorCommand.Run(["--raw", wav, "--centre", "1650", "--quiet"])
                .Should().Be(0);
        }
        finally
        {
            Console.SetOut(real);
        }

        string report = output.ToString();
        report.Should().Contain("IDFrame").And.Contain("4FSK.500.100");
        report.Should().Contain("2 decoded ok");
    }

    [Fact]
    public void The_Emitted_Stream_Is_What_The_Demodulator_Heard()
    {
        // The --emit seam exists so external reference decoders can be handed exactly
        // the audio our demodulator consumed. Property under test: emitting from the
        // shifted path and re-monitoring the emission at the native centre (no shift)
        // reproduces the decode.
        float[] native = KnownFrames();
        var shifted = new float[native.Length];
        new FrequencyShifter(Rate, 150).Process(native, shifted);
        string wav = StageWav(shifted, "raw-20260807T120200Z.wav");
        string emitted = Path.Combine(Path.GetDirectoryName(wav)!, "emitted-120200.wav");

        var output = new StringWriter();
        TextWriter real = Console.Out;
        Console.SetOut(output);
        try
        {
            ArdopMonitorCommand.Run(["--wav", wav, "--centre", "1650", "--quiet", "--emit", emitted])
                .Should().Be(0);
            ArdopMonitorCommand.Run(["--wav", emitted, "--centre", "1500", "--quiet"])
                .Should().Be(0);
        }
        finally
        {
            Console.SetOut(real);
        }

        string[] reports = output.ToString().Split("=== ardop-monitor");
        reports.Should().HaveCount(3);
        reports[1].Should().Contain("2 decoded ok");
        reports[2].Should().Contain("2 decoded ok");
    }
}
