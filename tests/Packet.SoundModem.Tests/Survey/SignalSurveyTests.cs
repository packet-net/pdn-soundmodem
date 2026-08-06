using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// Turning "something went past and I will never know what" into a WAV and a sidecar. The
/// station's own slots are what a burst is judged against, so these tests drive a survey
/// configured like the live 40 m plan.
/// </summary>
public class SignalSurveyTests : IDisposable
{
    private const int SampleRate = 12000;
    private const double BinWidthHz = 5.859375;
    private const int LinesPerSecond = 30;
    private const int LineLength = 1024;

    // The live plan: afsk300 at 850 Hz audio, ARDOP at 1500, bpsk300 at 2150 - and the gap
    // between the first two, where the operator saw a signal nobody was listening to.
    private static readonly ModemBand[] Bands =
    [
        new(0, "afsk300-il2pc", 675, 1025, 850),
        new(1, "ardop", 1250, 1750, 1500),
        new(2, "bpsk300-il2pc", 1950, 2350, 2150),
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-survey").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 4, 15, 52, 0, TimeSpan.Zero));

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

    private SignalSurveyOptions Options() => new()
    {
        Directory = _dir,
        TimeProvider = _time,
        DialFrequencyHz = 7_049_450,
        Sideband = "usb",
        MarginSeconds = 0.2,
    };

    private static byte[] Line(double? lowHz = null, double? highHz = null)
    {
        var line = new byte[LineLength];
        byte ToByte(double db) => (byte)Math.Clamp(
            (db - WaterfallSource.FloorDb) * (255 / -WaterfallSource.FloorDb), 0, 255);

        line.AsSpan().Fill(ToByte(-70));
        if (lowHz is double low && highHz is double high)
        {
            int lowBin = (int)(low / BinWidthHz);
            int highBin = (int)(high / BinWidthHz);
            line.AsSpan(lowBin, highBin - lowBin).Fill(ToByte(-50));
        }

        return line;
    }

    /// <summary>Runs the survey over a stretch of channel: quiet, then a burst, then quiet. Audio
    /// is fed in step with the lines so the ring and the line clock agree.</summary>
    private static void Play(
        SignalSurvey survey, double? lowHz, double? highHz, int burstLines,
        Action<long>? atBurstEnd = null)
    {
        int hop = SampleRate / LinesPerSecond;
        var audio = new float[hop];
        byte[] quiet = Line();
        byte[] signal = Line(lowHz, highHz);
        long line = 0;

        void Feed(byte[] which, int count)
        {
            for (int i = 0; i < count; i++)
            {
                survey.AddAudio(audio);
                survey.AddLine(line++, which);
            }
        }

        Feed(quiet, 90);          // bank a floor
        Feed(signal, burstLines);
        atBurstEnd?.Invoke(line);
        Feed(quiet, 60);          // let it close, and leave a trailing margin in the ring
    }

    private BurstCapture[] Captures()
    {
        var captures = new List<BurstCapture>();
        foreach (string path in Directory.GetFiles(_dir, "*.json").Order())
        {
            captures.Add(JsonSerializer.Deserialize<BurstCapture>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                })!);
        }

        return [.. captures];
    }

    private void Settle(SignalSurvey survey)
    {
        survey.Dispose();   // drains the background writer
    }

    [Fact]
    public void A_Packet_Nobody_Was_Listening_To_Is_Captured()
    {
        // The operator's case: a burst at 1144 Hz audio (7.050594 MHz on this dial), in the gap
        // between the afsk300 slot's top edge and ARDOP's bottom one. Every modem was running and
        // not one of them could have read it.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 944, 1344, burstLines: 60);
        Settle(survey);

        BurstCapture capture = Captures().Should().ContainSingle().Subject;
        capture.Verdict.Should().Be(SurveyVerdict.Unclaimed);
        capture.AudioCentreHz.Should().BeApproximately(1144, 25);
        capture.WidthHz.Should().BeApproximately(400, 30);
        capture.DurationSeconds.Should().BeApproximately(2, 0.3);
        capture.RfCentreHz.Should().BeApproximately(7_050_594, 30, "the dial makes it a band frequency");
        capture.Modems.Should().Contain("0:afsk300-il2pc").And.Contain("2:bpsk300-il2pc");
        Directory.GetFiles(_dir, "*.wav").Should().ContainSingle("the audio is the point");
    }

    [Fact]
    public void A_Burst_In_Our_Own_Slot_That_Did_Not_Decode_Is_Captured_As_A_Miss()
    {
        // The more valuable capture of the two: the station was listening and could not read it.
        // Invisible today unless somebody happens to be recording at the time.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 1950, 2350, burstLines: 60);
        Settle(survey);

        BurstCapture capture = Captures().Should().ContainSingle().Subject;
        capture.Verdict.Should().Be(SurveyVerdict.Missed);
        capture.SubChannel.Should().Be(2, "and it says which modem failed to read it");
        capture.Mode.Should().Be("bpsk300-il2pc");
    }

    [Fact]
    public void A_Burst_Our_Modem_Decoded_Is_Not_Captured()
    {
        // Normal traffic. The station already has the frame, and a week of captures of its own
        // working channel would bury everything worth looking at.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 1950, 2350, burstLines: 60, atBurstEnd: _ =>
            survey.NoteDecode(2, Ax25Frame("GB7RDG", "EI0RSI"), Quality("bpsk300-il2pc")));
        Settle(survey);

        Captures().Should().BeEmpty();
    }

    [Fact]
    public void A_Keyup_Reset_Is_Applied_On_The_Receive_Path_And_Abandons_The_Burst()
    {
        // Reset arrives from the transmitter thread at keyup; it must still take effect even
        // though the mutation is deferred to the receive path (the unguarded lists belong to
        // that thread). A burst straddling our own transmission is not a measurement of
        // anybody else's, so nothing may be captured from it.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 944, 1344, burstLines: 60, atBurstEnd: _ => survey.Reset());
        Settle(survey);

        Captures().Should().BeEmpty("the burst in flight was abandoned at the keyup");
    }

    [Fact]
    public void A_Burst_Read_As_Plain_Il2p_And_Withheld_Counts_As_Decoded()
    {
        // The survey is fed from the monitor path, so a frame the station read and did not pass
        // to its host marks its burst decoded and stops it being kept as a miss. That is the
        // wanted answer rather than an accident of the wiring: the survey's question is whether
        // this station could read that burst, and it could. A badged row in the panel naming
        // GB7BPQ tells the operator far more than another WAV of the same beacon every ten
        // minutes, which would spend the capture budget the next unexplained burst needs.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 1950, 2350, burstLines: 60, atBurstEnd: _ => survey.NoteDecode(
            2,
            Ax25Frame("GB7BPQ", "BEACON"),
            Quality("bpsk300-il2pc-multi9") with
            {
                CrcValid = null, PlainIl2p = true, MonitorOnly = true,
            }));
        Settle(survey);

        Captures().Should().BeEmpty("the station did read it, whatever it did with it afterwards");
    }

    [Fact]
    public void A_Decode_From_A_Waveform_That_Is_Not_Ax25_Is_A_Frame_The_Station_Read()
    {
        // ARDOP carries Winlink sessions and ID frames, not AX.25, so running its payload past
        // the address parser asks a question about a different protocol - and the parser's "byte
        // 0 is not a shifted callsign character" would file a perfectly good decode as
        // unattributed and capture it. Worse than noise in the directory: it would say the ARDOP
        // modem cannot read anything, on a station where it is working.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        byte[] session = [0x02, 0x40, 0x1F, .. "FC EM ARQ"u8.ToArray()];
        Play(survey, 1300, 1700, burstLines: 60, atBurstEnd: _ =>
            survey.NoteDecode(1, session, Quality("ardop"), ax25: false));
        Settle(survey);

        Captures().Should().BeEmpty("the station read it, whatever protocol it was carrying");
    }

    [Fact]
    public void A_Frame_That_Decoded_Without_Readable_Callsigns_Is_Captured_With_Its_Bytes()
    {
        // The operator's other sighting: "unattributed · 2·bpsk300-il2pc-multi9 · 118 B". The
        // frame passed RS and the IL2P CRC, so the bytes are right and it is the AX.25 addresses
        // that are not there - which means the payload is the evidence, and it travels with the
        // audio so the modulation can be re-examined against it.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        byte[] frame = [0x00, 0x01, 0x02, 0x03, .. new byte[114]];
        Play(survey, 1950, 2350, burstLines: 60, atBurstEnd: _ =>
            survey.NoteDecode(2, frame, Quality("bpsk300-il2pc-multi9")));
        Settle(survey);

        BurstCapture capture = Captures().Should().ContainSingle().Subject;
        capture.Verdict.Should().Be(SurveyVerdict.Unattributed);
        capture.FrameHex.Should().NotBeNull().And.StartWith("00010203");
        capture.SubChannel.Should().Be(2);
        capture.Mode.Should().Be("bpsk300-il2pc-multi9");

        // The sidecar answers the question rather than posing it: which IL2P encapsulation it
        // arrived in - Type 1 and Type 0 put the address field in different places - and what
        // specifically would not read. Without these, diagnosing one of these means pulling the
        // payload blob out of the frame log by hand, which is what happened the first time.
        capture.Il2pHeaderType.Should().Be("Type1");
        capture.AttributionNote.Should().NotBeNull()
            .And.Contain("byte 0").And.Contain("destination");
    }

    [Fact]
    public void An_Ssb_Over_Is_Not_A_Packet()
    {
        // Capturing every voice contact on 40 m would fill the disk with the one thing this
        // facility is not for. Note what does the separating: a voice over and a wide data burst
        // occupy much the same 2.4 kHz, so width alone cannot tell them apart - an over runs for
        // tens of seconds and the longest frame these modes can carry does not.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 300, 2700, burstLines: 25 * LinesPerSecond);
        Settle(survey);

        Captures().Should().BeEmpty();
    }

    [Fact]
    public void A_Wide_But_Brief_Burst_Is_Still_A_Packet()
    {
        // The other side of that judgement, stated so it cannot be quietly lost: 2.4 kHz for two
        // seconds is a wideband data mode, not somebody talking, and it is exactly the kind of
        // thing this station has no decoder for.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 300, 2700, burstLines: 2 * LinesPerSecond);
        Settle(survey);

        // Missed rather than Unclaimed: a burst is placed by its centre, and 1500 Hz is the
        // ARDOP slot. A signal that wide overlaps every modem the station runs, and saying it
        // belongs to the one it is centred on is the least misleading of the available answers.
        Captures().Should().ContainSingle().Which.Verdict.Should().Be(SurveyVerdict.Missed);
    }

    [Fact]
    public void A_Carrier_That_Never_Stops_Is_Not_A_Packet()
    {
        var options = Options();
        options.MaxSeconds = 1;
        var survey = new SignalSurvey(options, Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        Play(survey, 944, 1344, burstLines: 300);
        Settle(survey);

        Captures().Should().BeEmpty("something that does not stop is not a transmission");
    }

    [Fact]
    public void The_Same_Frequency_Is_Left_Alone_For_A_While_After_A_Capture()
    {
        // One station rag-chewing on an unclaimed frequency is one discovery, not two hundred.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        int hop = SampleRate / LinesPerSecond;
        var audio = new float[hop];
        byte[] quiet = Line();
        byte[] signal = Line(944, 1344);
        long line = 0;

        void Feed(byte[] which, int count)
        {
            for (int i = 0; i < count; i++)
            {
                survey.AddAudio(audio);
                survey.AddLine(line++, which);
            }
        }

        Feed(quiet, 90);
        for (int round = 0; round < 3; round++)
        {
            Feed(signal, 60);
            Feed(quiet, 60);
        }

        long skipped = survey.SkippedForBudget;
        Settle(survey);

        Captures().Should().ContainSingle("the cooldown holds the second and third");
        skipped.Should().Be(2, "and says so rather than dropping them silently");
    }

    [Fact]
    public void A_Different_Frequency_Is_Not_Held_By_Another_Ones_Cooldown()
    {
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        int hop = SampleRate / LinesPerSecond;
        var audio = new float[hop];
        byte[] quiet = Line();
        long line = 0;

        void Feed(byte[] which, int count)
        {
            for (int i = 0; i < count; i++)
            {
                survey.AddAudio(audio);
                survey.AddLine(line++, which);
            }
        }

        Feed(quiet, 90);
        Feed(Line(944, 1344), 60);
        Feed(quiet, 60);
        Feed(Line(2600, 3000), 60);
        Feed(quiet, 60);
        Settle(survey);

        Captures().Should().HaveCount(2);
    }

    [Fact]
    public void Nothing_Is_Written_Where_The_Audio_Has_Already_Aged_Out()
    {
        // The line clock and the audio must be fed in step. Fed lines with no audio behind them,
        // the survey must write nothing rather than a WAV of whatever happened to be in the ring.
        var survey = new SignalSurvey(Options(), Bands, SampleRate, BinWidthHz, LinesPerSecond, LineLength);
        byte[] quiet = Line();
        byte[] signal = Line(944, 1344);
        long line = 0;
        for (int i = 0; i < 90; i++)
        {
            survey.AddLine(line++, quiet);
        }

        for (int i = 0; i < 60; i++)
        {
            survey.AddLine(line++, signal);
        }

        for (int i = 0; i < 60; i++)
        {
            survey.AddLine(line++, quiet);
        }

        long skipped = survey.SkippedForBudget;
        Settle(survey);

        Captures().Should().BeEmpty();
        skipped.Should().BeGreaterThan(0, "a capture that cannot be filled is counted, not faked");
    }

    private static FrameQuality Quality(string mode) =>
        new(mode, FrameBytes: 118, CorrectedBytes: 0, CrcValid: true,
            HeaderType: M0LTE.Il2p.Il2pHeaderType.Type1);

    private static byte[] Ax25Frame(string from, string to)
    {
        var frame = new byte[20];
        Write(frame, 0, to, last: false);
        Write(frame, 7, from, last: true);
        frame[14] = 0x03;
        frame[15] = 0xF0;
        return frame;

        static void Write(byte[] frame, int at, string call, bool last)
        {
            for (int n = 0; n < 6; n++)
            {
                frame[at + n] = (byte)((n < call.Length ? call[n] : ' ') << 1);
            }

            frame[at + 6] = (byte)(0x60 | (last ? 1 : 0));
        }
    }
}
