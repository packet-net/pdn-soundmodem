using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.SoundModem.OfdmFmTool;
using Packet.SoundModem.OfdmFmTool.Ax25;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class Ax25BehaviourTests
{
    private sealed class Recorder
    {
        public List<byte[]> Sent { get; } = [];

        public int Disconnects { get; private set; }

        public (long, long) Stats { get; set; } = (0, 0);

        public AnswerBehaviour Make() => new("M0XYZ-1", Sent.Add, () => Disconnects++, () => Stats);

        public string Text() => Encoding.ASCII.GetString(Sent.SelectMany(b => b).ToArray());
    }

    [Fact]
    public void On_connect_the_answering_side_sends_a_one_line_banner()
    {
        var r = new Recorder();
        r.Make().Connected();

        r.Text().Should().Be("pdn-ofdm M0XYZ-1\r");
    }

    [Fact]
    public void Each_line_is_echoed_whatever_the_line_end_and_however_it_is_split_across_frames()
    {
        var r = new Recorder();
        AnswerBehaviour answer = r.Make();

        answer.Data("hel"u8);
        answer.Data("lo\r\nsecond\nthird\r"u8);

        r.Text().Should().Be("hello\rsecond\rthird\r");
    }

    [Fact]
    public void SEND_n_is_answered_with_exactly_n_bytes_of_the_pattern()
    {
        var r = new Recorder();

        r.Make().Data("SEND 2000\r"u8);

        byte[] sent = r.Sent.Single();
        sent.Should().HaveCount(2000);
        sent.Should().Equal(TestPattern.Make(2000));
        sent.Where(b => b == '\r').Should().HaveCount(25);
        sent.Where(b => b != '\r').Should().OnlyContain(b => b >= '!' && b <= '~');
    }

    [Theory]
    [InlineData("SEND 0")]
    [InlineData("SEND x")]
    [InlineData("SEND 1000001")]
    [InlineData("SEND -5")]
    public void A_SEND_that_is_not_a_sensible_number_is_refused_in_one_line(string line)
    {
        var r = new Recorder();

        r.Make().Data(Encoding.ASCII.GetBytes(line + "\r"));

        r.Text().Should().StartWith("SEND needs a number").And.EndWith("\r");
        r.Sent.Should().ContainSingle();
    }

    [Fact]
    public void BYE_ends_the_link_and_nothing_is_echoed()
    {
        var r = new Recorder();

        r.Make().Data("bye\r"u8);

        r.Disconnects.Should().Be(1);
        r.Sent.Should().BeEmpty();
    }

    [Fact]
    public void STATS_says_how_many_frames_were_sent_and_how_many_of_them_again()
    {
        var r = new Recorder { Stats = (9, 2) };

        r.Make().Data("STATS\r"u8);

        r.Text().Should().Be("STATS 9 2\r");
    }

    [Fact]
    public void The_repeat_counter_counts_a_frame_as_new_when_its_number_is_the_next_in_turn_and_wraps_at_the_modulus()
    {
        var counter = new RepeatCounter();

        foreach (int n in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4 })
        {
            counter.Sent(n);
        }

        counter.Counts.Should().Be((13, 0), "the numbers wrap and are all new");

        // A loss: 2 is sent again with what followed it.
        foreach (int n in new[] { 2, 3, 4 })
        {
            counter.Sent(n);
        }

        counter.Counts.Should().Be((16, 3));
        counter.Sent(5);
        counter.Counts.Should().Be((17, 3), "the next new frame is still 5");
    }

    [Fact]
    public void A_get_is_timed_from_the_request_to_the_last_byte_on_the_clock_it_was_given()
    {
        var clock = new FakeTimeProvider();
        var get = new GetMeasurement(200, clock);
        get.Requested();

        clock.Advance(TimeSpan.FromSeconds(2));
        get.Data([.. "pdn-ofdm M0XYZ-1\r"u8.ToArray(), .. TestPattern.Make(100)]);
        get.GotAllBytes.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(2));
        get.Data(TestPattern.Make(100, offset: 100));
        clock.Advance(TimeSpan.FromSeconds(10));

        get.GotAllBytes.Should().BeTrue();
        get.Took.Should().Be(TimeSpan.FromSeconds(4), "the clock stops at the last byte, not at the end of the call");
        get.Result(5, 1).Should().Be("Received 200 bytes in 4.00 s, 0.40 kbit/s, 1 of 5 frames sent again");
    }

    [Fact]
    public void A_get_counts_bytes_that_are_not_the_pattern_and_keeps_what_follows_the_bytes_as_lines()
    {
        var clock = new FakeTimeProvider();
        var get = new GetMeasurement(10, clock);
        get.Requested();
        byte[] data = [.. "pdn-ofdm M0XYZ-1\r"u8.ToArray(), .. TestPattern.Make(10), .. "STATS 3 1\r"u8.ToArray()];
        data[19] = (byte)'~';

        IReadOnlyList<string> lines = get.Data(data);

        lines.Should().Equal("STATS 3 1");
        get.Damaged.Should().Be(1);
        get.Result(3, 1).Should().EndWith("1 bytes damaged");
    }

    [Fact]
    public void The_link_settings_are_the_agreed_ones()
    {
        LinkSettings.T1.Should().Be(TimeSpan.FromSeconds(30));
        LinkSettings.T2.Should().Be(TimeSpan.FromSeconds(1));
        LinkSettings.T3.Should().Be(TimeSpan.FromSeconds(120));
        LinkSettings.Retries.Should().Be(10);
        LinkSettings.Window.Should().Be(4);
        LinkSettings.Paclen.Should().Be(256);
        var options = LinkSettings.Options(new Packet.Core.Callsign("G4ABC", 1), _ => { });
        options.PreferExtendedConnect.Should().BeFalse("mod 8 for this slice");
        options.RestartT1OnTxComplete.Should().BeTrue();
        options.PreConnectXidNegotiatesSrej.Should().BeFalse();
    }

    [Fact]
    public void The_new_commands_are_read_from_the_command_line()
    {
        ParseResult answer = CommandLine.Parse(["answer", "--device", "d", "--ptt", "serial:x", "--call", "g4abc-1"]);
        ParseResult call = CommandLine.Parse(["call", "--device", "d", "--ptt", "serial:x", "--call", "G4ABC-1", "--to", "m0xyz-1", "--get", "2000"]);

        answer.Options!.Verb.Should().Be(Verb.Answer);
        call.Options!.Verb.Should().Be(Verb.Call);
        call.Options.Get.Should().Be(2000);
        call.Options.To.Should().Be("M0XYZ-1");
    }

    [Theory]
    [InlineData("answer --device d --call G4ABC-1", "--ptt is needed")]
    [InlineData("answer --device d --ptt serial:x --call G4ABC-1 --to M0XYZ", "--to does not apply")]
    [InlineData("call --device d --ptt serial:x --call G4ABC-1", "--to is needed")]
    [InlineData("call --device d --ptt serial:x --call G4ABC-1 --to M0XYZ-1 --get 0", "--get needs a whole number")]
    [InlineData("call --device d --ptt serial:x --call G4ABC-1 --to M0XYZ-99", "--to must be a callsign")]
    [InlineData("answer --device d --ptt serial:x --call TOOLONGCALL", "--call must be")]
    [InlineData("ping --device d --ptt serial:x --call G4ABC-1 --to M0XYZ-1 --get 5", "--get does not apply")]
    public void Mistakes_in_the_new_commands_are_one_line(string line, string expectedStart)
    {
        ParseResult result = CommandLine.Parse(line.Split(' '));

        result.Options.Should().BeNull();
        result.Error.Should().StartWith(expectedStart);
    }
}
