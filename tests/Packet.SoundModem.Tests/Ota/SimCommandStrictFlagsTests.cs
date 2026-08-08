using System.Diagnostics;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The instrument hazard this fixes: <c>sm-ota sim</c> invoked with a mistyped or
/// not-yet-supported flag (the roadmap's own example is <c>--impulse</c> against a build that
/// predates it) used to be silently accepted, run every requested burst anyway with the flag's
/// effect simply missing, and report numbers for the wrong experiment as if they were the one
/// asked for. <see cref="SimCommand.Run"/> now calls <c>Args.RejectUnknown</c> before the burst
/// loop starts, so a bogus flag fails loudly - and fast - instead.
/// </summary>
public class SimCommandStrictFlagsTests
{
    [Fact]
    public void Sim_With_A_Bogus_Flag_Throws_Naming_It_And_Runs_No_Bursts()
    {
        // A burst count and mode that would take clearly measurable wall time if the loop ever
        // ran, so "the exception arrives near-instantly" is real evidence no burst was rendered,
        // channel-injected or decoded - not just that the process eventually gave up.
        string[] argv =
        [
            "--mode", "bpsk300", "--snr", "20", "--bursts", "100000", "--quiet",
            "--impulsee", "220", // typo'd flag: sim never reads "impulsee"
        ];

        var stopwatch = Stopwatch.StartNew();
        Action act = () => SimCommand.Run(argv);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*--impulsee*", "the message must name the exact flag that was unread")
            .Which.Message.Should().Contain("sim", "the message should name the command too");
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "100000 bpsk300 bursts take far longer than this - a fast failure is proof none ran");
    }

    [Fact]
    public void Sim_With_A_Bogus_Flag_Never_Writes_The_Csv_Output()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("sim-strict-flags-");
        try
        {
            string csvPath = Path.Combine(dir.FullName, "out.csv");
            string[] argv =
            [
                "--mode", "bpsk300", "--snr", "20", "--bursts", "5", "--quiet",
                "--csv", csvPath, "--not-a-real-flag", "1",
            ];

            Action act = () => SimCommand.Run(argv);

            act.Should().Throw<ArgumentException>().WithMessage("*--not-a-real-flag*");
            File.Exists(csvPath).Should().BeFalse(
                "the run must fail before the point loop starts, well before any --csv write");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Sim_With_Only_Recognised_Flags_Still_Runs()
    {
        // The counterpart to the two tests above: RejectUnknown must not become a new way to
        // break a legitimate invocation. One burst at a friendly SNR is enough to prove the run
        // reaches completion and returns success.
        string[] argv = ["--mode", "bpsk300", "--snr", "20", "--bursts", "1", "--quiet"];

        int code = SimCommand.Run(argv);

        code.Should().Be(0);
    }
}
