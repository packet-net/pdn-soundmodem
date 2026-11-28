using AwesomeAssertions;
using Packet.SoundModem.OfdmFmTool;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class CommandLineTests
{
    [Fact]
    public void Listen_without_a_ptt_is_receive_only_and_needs_no_address()
    {
        ParseResult result = CommandLine.Parse(["listen", "--device", "pipe:a,b,48000", "--call", "g4abc-1"]);

        result.Error.Should().BeNull();
        ToolOptions options = result.Options!;
        options.Verb.Should().Be(Verb.Listen);
        options.Ptt.Should().BeNull();
        options.Call.Should().Be("G4ABC-1", "the call is upper-cased as it goes on the air");
        options.To.Should().BeNull();
        options.Detail.Should().BeFalse();
    }

    [Fact]
    public void Send_joins_the_text_words_and_upper_cases_the_address()
    {
        ParseResult result = CommandLine.Parse(["send", "--device", "d", "--ptt", "serial:/dev/ttyUSB0", "--call", "G4ABC", "--to", "m0xyz-1", "--detail", "hello", "there", "--log", "x.log"]);

        result.Error.Should().BeNull();
        result.Options!.Text.Should().Be("hello there");
        result.Options.LogFile.Should().Be("x.log", "an option may come after the text words");
        result.Options.To.Should().Be("M0XYZ-1");
        result.Options.Detail.Should().BeTrue();
    }

    [Fact]
    public void Options_after_the_verb_and_before_the_text_are_read_as_options()
    {
        ParseResult result = CommandLine.Parse(["send", "--device", "d", "--ptt", "serial:x", "--call", "G4ABC", "--log", "x.log", "--to", "M0XYZ", "hi"]);

        result.Options!.LogFile.Should().Be("x.log");
        result.Options.Text.Should().Be("hi");
    }

    [Fact]
    public void Ping_takes_the_documented_defaults()
    {
        ParseResult result = CommandLine.Parse(["ping", "--device", "d", "--ptt", "serial:x", "--call", "G4ABC", "--to", "M0XYZ"]);

        result.Options!.Count.Should().Be(5);
        result.Options.Size.Should().Be(32);
    }

    [Fact]
    public void Ping_reads_count_size_and_the_recording()
    {
        ParseResult result = CommandLine.Parse(["ping", "--device", "d", "--ptt", "serial:x", "--call", "G4ABC", "--to", "M0XYZ", "--count", "9", "--size", "100", "--record", "a.wav"]);

        result.Options!.Count.Should().Be(9);
        result.Options.Size.Should().Be(100);
        result.Options.RecordFile.Should().Be("a.wav");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("help")]
    public void Help_is_asked_for(string word) => CommandLine.Parse([word]).Help.Should().BeTrue();

    [Fact]
    public void No_arguments_shows_the_help() => CommandLine.Parse([]).Help.Should().BeTrue();

    [Theory]
    [InlineData("listen --call G4ABC", "--device is needed")]
    [InlineData("listen --device d", "--call is needed")]
    [InlineData("send --device d --call G4ABC --to M0XYZ hi", "--ptt is needed")]
    [InlineData("send --device d --ptt serial:x --call G4ABC hi", "--to is needed")]
    [InlineData("send --device d --ptt serial:x --call G4ABC --to M0XYZ", "no text to send")]
    [InlineData("ping --device d --ptt serial:x --call G4ABC", "--to is needed")]
    [InlineData("listen --device d --call G4ABC --to M0XYZ", "--to does not apply to listen")]
    [InlineData("send --device d --ptt serial:x --call G4ABC --to M0XYZ --count 3 hi", "--count does not apply to send")]
    [InlineData("listen --device d --call G4ABC --frob", "unknown option --frob")]
    [InlineData("listen --device d --call G4ABC stray", "unexpected argument stray")]
    [InlineData("dance --device d", "unknown command dance")]
    [InlineData("listen --device", "--device needs a value")]
    [InlineData("listen --device d --device e --call G4ABC", "--device given twice")]
    [InlineData("listen --device d --call G4 ABC", "unexpected argument ABC")]
    [InlineData("listen --device d --call TOOLONGCALLSIGN-1234", "--call must be 1 to 16 characters")]
    [InlineData("listen --device d --call G4*BC", "--call must be 1 to 16 characters")]
    [InlineData("ping --device d --ptt serial:x --call G4ABC --to M0XYZ --count 0", "--count needs a whole number from 1 to 1000")]
    [InlineData("ping --device d --ptt serial:x --call G4ABC --to M0XYZ --size 2", "--size needs a whole number from 3 to 4000")]
    [InlineData("ping --device d --ptt serial:x --call G4ABC --to M0XYZ --size x", "--size needs a whole number")]
    public void A_mistake_is_one_line_saying_what_is_wrong(string line, string expectedStart)
    {
        ParseResult result = CommandLine.Parse(line.Split(' '));

        result.Options.Should().BeNull();
        result.Error.Should().StartWith(expectedStart);
        result.Error.Should().NotContain("\n");
        result.Error.Should().MatchRegex("^[\\x20-\\x7e]+$", "plain ASCII");
    }

    [Fact]
    public void The_usage_text_is_plain_ascii_and_names_every_option()
    {
        Usage.Text.Should().MatchRegex("^[\\x20-\\x7e\\n]+$");
        foreach (string option in new[] { "--device", "--ptt", "--call", "--to", "--count", "--size", "--detail", "--log", "--record" })
        {
            Usage.Text.Should().Contain(option);
        }
    }
}
