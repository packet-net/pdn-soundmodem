using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

/// <summary>
/// <see cref="DaemonVersion"/> reads the test assembly's own build metadata, which a hermetic
/// build always carries but whose exact value changes on every commit. So every assertion here
/// is about shape, never about a literal version or commit (#480).
/// </summary>
public class DaemonVersionTests
{
    [Fact]
    public void The_Version_Is_Never_Empty()
    {
        DaemonVersion.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void A_Missing_Commit_Reads_As_Null_Not_As_An_Empty_String()
    {
        // Either the SDK embedded one, or it did not; there is no third shape on the wire.
        if (DaemonVersion.Commit is { } commit)
        {
            commit.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void A_Present_Commit_Is_The_Full_Forty_Character_Hex_Sha()
    {
        // This checkout is a real git repository, so a normal Release build embeds one; only a
        // build with no .git behind it would leave Commit null, which the test above covers.
        DaemonVersion.Commit.Should().MatchRegex(
            "^[0-9a-f]{40}$", "the .NET SDK embeds the full sha, not a shortened form");
    }

    [Fact]
    public void The_Bare_Unversioned_Default_Is_Never_Reported_As_A_Release()
    {
        // "dotnet test" never passes -p:Version (only packaging/build-deb.sh does), so the core
        // library this reads is always built with the default here - the exact case #480 was
        // filed over: a build nothing named a version for.
        DaemonVersion.Version.Should().Be(DaemonVersion.UnversionedDefault);
        DaemonVersion.IsRelease.Should().BeFalse();
    }

    [Fact]
    public void Describe_Says_Dev_Build_Plainly_Rather_Than_Reading_Like_A_Release()
    {
        string description = DaemonVersion.Describe();

        description.Should().Contain(DaemonVersion.Version)
            .And.Contain("dev build")
            .And.Contain("not a numbered release");
    }

    [Fact]
    public void Describe_Never_Says_Unknown_When_A_Real_Version_Was_Read()
    {
        // "unknown" is reserved for an assembly with no version metadata at all, which a normal
        // build of this repo never produces.
        DaemonVersion.Describe().Should().NotContain(DaemonVersion.Unknown);
    }

    [Fact]
    public void Describe_Includes_The_Commit_When_One_Was_Read()
    {
        if (DaemonVersion.Commit is { } commit)
        {
            DaemonVersion.Describe().Should().Contain(commit);
        }
    }

    [Fact]
    public void Describe_Is_Plain_Ascii()
    {
        // SourceTextTests pins this for string literals in the source; this pins the same rule
        // for the actual runtime output, which is what a journal ends up holding.
        DaemonVersion.Describe().Where(c => c > 127).Should().BeEmpty();
    }
}
