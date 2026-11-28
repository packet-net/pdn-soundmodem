using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The instrument hazard this fixes: <c>sm-ota sim</c> invoked with a mistyped or
/// not-yet-supported flag (the roadmap's own example at the time is <c>--impulse</c> against a
/// build that predates it) used to be silently accepted, run every requested burst anyway with the flag's
/// effect simply missing, and report numbers for the wrong experiment as if they were the one
/// asked for. <see cref="SimCommand.Run"/> now calls <c>Args.RejectUnknown</c> before the burst
/// loop starts, so a bogus flag fails loudly instead.
/// </summary>
public class SimCommandStrictFlagsTests
{
    [Fact]
    public void Sim_With_A_Bogus_Flag_Throws_Naming_It_And_Runs_No_Bursts()
    {
        // Not quiet, so the command says what it is doing: its first line is written after the flags
        // are checked and before the first burst, and a point line is written after each point.
        // A rejection that comes first leaves neither, whatever the burst count.
        string[] argv =
        [
            "--mode", "bpsk300", "--snr", "20", "--bursts", "100000",
            "--impulsee", "220", // typo'd flag: sim never reads "impulsee"
        ];

        TextWriter original = Console.Error;
        var said = new StringWriter();
        Console.SetError(said);
        try
        {
            Action act = () => SimCommand.Run(argv);
            act.Should().Throw<ArgumentException>()
                .WithMessage("*--impulsee*", "the message must name the exact flag that was unread")
                .Which.Message.Should().Contain("sim", "the message should name the command too");
        }
        finally
        {
            Console.SetError(original);
        }

        said.ToString().Should().NotContain("sim bpsk300", "the run announces itself only once its flags are accepted")
            .And.NotContain(" ok ", "no point, and so no burst, was run before the rejection");
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
