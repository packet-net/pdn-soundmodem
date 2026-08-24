using Packet.SoundModem.Daemon;
using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// Turning a proposal into the configuration document that acts on it.
/// </summary>
/// <remarks>
/// The property worth pinning is that there is only ever one door. A proposal produces a whole
/// configuration and nothing else, so acting on it is an ordinary POST to <c>/api/config</c> and
/// is validated, refused and made ephemeral by exactly the same code as an operator's own edit.
/// A second apply path would be a second set of rules to keep in step, and the first set is the
/// one carrying the safety property.
/// </remarks>
public class ProposedConfigTests
{
    private const string Running = """
        {
          "device": "flex:discover",
          "modems": [
            { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300, "port": 8101 },
            { "subChannel": 2, "mode": "bpsk300", "rfFrequency": 7051600, "port": 8102 }
          ],
          "survey": { "path": "/var/lib/pdn-soundmodem/survey", "propose": true }
        }
        """;

    [Fact]
    public void A_Proposal_Becomes_A_Configuration_The_Api_Would_Accept()
    {
        string? amended = ProposedConfig.Amend(Running, Proposal(), out string why);

        why.Should().BeEmpty();
        amended.Should().NotBeNull();
        ConfigApi.Validate(amended!).Should().BeNull(
            "a proposal the station cannot start on is not a proposal");
        amended.Should().Contain("\"afsk300\"").And.Contain("7050570");
    }

    [Fact]
    public void The_New_Modem_Takes_The_Lowest_Free_Sub_Channel_And_Leaves_The_Rest_Alone()
    {
        // 0 and 2 are in use, so 1 is the answer - and the two entries already there must come
        // out byte-identical, because this runs against a station carrying traffic.
        string amended = ProposedConfig.Amend(Running, Proposal(), out _)!;

        amended.Should().Contain("\"subChannel\": 1");
        amended.Should().Contain("afsk300-il2pc").And.Contain("7050300")
            .And.Contain("bpsk300").And.Contain("7051600");
        amended.Should().Contain("flex:discover", "nothing outside the modem list is touched");
    }

    [Fact]
    public void A_Framing_Change_Is_Also_An_Addition_Rather_Than_A_Rewrite()
    {
        // The modem already on that frequency is reading somebody. Changing its framing to catch
        // a station it cannot read would drop the stations it can, so both kinds of proposal come
        // out as a new entry - which is what the GB7BWR-2 finding of 2026-08-03 asked for and
        // what PD4R-12 got on 2026-08-24.
        string amended = ProposedConfig.Amend(
            Running, Proposal() with { Kind = ProposalKind.FramingChange, Conflicts = [0] },
            out _)!;

        amended.Should().Contain("afsk300-il2pc", "modem 0 keeps the framing it can read");
        amended.Should().Contain("\"subChannel\": 1");
        ConfigApi.Validate(amended).Should().BeNull();
    }

    [Fact]
    public void A_Station_Without_A_Dial_Is_Told_Why_It_Cannot_Be_Acted_On()
    {
        // An audio centre is not a frequency to configure. Saying so beats writing a modem entry
        // that would land wherever the dial happens to be that day.
        ProposedConfig.Amend(Running, Proposal() with { RfFrequencyHz = null }, out string why)
            .Should().BeNull();

        why.Should().Contain("audio frequencies only").And.Contain("dialFrequency");
    }

    [Fact]
    public void A_Configuration_With_Nowhere_To_Add_A_Modem_Is_Refused_Rather_Than_Corrupted()
    {
        ProposedConfig.Amend("{ \"device\": \"alsa:default\" }", Proposal(), out string why)
            .Should().BeNull();
        why.Should().Contain("modems");

        ProposedConfig.Amend("not json at all", Proposal(), out string broken).Should().BeNull();
        broken.Should().Contain("will not parse");
    }

    [Fact]
    public void Every_Sub_Channel_Taken_Is_A_Refusal_With_The_Reason()
    {
        // A KISS nibble addresses 0-15 and no more, which is arithmetic rather than a policy
        // this code gets to choose.
        var modems = new System.Text.StringBuilder();
        for (int sub = 0; sub <= ProposedConfig.MaxSubChannel; sub++)
        {
            modems.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"{(sub == 0 ? "" : ",")}{{\"subChannel\":{sub},\"mode\":\"afsk1200\"}}");
        }

        ProposedConfig.Amend($"{{\"modems\":[{modems}]}}", Proposal(), out string why)
            .Should().BeNull();
        why.Should().Contain("0-15").And.Contain("has to come out");
    }

    private static ModemProposal Proposal() => new(
        Mode: "afsk300",
        AudioCentreHz: 1120,
        RfFrequencyHz: 7050570,
        Kind: ProposalKind.NewModem,
        Captures: 34,
        Frames: 34,
        Stations: ["PD4R-12"],
        MeanSnrDb: 19.4,
        FirstHeard: new DateTimeOffset(2026, 8, 6, 16, 20, 4, TimeSpan.Zero),
        LastHeard: new DateTimeOffset(2026, 8, 24, 15, 22, 42, TimeSpan.Zero),
        Conflicts: []);
}
