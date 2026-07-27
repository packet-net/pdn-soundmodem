using System.Text.Json;
using AwesomeAssertions;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The two documents a pass is made of: the schedule (what was asked for) and the manifest (what
/// happened). Their whole purpose is to make a score interpretable months later, so the tests are
/// about the facts that are impossible to reconstruct once lost.
/// </summary>
public sealed class CampaignFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("campaign").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    private static CampaignSchedule Schedule() => new(
        Name: "wn6-awgn",
        OffsetHz: 2000,
        Bursts:
        [
            new CampaignBurst(6, Ms110dInterleaverKind.Short, Blocks: 1, Seed: 1, SnrDb: 15),
            new CampaignBurst(6, Ms110dInterleaverKind.Short, Blocks: 1, Seed: 2, SnrDb: 9),
            new CampaignBurst(2, Ms110dInterleaverKind.Long, Blocks: 2, Seed: 3, SnrDb: 0,
                Channel: LadderChannel.Poor),
        ],
        Notes: "the point of the campaign");

    [Fact]
    public void A_schedule_survives_a_round_trip_including_its_enums()
    {
        string path = Path("schedule.json");
        CampaignFiles.Save(path, Schedule());

        CampaignSchedule back = CampaignFiles.Load<CampaignSchedule>(path);

        // Records compare by value, but Bursts is a list and lists compare by reference — so
        // `back.Should().Be(original)` would fail on two identical schedules. Compare the parts.
        back.Name.Should().Be("wn6-awgn");
        back.OffsetHz.Should().Be(2000);
        back.Notes.Should().Be("the point of the campaign");
        back.Bursts.Should().Equal(Schedule().Bursts);

        File.ReadAllText(path).Should().Contain("\"Poor\"")
            .And.Contain("\"Long\"", "enums are written as names — a schedule is read by people");
    }

    [Fact]
    public void A_schedule_row_regenerates_exactly_the_bits_the_ladder_rendered()
    {
        // The row has to be enough on its own. If it is not, a manifest is a description of a
        // transmission nobody can reproduce.
        IReadOnlyList<LadderPoint> plan = LadderPass.Plan(6, [9], 1, firstSeed: 77);
        IReadOnlyList<RenderedPoint> rendered = new LadderPass(new LadderPassOptions
        {
            OutputRate = 48000,
        }).Render(plan);

        var row = new CampaignBurst(
            plan[0].WaveformNumber, plan[0].Interleaver, plan[0].Blocks, plan[0].Seed,
            plan[0].SnrDb, plan[0].Channel);

        row.Reference().PayloadBits.Should().Equal(rendered[0].Reference.PayloadBits);
        row.Reference().Block(0).ToArray().Should().Equal(rendered[0].Reference.Block(0).ToArray());
    }

    [Fact]
    public void A_manifest_is_told_from_a_schedule_by_inspection_not_by_a_failed_parse()
    {
        // System.Text.Json does not object to missing members, so deserialising a bare schedule
        // into a manifest SUCCEEDS and yields one whose Schedule is null. A try/catch fallback
        // would never fire, and the caller would score an empty pass as though nothing had been
        // transmitted — a silent wrong answer, which is the only kind worth writing a test for.
        string schedulePath = Path("bare.json");
        CampaignFiles.Save(schedulePath, Schedule());

        CampaignManifest mistaken = CampaignFiles.Load<CampaignManifest>(schedulePath);
        mistaken.Schedule.Should().BeNull("this is exactly the trap — it parsed, and it is empty");

        (CampaignSchedule schedule, IReadOnlyList<double>? starts, string revision) =
            CampaignFiles.LoadScheduleOrManifest(schedulePath);

        schedule.Bursts.Should().HaveCount(3, "the loader must recognise a bare schedule");
        starts.Should().BeNull("a schedule does not know where the bursts ended up");
        revision.Should().Be("—");
    }

    [Fact]
    public void A_manifest_carries_the_positions_and_the_revision()
    {
        string path = Path("pass.manifest.json");
        CampaignFiles.Save(path, new CampaignManifest(
            Schedule: Schedule(),
            ModemRevision: "abc123def456-dirty",
            WrittenUtc: DateTimeOffset.UnixEpoch,
            Radio: "10.45.0.76",
            FrequencyMHz: "18.106500",
            RfPower: 15,
            PassGain: 2.1f,
            DialCorrectionHz: 129,
            CapturePath: "pass.wav",
            CaptureSha256: "deadbeef",
            BurstStartSeconds: [9.1, 20.4, 31.6],
            SupplyNote: "large battery"));

        (CampaignSchedule schedule, IReadOnlyList<double>? starts, string revision) =
            CampaignFiles.LoadScheduleOrManifest(path);

        schedule.Name.Should().Be("wn6-awgn");
        starts.Should().Equal(9.1, 20.4, 31.6);
        revision.Should().Be("abc123def456-dirty");
    }

    [Fact]
    public void A_document_with_no_bursts_is_refused_rather_than_scored_as_an_empty_pass()
    {
        string path = Path("empty.json");
        CampaignFiles.Save(path, new CampaignSchedule("nothing", 2000, []));

        Action load = () => CampaignFiles.LoadScheduleOrManifest(path);

        load.Should().Throw<InvalidDataException>(
            "an empty pass and a pass where everything was missed must not look the same");
    }

    [Fact]
    public void Something_that_is_not_json_at_all_is_refused()
    {
        string path = Path("junk.json");
        File.WriteAllText(path, "this is not json");

        Action load = () => CampaignFiles.LoadScheduleOrManifest(path);

        load.Should().Throw<JsonException>();
    }

    [Fact]
    public void The_binary_knows_which_revision_built_it()
    {
        // A score without a revision is uninterpretable — the demodulator changes daily — and
        // asking an operator to write it down by hand is asking for it to be wrong.
        string revision = CampaignFiles.ModemRevision();

        revision.Should().NotBeNullOrWhiteSpace();
        revision.Should().NotBe("unknown",
            "the build stamps the repository revision; if this fails, the MSBuild target in "
            + "Packet.SoundModem.Ota.csproj has stopped working and every manifest from now on "
            + "would silently say 'unknown'");
        // TrimEnd(char[]) would also eat a trailing hex digit that happened to be d, e, i, r,
        // t or y — strip the suffix, not the characters in it.
        string sha = revision.EndsWith("-dirty", StringComparison.Ordinal)
            ? revision[..^"-dirty".Length]
            : revision;
        sha.Should().MatchRegex("^[0-9a-f]{12}$",
            "the stamp is a 12-character abbreviated hash, optionally -dirty");
    }

    [Fact]
    public void The_scorer_schedule_keeps_the_transmit_order_and_the_known_positions()
    {
        List<ScheduledBurst> bursts = Schedule().ToScorerSchedule([9.1, 20.4, 31.6]);

        bursts.Select(b => b.ExpectedSeconds).Should().Equal(9.1, 20.4, 31.6);
        bursts[2].Reference.Settings.WaveformNumber.Should().Be(2);

        List<ScheduledBurst> unknown = Schedule().ToScorerSchedule();
        unknown.Should().OnlyContain(b => b.ExpectedSeconds < 0,
            "without positions the scorer must match by order alone, not invent times");
    }
}
