using AwesomeAssertions;
using Packet.SoundModem.OfdmFmTool;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class VersionTests
{
    [Fact]
    public void The_start_up_line_is_one_plain_line_naming_the_tool_and_the_modem_library_as_built()
    {
        string line = Versions.Line();

        line.Should().MatchRegex(@"^pdn-ofdm \S+, M0LTE\.OfdmFm \d+\.\d+\.\d+\S*$");
        line.Should().NotContain("+", "the build's commit is not part of it");
        string library = typeof(M0LTE.OfdmFm.Station).Assembly.GetName().Version!.ToString(3);
        line.Should().Contain($"M0LTE.OfdmFm {library}");
    }
}
