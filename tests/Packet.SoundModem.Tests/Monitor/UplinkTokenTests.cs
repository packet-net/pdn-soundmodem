using AwesomeAssertions;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Monitor;

/// <summary>
/// <c>pdn-soundmodem --uplink-token CALLSIGN</c>: what it prints, and what it does instead when it
/// is not told which station the token is for.
/// </summary>
/// <remarks>
/// What it prints is pasted into two config files by hand - one line into the station's
/// <c>publish</c> block and one entry into the site's <c>monitor.uplinks</c> - so it is an
/// interface and is pinned like one. Driven through the <see cref="TextWriter"/> overload rather
/// than by redirecting the console, because that is process-wide and these run beside everything
/// else.
/// </remarks>
public class UplinkTokenTests
{
    [Fact]
    public void The_Printed_Entry_Is_For_The_Station_It_Was_Asked_About()
    {
        (string printed, string errors, int exit) = Print("gb7rdg-2");

        exit.Should().Be(0);
        errors.Should().BeEmpty();

        // Upper-cased, with the slug that callsign gives, because the entry is pasted in as it
        // stands. The example this used to print was GB7RDG-2, which is a real station on the
        // live site rather than a placeholder, so the tool read as though it were suggesting it.
        printed.Should().Contain("\"callsign\": \"GB7RDG-2\"")
            .And.Contain("\"slug\": \"gb7rdg-2\"")
            .And.Contain("/r/gb7rdg-2/", "the operator is told where that station's page will be");
    }

    [Fact]
    public void The_Token_And_The_Hash_Printed_With_It_Are_Halves_Of_One_Token()
    {
        // The whole point of printing both at once: the hash stays on the site and the token goes
        // to the operator, and nothing afterwards can check that they were ever a pair.
        (string printed, _, _) = Print("M0LTE-7");

        string token = Between(printed, "\"token\": \"");
        string hash = Between(printed, "\"tokenSha256\": \"");

        token.Should().StartWith("pdnsm_").And.HaveLength("pdnsm_".Length + 43);
        UplinkToken.Hash(token).Should().Be(hash);
        printed.Should().Contain("never the token", "so the site owner keeps the right half");
    }

    [Fact]
    public void Without_A_Callsign_It_Says_So_Rather_Than_Minting_One_For_Nobody()
    {
        foreach (string? missing in (string?[])[null, "", "   ", "not a callsign"])
        {
            (string printed, string errors, int exit) = Print(missing);

            exit.Should().Be(2, "the token is issued against a callsign, so there is nothing to do");
            printed.Should().BeEmpty("nothing was minted, so there is nothing anybody has to keep");
            errors.Should().Contain("--uplink-token")
                .And.Contain("GB7RDG-2", "the sentence shows what to type");
        }
    }

    /// <summary>Runs the printer somewhere a test can read both of its streams.</summary>
    private static (string Printed, string Errors, int Exit) Print(string? callsign)
    {
        var printed = new StringWriter();
        var errors = new StringWriter();
        int exit = UplinkToken.Print(callsign, printed, errors);
        return (printed.ToString(), errors.ToString(), exit);
    }

    /// <summary>What one of the printed JSON lines says, between its quotes.</summary>
    private static string Between(string printed, string after)
    {
        int start = printed.IndexOf(after, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "\"{0}\" should have been printed", after);
        start += after.Length;
        return printed[start..printed.IndexOf('"', start)];
    }
}
