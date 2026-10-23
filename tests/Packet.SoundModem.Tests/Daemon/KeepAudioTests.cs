using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon.KeepAudio;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// The in-memory ring behind <c>/api/keepaudio</c>: audio laid on the wall clock, silence where
/// nothing arrived, and a stretch read back by time.
/// </summary>
public class KeepAudioRingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-22T16:00:00Z");

    private static float[] Ramp(int count, float step = 1f / 32768)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = ((i % 1000) + 1) * step;
        }

        return samples;
    }

    [Fact]
    public void A_Stretch_Reads_Back_By_Time_Sample_Exact()
    {
        var ring = new KeepAudioRing(8000, TimeSpan.FromSeconds(60));
        float[] block = Ramp(800);
        for (int i = 1; i <= 50; i++)
        {
            ring.Append(block, Start + TimeSpan.FromMilliseconds(100 * i));
        }

        ring.HeldFrom.Should().Be(Start);
        ring.HeldTo.Should().Be(Start + TimeSpan.FromSeconds(5));

        (float[] samples, int held) = ring.Extract(Start + TimeSpan.FromSeconds(1), Start + TimeSpan.FromSeconds(2));
        samples.Should().HaveCount(8000);
        held.Should().Be(8000);
        samples[0].Should().BeApproximately(block[0], 1e-6f, "a second in is the start of a block");
        samples[123].Should().BeApproximately(block[123], 1e-6f);
    }

    [Fact]
    public void A_Gap_In_Delivery_Becomes_Silence_So_Later_Audio_Stays_On_Time()
    {
        var ring = new KeepAudioRing(8000, TimeSpan.FromSeconds(60));
        ring.Append(Ramp(8000), Start + TimeSpan.FromSeconds(1));

        // Nothing for three seconds, as while the station transmits, then a second of audio.
        ring.Append(Ramp(8000), Start + TimeSpan.FromSeconds(5));

        ring.HeldTo.Should().Be(Start + TimeSpan.FromSeconds(5));
        (float[] gap, _) = ring.Extract(Start + TimeSpan.FromSeconds(1.5), Start + TimeSpan.FromSeconds(3.5));
        gap.Should().OnlyContain(s => s == 0, "the station heard nothing then");
        (float[] after, _) = ring.Extract(Start + TimeSpan.FromSeconds(4), Start + TimeSpan.FromSeconds(5));
        after[0].Should().BeApproximately(1f / 32768, 1e-6f, "the late block lands where it arrived");
    }

    [Fact]
    public void Audio_Running_Ahead_Of_The_Clock_Moves_The_Origin_Back()
    {
        var ring = new KeepAudioRing(8000, TimeSpan.FromSeconds(60));
        ring.Append(Ramp(8000), Start + TimeSpan.FromSeconds(1));
        // A backlog: two seconds of audio delivered after only one more second of clock.
        ring.Append(Ramp(16000), Start + TimeSpan.FromSeconds(2));

        ring.HeldTo.Should().Be(Start + TimeSpan.FromSeconds(2), "the newest sample is now");
        ring.HeldFrom.Should().Be(Start - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Only_The_Last_Ring_Length_Is_Held_And_The_Rest_Reads_As_Silence()
    {
        var ring = new KeepAudioRing(8000, TimeSpan.FromSeconds(10));
        for (int i = 1; i <= 30; i++)
        {
            ring.Append(Ramp(8000), Start + TimeSpan.FromSeconds(i));
        }

        ring.HeldFrom.Should().Be(Start + TimeSpan.FromSeconds(20));
        (float[] samples, int held) = ring.Extract(Start + TimeSpan.FromSeconds(15), Start + TimeSpan.FromSeconds(25));
        samples.Should().HaveCount(80000);
        held.Should().Be(40000, "the first five seconds asked for have been overwritten");
        samples[..40000].Should().OnlyContain(s => s == 0);
        samples[40000].Should().BeApproximately(1f / 32768, 1e-6f);
    }
}

/// <summary>
/// The recorder behind <c>/api/keepaudio</c>: what it refuses, what it clamps, what it writes and
/// what it deletes.
/// </summary>
public sealed class KeepAudioRecorderTests : IDisposable
{
    private const int InputRate = 48000;
    private readonly ScratchDirectory _scratch = new("keep-audio-tests");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-22T15:55:00Z"));
    private readonly List<string> _said = [];
    private bool _busy;

    public void Dispose() => _scratch.Dispose();

    private string Root => Path.Combine(_scratch.FullName, "kept-audio");

    private KeepAudioRecorder NewRecorder() => new(
        InputRate, Root,
        at => new DetectorSample(
            at, _busy, false, 3, _busy ? "energy sub3" : null,
            new BusyReading(_busy ? 9.5 : 1.5, 6), null, "sub3 ms110d=" + (_busy ? "busy" : "clear")),
        _time, _said.Add, _said.Add);

    /// <summary>Runs the station for a while: 100 ms blocks of a 1 kHz tone and a look each time.</summary>
    private void Run(KeepAudioRecorder recorder, TimeSpan length)
    {
        var block = new float[InputRate / 10];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = 0.25f * MathF.Sin(2 * MathF.PI * 1000 * i / InputRate);
        }

        for (int i = 0; i < (int)(length.TotalSeconds * 10); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            recorder.Process(block);
            recorder.Tick();
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    [Fact]
    public async Task A_Window_Partly_In_The_Past_Is_Written_Once_It_Ends()
    {
        using KeepAudioRecorder recorder = NewRecorder();
        Run(recorder, TimeSpan.FromSeconds(30));

        (KeptWindow? window, int status, string? refusal) = recorder.Keep(
            "slot 16:00", Now - TimeSpan.FromSeconds(10), Now + TimeSpan.FromSeconds(10));
        status.Should().Be(200, refusal);
        window!.Id.Should().Be("20261022T155520Z-slot-16-00");

        _busy = true;
        Run(recorder, TimeSpan.FromSeconds(5));
        _busy = false;
        Run(recorder, TimeSpan.FromSeconds(6));
        recorder.Status().Pending.Should().ContainSingle("it is not written until its end has settled");
        Run(recorder, TimeSpan.FromSeconds(2));

        await recorder.WhenWrittenAsync();
        string directory = Path.Combine(Root, window.Id);
        Directory.Exists(directory).Should().BeTrue();

        (float[] audio, int rate) = WavFile.ReadMono(Path.Combine(directory, "audio.wav"));
        rate.Should().Be(KeepAudioRecorder.StoredRate);
        audio.Should().HaveCount(20 * KeepAudioRecorder.StoredRate);
        audio.Skip(8000).Take(8000).Max().Should().BeInRange(0.2f, 0.3f, "the tone survives decimation");

        string[] csv = File.ReadAllLines(Path.Combine(directory, "detector.csv"));
        csv[0].Should().Be(KeptWindowWriter.CsvHeader);
        csv.Should().HaveCount(21, "one row per second");
        csv[1].Should().StartWith("2026-10-22T15:55:20Z,0,0.00,0,3,,1.5,6.0,,");
        csv.Count(l => l.Contains(",1,1.00,0,3,energy sub3,9.5,6.0,", StringComparison.Ordinal)).Should().BeInRange(4, 5);

        PngProbe.Read(Path.Combine(directory, "waterfall.png")).Width.Should().Be(80, "20 s at four columns a second");

        JsonNode description = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "window.json")))!;
        description["label"]!.GetValue<string>().Should().Be("slot 16:00");
        description["note"].Should().BeNull();
        recorder.Status().Kept.Should().Equal(window.Id);
        recorder.Status().Pending.Should().BeEmpty();
    }

    [Fact]
    public void A_Window_From_Before_Start_Up_Is_Clamped_And_Says_So()
    {
        using KeepAudioRecorder recorder = NewRecorder();
        Run(recorder, TimeSpan.FromSeconds(20));

        (KeptWindow? window, int status, _) = recorder.Keep(
            "early", Now - TimeSpan.FromMinutes(2), Now + TimeSpan.FromSeconds(10));
        status.Should().Be(200);
        window!.From.Should().Be(Now - TimeSpan.FromSeconds(20));
        window.Note.Should().Contain("only been listening since");
    }

    [Theory]
    [InlineData(-30, -20, 0, "is not to be refused, this row is a control")]
    [InlineData(0, 601, 400, "longer than 10 minutes")]
    [InlineData(-301, 10, 400, "further back than the ring is promised to hold")]
    [InlineData(800, 901, 400, "ending more than 15 minutes ahead")]
    [InlineData(10, 10, 400, "ending where it starts")]
    [InlineData(10, 5, 400, "ending before it starts")]
    public void Unreasonable_Windows_Are_Refused_With_A_Reason(int fromSeconds, int toSeconds, int expected, string why)
    {
        using KeepAudioRecorder recorder = NewRecorder();
        Run(recorder, TimeSpan.FromMinutes(6));

        (KeptWindow? window, int status, string? refusal) = recorder.Keep(
            "x", Now + TimeSpan.FromSeconds(fromSeconds), Now + TimeSpan.FromSeconds(toSeconds));
        if (expected == 0)
        {
            window.Should().NotBeNull(why);
            return;
        }

        status.Should().Be(expected, why);
        window.Should().BeNull();
        refusal.Should().NotBeNullOrWhiteSpace();
        refusal!.All(c => c is >= ' ' and <= '~').Should().BeTrue("journal and API text is ASCII");
    }

    [Fact]
    public void Overlapping_Windows_Are_All_Kept_And_Their_Names_Do_Not_Collide()
    {
        using KeepAudioRecorder recorder = NewRecorder();
        Run(recorder, TimeSpan.FromSeconds(30));
        DateTimeOffset from = Now - TimeSpan.FromSeconds(20);

        recorder.Keep("a", from, Now).Window!.Id.Should().Be("20261022T155510Z-a");
        recorder.Keep("a", from, Now + TimeSpan.FromSeconds(5)).Window!.Id.Should().Be("20261022T155510Z-a-2");
        recorder.Keep("b", from + TimeSpan.FromSeconds(5), Now).Window.Should().NotBeNull();
        recorder.Status().Pending.Should().HaveCount(3);
    }

    [Fact]
    public void Too_Many_Waiting_Windows_Are_A_Conflict()
    {
        using KeepAudioRecorder recorder = NewRecorder();
        Run(recorder, TimeSpan.FromSeconds(5));
        for (int i = 0; i < KeepAudioRecorder.MaxPending; i++)
        {
            recorder.Keep($"w{i}", Now, Now + TimeSpan.FromMinutes(1)).Status.Should().Be(200);
        }

        recorder.Keep("one more", Now, Now + TimeSpan.FromMinutes(1)).Status.Should().Be(409);
    }

    [Theory]
    [InlineData("slot 1600", "slot-1600")]
    [InlineData("  ../../etc/passwd ", "etc-passwd")]
    [InlineData("GB7RDG_mailcast.v2", "GB7RDG_mailcast.v2")]
    [InlineData("café  run", "caf-run")]
    [InlineData("///", "")]
    public void Labels_Become_Safe_Directory_Names(string label, string expected) =>
        KeepAudioRecorder.SanitiseLabel(label).Should().Be(expected);

    [Fact]
    public void Labels_That_Leave_Nothing_Are_Refused()
    {
        using KeepAudioRecorder recorder = NewRecorder();
        recorder.Keep("///", Now, Now + TimeSpan.FromSeconds(5)).Status.Should().Be(400);
        recorder.Keep("", Now, Now + TimeSpan.FromSeconds(5)).Status.Should().Be(400);
        recorder.Keep(new string('a', 65), Now, Now + TimeSpan.FromSeconds(5)).Status.Should().Be(400);
    }

    [Fact]
    public void Windows_Older_Than_Thirty_Days_Are_Pruned_And_Nothing_Else_Is()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-29T12:00:00Z");
        string old = Path.Combine(Root, "20261030T115959Z-old");
        string young = Path.Combine(Root, "20261030T120001Z-young");
        string partial = Path.Combine(Root, ".20261001T000000Z-stale.partial");
        string foreign = Path.Combine(Root, "someone-elses");
        foreach (string d in (string[])[old, young, partial, foreign])
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "f"), "x");
        }

        KeepAudioRecorder.Prune(Root, now, _said.Add).Should().Be(2);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(partial).Should().BeFalse("a half-written window left by a crash goes too");
        Directory.Exists(young).Should().BeTrue();
        Directory.Exists(foreign).Should().BeTrue("a directory not named by a time is not ours");
    }
}

/// <summary>The request handling of <c>/api/keepaudio</c>, without HTTP.</summary>
public sealed class KeepAudioApiTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new("keep-audio-api-tests");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-22T15:58:00Z"));
    private readonly KeepAudioRecorder _recorder;

    public KeepAudioApiTests()
    {
        _recorder = new KeepAudioRecorder(
            8000, _scratch.FullName,
            at => new DetectorSample(at, false, false, null, null, null, null, ""),
            _time, _ => { }, _ => { });
        for (int i = 0; i < 60; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _recorder.Process(new float[8000]);
        }
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _scratch.Dispose();
    }

    [Fact]
    public void A_Good_Request_Answers_With_The_Window()
    {
        (int status, JsonObject answer) = KeepAudioApi.Handle(_recorder, "POST",
            """{"from": "2026-10-22T15:58:30Z", "to": "2026-10-22T16:01:30.2Z", "label": "slot 1600"}""");
        status.Should().Be(200, answer.ToJsonString());
        answer["id"]!.GetValue<string>().Should().Be("20261022T155830Z-slot-1600");
        answer["from"]!.GetValue<string>().Should().Be("2026-10-22T15:58:30Z");
        answer["to"]!.GetValue<string>().Should().Be("2026-10-22T16:01:31Z", "the end rounds outwards to the second");
        answer["clamped"]!.GetValue<bool>().Should().BeFalse();
        answer["readyAfter"]!.GetValue<string>().Should().Be("2026-10-22T16:01:33Z");
        answer["directory"]!.GetValue<string>().Should().Be(Path.Combine(_scratch.FullName, "20261022T155830Z-slot-1600"));

        JsonObject listed = KeepAudioApi.Handle(_recorder, "GET", "").Answer;
        listed["pending"]!.AsArray().Should().ContainSingle();
        listed["sampleRate"]!.GetValue<int>().Should().Be(8000);
        listed["retentionDays"]!.GetValue<double>().Should().Be(30);
    }

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("[1]", "POST {")]
    [InlineData("""{"from": "2026-10-22T15:58:30Z", "label": "x"}""", "POST {")]
    [InlineData("""{"from": "yesterday", "to": "2026-10-22T16:00:00Z", "label": "x"}""", "\"from\" is not a time")]
    [InlineData("""{"from": 5, "to": "2026-10-22T16:00:00Z", "label": "x"}""", "are strings")]
    [InlineData("""{"from": "2026-10-22T15:58:30Z", "to": "2026-10-22T16:00:00Z", "label": "x", "seconds": 5}""", "no field \"seconds\"")]
    [InlineData("""{"from": "2026-10-22T15:58:30Z", "to": "2026-10-22T16:00:00Z"}""", "\"label\" is required")]
    public void A_Bad_Request_Is_A_400_That_Says_Why(string body, string expected)
    {
        (int status, JsonObject answer) = KeepAudioApi.Handle(_recorder, "POST", body);
        status.Should().Be(400);
        answer["refused"]!.GetValue<string>().Should().Contain(expected);
    }

    [Fact]
    public void Another_Method_Is_A_405_With_The_Usage() =>
        KeepAudioApi.Handle(_recorder, "DELETE", "").Status.Should().Be(405);
}

/// <summary>The PNG the waterfall is written as is one a decoder can read.</summary>
public class KeepAudioPngTests
{
    [Fact]
    public void An_Encoded_Image_Reads_Back_Pixel_Exact_With_Valid_Chunks()
    {
        const int width = 7;
        const int height = 5;
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(i * 37);
        }

        string path = Path.Combine(Path.GetTempPath(), $"keep-audio-png-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, PngWriter.Encode(width, height, rgb));
            (int w, int h, byte[] pixels) = PngProbe.Read(path);
            w.Should().Be(width);
            h.Should().Be(height);
            pixels.Should().Equal(rgb);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_Waterfall_Marks_Busy_Seconds_Red_In_Its_Strip()
    {
        DateTimeOffset from = DateTimeOffset.Parse("2026-10-22T16:00:00Z");
        var samples = new List<DetectorSample>();
        for (int i = 0; i < 40; i++)
        {
            DateTimeOffset at = from + TimeSpan.FromMilliseconds(100 * i);
            bool busy = i is >= 10 and < 20;
            samples.Add(new DetectorSample(at, busy, false, null, busy ? "fm-shape" : null, null, null, ""));
        }

        var audio = new float[4 * 8000];
        string path = Path.Combine(Path.GetTempPath(), $"keep-audio-waterfall-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, WaterfallPicture.Render(audio, 8000, from, samples));
            (int w, int h, byte[] pixels) = PngProbe.Read(path);
            w.Should().Be(16);
            h.Should().Be(WaterfallPicture.Height);
            (byte, byte, byte) Pixel(int x, int y) => (pixels[(y * w + x) * 3], pixels[(y * w + x) * 3 + 1], pixels[(y * w + x) * 3 + 2]);
            Pixel(0, 0).Should().Be(WaterfallPicture.ClearColour);
            Pixel(5, 0).Should().Be(WaterfallPicture.BusyColour, "1.25 s in, inside the busy second");
            Pixel(12, 0).Should().Be(WaterfallPicture.ClearColour);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The CSV: busy when any look in a second said busy, the look that best explains it, and a bare
/// time for a second with no looks.
/// </summary>
public class KeptWindowCsvTests
{
    [Fact]
    public void Each_Second_Is_Summarised_From_Its_Looks()
    {
        DateTimeOffset from = DateTimeOffset.Parse("2026-10-22T16:00:00Z");
        DetectorSample Look(double seconds, bool busy, double level) =>
            new(from + TimeSpan.FromSeconds(seconds), busy, false, null, busy ? "energy sub0" : null,
                new BusyReading(level, 6), -95.5, "sub0 afsk1200=x");
        DetectorSample[] looks = [Look(0.1, false, 1), Look(0.5, false, 3), Look(1.2, true, 7.5), Look(1.6, false, 2)];

        string[] rows = KeptWindowWriter.Csv(from, from + TimeSpan.FromSeconds(3), looks, out int busySeconds)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        busySeconds.Should().Be(1);
        rows.Should().Equal(
            KeptWindowWriter.CsvHeader,
            "2026-10-22T16:00:00Z,0,0.00,0,,,3.0,6.0,-95.5,\"sub0 afsk1200=x\"",
            "2026-10-22T16:00:01Z,1,0.50,0,,energy sub0,7.5,6.0,-95.5,\"sub0 afsk1200=x\"",
            "2026-10-22T16:00:02Z,,,,,,,,,");
    }
}

/// <summary>The channel's account of its own carrier-sense answer.</summary>
public class CarrierSenseExplanationTests
{
    private sealed class FixedSource(bool? busy) : IChannelBusySource
    {
        public bool? Busy => busy;
    }

    [Fact]
    public void A_Source_With_An_Opinion_Is_Named_As_The_Reason()
    {
        var channel = new SoundModemChannel(12000, channelBusySource: new FixedSource(true));
        CarrierSenseExplanation why = channel.ExplainCarrierSense();
        why.Busy.Should().BeTrue();
        why.DecidedBy.Should().Be(nameof(FixedSource));
        why.Detectors.Should().ContainSingle().Which.Counts.Should().BeTrue();
        DetectorSample.Describe(why.Detectors).Should().Be("FixedSource=busy");
    }

    [Fact]
    public void A_Clear_Channel_Has_No_Reason()
    {
        var channel = new SoundModemChannel(12000, channelBusySource: new FixedSource(false));
        CarrierSenseExplanation why = channel.ExplainCarrierSense();
        why.Busy.Should().BeFalse();
        why.DecidedBy.Should().BeNull();
        why.RadioSignalDbm.Should().BeNull();
    }
}

/// <summary>A strict little PNG reader for the tests: every chunk's CRC checked, IDAT inflated.</summary>
internal static class PngProbe
{
    public static (int Width, int Height, byte[] Rgb) Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        file.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        int at = 8;
        int width = 0;
        int height = 0;
        using var idat = new MemoryStream();
        while (true)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            ReadOnlySpan<byte> data = file.AsSpan(at + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8 + length));
            PngWriter.Crc(file.AsSpan(at + 4, 4), data).Should().Be(crc, $"the {type} chunk's CRC");
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                data[8].Should().Be(8);
                data[9].Should().Be(2);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            at += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        byte[] raw = inflated.ToArray();
        raw.Should().HaveCount(height * (1 + width * 3));
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            raw[y * (1 + width * 3)].Should().Be(0, "every row is unfiltered");
            Array.Copy(raw, y * (1 + width * 3) + 1, rgb, y * width * 3, width * 3);
        }

        return (width, height, rgb);
    }
}
