using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// <c>Args</c>'s touch-tracking and <c>RejectUnknown</c>: the instrument-hazard fix. Before this,
/// a mistyped or not-yet-supported flag (e.g. <c>--impulse</c> against a build that predates it)
/// was silently dropped by every sm-ota subcommand, which then ran with its defaults and reported
/// numbers for the wrong experiment. <c>RejectUnknown</c> compares the flags the caller actually
/// passed against the flags the command's own Req/Str/Int/Dbl/Has calls touched, so the "known
/// flags" set falls straight out of the command's real code path - nothing to hand-maintain and
/// nothing that can drift out of sync with it.
/// </summary>
public class ArgsTests
{
    [Fact]
    public void RejectUnknown_Names_The_Flag_The_Caller_Passed_But_The_Command_Never_Read()
    {
        global::Args a = global::Args.Parse(["--mode", "freedv-datac1", "--impulse", "220"])!;
        _ = a.Str("mode", null); // the command reads --mode; --impulse is never touched

        Action act = () => a.RejectUnknown("sim");

        act.Should().Throw<ArgumentException>().WithMessage("*--impulse*");
    }

    [Fact]
    public void RejectUnknown_Names_The_Command_In_Its_Message()
    {
        global::Args a = global::Args.Parse(["--bogus", "1"])!;

        Action act = () => a.RejectUnknown("sim");

        act.Should().Throw<ArgumentException>().WithMessage("*sim*");
    }

    [Fact]
    public void RejectUnknown_Lists_Every_Flag_The_Command_Did_Read_As_The_Known_Set()
    {
        global::Args a = global::Args.Parse(["--mode", "freedv-datac1", "--snr", "0,3", "--bogus", "1"])!;
        _ = a.Req("mode");
        _ = a.Req("snr");
        _ = a.Has("quiet"); // recognised, not passed this run - still counts as known

        Action act = () => a.RejectUnknown("sim");

        string message = act.Should().Throw<ArgumentException>().Which.Message;
        message.Should().Contain("--mode");
        message.Should().Contain("--snr");
        message.Should().Contain("--quiet");
    }

    [Fact]
    public void RejectUnknown_Names_Every_Unrecognised_Flag_When_There_Is_More_Than_One()
    {
        global::Args a = global::Args.Parse(["--mode", "freedv-datac1", "--impulse", "220", "--fooo", "1"])!;
        _ = a.Str("mode", null);

        Action act = () => a.RejectUnknown("sim");

        string message = act.Should().Throw<ArgumentException>().Which.Message;
        message.Should().Contain("--fooo");
        message.Should().Contain("--impulse");
    }

    [Fact]
    public void RejectUnknown_Does_Not_Throw_When_Every_Passed_Flag_Was_Read()
    {
        global::Args a = global::Args.Parse(
            ["--mode", "freedv-datac1", "--snr", "0,3", "--bursts", "10", "--quiet"])!;
        _ = a.Req("mode");
        _ = a.Req("snr");
        _ = a.Int("bursts", 100);
        _ = a.Has("quiet");

        Action act = () => a.RejectUnknown("sim");

        act.Should().NotThrow();
    }

    [Fact]
    public void Has_Counts_As_A_Read_Even_When_The_Flag_Is_A_Bare_Switch()
    {
        // --quiet carries no value; Args.Parse stores it as "true". Reading it via Has (never Str
        // or Int) must still be enough to keep RejectUnknown quiet about it.
        global::Args a = global::Args.Parse(["--quiet"])!;

        bool quiet = a.Has("quiet");

        quiet.Should().BeTrue();
        Action act = () => a.RejectUnknown("sim");
        act.Should().NotThrow();
    }

    [Fact]
    public void Str_With_A_Null_Default_Counts_As_A_Read_Even_When_The_Flag_Is_Absent()
    {
        // The "optional value, absent means null" idiom (SimCommand's --detector, --detector2,
        // --centre, ...): Str(k, null) is called unconditionally to decide whether the flag was
        // given at all, and must count as a read whether or not it was.
        global::Args a = global::Args.Parse(["--mode", "freedv-datac1"])!;
        _ = a.Str("mode", null);
        string? detector = a.Str("detector", null); // not passed; still touches "detector"

        detector.Should().BeNull();
        Action act = () => a.RejectUnknown("sim");
        act.Should().NotThrow();
    }

    [Fact]
    public void Str_With_A_Null_Default_Protects_The_Flag_When_It_Was_Passed()
    {
        global::Args a = global::Args.Parse(["--mode", "freedv-datac1", "--detector", "mlse"])!;
        _ = a.Str("mode", null);
        string? detector = a.Str("detector", null);

        detector.Should().Be("mlse");
        Action act = () => a.RejectUnknown("sim");
        act.Should().NotThrow();
    }

    [Fact]
    public void An_Optional_Flag_The_Caller_Never_Passed_Does_Not_Trip_RejectUnknown()
    {
        // A flag being "known" to the command (its accessor gets called every run) is
        // independent of whether the caller happened to pass it this time - only flags actually
        // present in argv can ever be "unknown".
        global::Args a = global::Args.Parse(["--mode", "freedv-datac1"])!;
        _ = a.Req("mode");
        _ = a.Int("bursts", 100); // never passed, default used

        Action act = () => a.RejectUnknown("sim");

        act.Should().NotThrow();
    }

    [Fact]
    public void Req_Int_And_Dbl_Each_Count_As_Reads()
    {
        global::Args a = global::Args.Parse(["--wn", "6", "--gap", "3.5", "--name", "wn6"])!;
        _ = a.Req("name");
        _ = a.Int("wn", 0);
        _ = a.Dbl("gap", 0);

        Action act = () => a.RejectUnknown("ladder");

        act.Should().NotThrow();
    }
}
