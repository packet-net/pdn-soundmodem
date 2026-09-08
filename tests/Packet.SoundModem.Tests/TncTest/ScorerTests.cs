using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Modems;
using Packet.SoundModem.TncTest;

namespace Packet.SoundModem.Tests.TncTest;

/// <summary>
/// The benchmark harness itself, over audio whose frame count is known because this test made it.
/// </summary>
/// <remarks>
/// A corpus score is only ever a comparison, so nothing in a real run can tell you the harness is
/// counting correctly: the recording's true packet count is unknown, which is the whole reason
/// the TNC Test CD is a leaderboard rather than a pass mark. These run the same code path over a
/// signal with a known answer, so a harness that lost frames at a chunk boundary, or double
/// counted them, or dropped the last one for want of a flush, fails here rather than quietly
/// producing a plausible number on the real thing.
/// </remarks>
public class ScorerTests
{
    private const int DspRate = 12000;

    [Fact]
    public void Every_Transmitted_Frame_Is_Scored()
    {
        float[] audio = Transmit(20, DspRate, out IReadOnlyList<byte[]> sent);

        ModeScore score = Scorer.Run("afsk1200", audio, DspRate, centreHz: null, offsetPairs: null);

        score.Score.Should().Be(sent.Count);
        score.Ax25Shaped.Should().Be(sent.Count);
        score.Delivered.Should().Be(sent.Count, "what the host was handed must match what was heard");
    }

    [Fact]
    public void A_Frame_Flush_Against_The_End_Of_The_Track_Is_Not_Lost()
    {
        // No trailing silence at all: the closing flag is the last thing in the file, which is
        // where a harness that does not flush the demodulator's filters loses a frame. A live
        // channel never ends, so nothing inside the modem does this for us.
        float[] audio = Transmit(3, DspRate, out _, trailingSilenceSeconds: 0);

        ModeScore score = Scorer.Run("afsk1200", audio, DspRate, centreHz: null, offsetPairs: null);

        score.Score.Should().Be(3);
    }

    [Fact]
    public void Distinct_Counts_Contents_And_Score_Counts_Transmissions()
    {
        // The same bytes sent twice, far enough apart to be two transmissions rather than two
        // readings of one. A TNC would report both, so the score is 2 and the distinct count 1.
        byte[] frame = Ui("M0LTE", "APRS", "the same thing twice");
        float[] audio = Concatenate(DspRate, 1.0, [Modulate(frame, DspRate), Modulate(frame, DspRate)]);

        ModeScore score = Scorer.Run("afsk1200", audio, DspRate, centreHz: null, offsetPairs: null);

        score.Score.Should().Be(2);
        score.Distinct.Should().Be(1);
    }

    [Fact]
    public void Source_Callsigns_Are_Counted_Once_Each_However_Often_They_Transmit()
    {
        float[] audio = Concatenate(DspRate, 0.6,
        [
            Modulate(Ui("M0LTE", "APRS", "one"), DspRate),
            Modulate(Ui("M0LTE-9", "APRS", "two"), DspRate),
            Modulate(Ui("M0LTE", "APRS", "three"), DspRate),
        ]);

        ModeScore score = Scorer.Run("afsk1200", audio, DspRate, centreHz: null, offsetPairs: null);

        score.Score.Should().Be(3);
        // An SSID is a different transmitter as far as a monitor is concerned, and the corpus
        // report counts it as one: two stations here, not one and not three.
        score.Stations.Should().BeEquivalentTo(["M0LTE", "M0LTE-9"]);
    }

    [Fact]
    public void A_Track_At_A_Rate_The_Mode_Does_Not_Run_At_Is_Resampled_And_Still_Scores()
    {
        // 44100 Hz is the rate the WA8LMF corpus arrives at and 12000 does not divide it, so
        // every score off that corpus passes through the resampler. This is that path.
        float[] atCdRate = Transmit(8, 44100, out IReadOnlyList<byte[]> sent);
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        WavFile.WriteMono(path, atCdRate, 44100);

        try
        {
            TrackAudio track = TrackReader.Read(path, requestedChannel: null);
            track.SampleRate.Should().Be(44100);

            ModeScore score = Scorer.Run(
                "afsk1200", TrackReader.AtRate(track, DspRate), DspRate,
                centreHz: null, offsetPairs: null);

            score.Score.Should().Be(sent.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_Window_Scores_Only_What_Is_Inside_It()
    {
        // Frames every 2 s from 2 s onwards; a 5 s window starting at 1 s holds the first two.
        float[] audio = Concatenate(DspRate, 2.0,
        [
            Modulate(Ui("M0LTE", "APRS", "one"), DspRate),
            Modulate(Ui("M0LTE", "APRS", "two"), DspRate),
            Modulate(Ui("M0LTE", "APRS", "three"), DspRate),
        ]);

        var whole = new TrackAudio(audio, DspRate, 1, 0, [0], 16, null);
        TrackAudio window = TrackReader.Slice(whole, skipSeconds: 1, lengthSeconds: 5);

        ModeScore score = Scorer.Run(
            "afsk1200", window.Samples, DspRate, centreHz: null, offsetPairs: null);

        score.Score.Should().Be(2);
    }

    /// <summary>A run of frames from different stations, spaced out as a channel would space them.</summary>
    private static float[] Transmit(
        int count, int sampleRate, out IReadOnlyList<byte[]> sent, double trailingSilenceSeconds = 0.5)
    {
        var frames = new List<byte[]>();
        var bursts = new List<float[]>();
        for (int i = 0; i < count; i++)
        {
            byte[] frame = Ui($"N0CAL-{i % 10}", "APRS", $"corpus harness test frame {i}");
            frames.Add(frame);
            bursts.Add(Modulate(frame, sampleRate));
        }

        sent = frames;
        return Concatenate(sampleRate, 0.4, bursts, trailingSilenceSeconds);
    }

    private static float[] Modulate(byte[] frame, int sampleRate) =>
        new Afsk1200Modem(sampleRate, _ => { }).Modulate(frame, txDelayMilliseconds: 300);

    private static float[] Concatenate(
        int sampleRate, double gapSeconds, IReadOnlyList<float[]> bursts,
        double trailingSilenceSeconds = 0.5)
    {
        int gap = (int)(gapSeconds * sampleRate);
        int total = bursts.Sum(b => b.Length) + (gap * bursts.Count)
            + (int)(trailingSilenceSeconds * sampleRate);
        var audio = new float[total];

        int at = gap;
        foreach (float[] burst in bursts)
        {
            burst.CopyTo(audio, at);
            at += burst.Length + gap;
        }

        return audio;
    }

    /// <summary>A UI frame, addresses and control only; the harness never sees an FCS.</summary>
    private static byte[] Ui(string source, string destination, string payload)
    {
        var frame = new List<byte>();
        frame.AddRange(Address(destination, last: false));
        frame.AddRange(Address(source, last: true));
        frame.Add(0x03);                                     // UI
        frame.Add(0xF0);                                     // no layer 3
        frame.AddRange(System.Text.Encoding.ASCII.GetBytes(payload));
        return [.. frame];
    }

    private static byte[] Address(string call, bool last)
    {
        string[] parts = call.Split('-');
        int ssid = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        var bytes = new byte[7];
        for (int i = 0; i < 6; i++)
        {
            bytes[i] = (byte)((i < parts[0].Length ? parts[0][i] : ' ') << 1);
        }

        bytes[6] = (byte)(0x60 | (ssid << 1) | (last ? 1 : 0));
        return bytes;
    }
}
