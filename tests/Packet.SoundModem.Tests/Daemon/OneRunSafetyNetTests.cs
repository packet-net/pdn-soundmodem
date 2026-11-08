using System.Diagnostics;
using System.Text;
using Packet.SoundModem.Daemon;

namespace Packet.SoundModem.Tests.Daemon;

/// <summary>
/// A one-run change that does not start must not leave the daemon down: exit 2 is final under the
/// shipped unit, so a start from a one-run file that fails exits 1 and systemd tries the saved file.
/// </summary>
public sealed class OneRunSafetyNetTests : IDisposable
{
    private const string Saved = """
        {
          "bind": "127.0.0.1",
          "waterfall": { "port": 18099 },
          "monitor": {
            "directory": "http://127.0.0.1:1/instances",
            "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ]
          }
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("pdnsm-oneshot").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Exit, string Output, string Pending) Run(string? pending)
    {
        string config = Path.Combine(_dir, "soundmodem.json");
        string state = Path.Combine(_dir, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(config, pending is null ? Saved.Replace("7050300", "").Replace("\"rfFrequency\": ", "\"frequency\": 1500") : Saved);
        string pendingPath = Path.Combine(state, "pending-config.json");
        if (pending is not null)
        {
            File.WriteAllText(pendingPath, pending);
        }

        var info = new ProcessStartInfo(UsageTests.DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "pdn-soundmodem.dll"));
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(config);
        info.Environment["STATE_DIRECTORY"] = state;
        info.Environment["INVOCATION_ID"] = "one-run-safety-net-tests";

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"the daemon did not exit:\n{output}");
        }

        process.WaitForExit();
        return (process.ExitCode, output.ToString(), pendingPath);
    }

    [Fact]
    public void A_One_Run_File_That_Fails_To_Start_Exits_1_And_Says_It_Is_Going_Back()
    {
        // Loads, and is then refused by the site's own start-up: no radio frequency.
        (int exit, string output, string pending) = Run(Saved.Replace("\"rfFrequency\": 7050300", "\"frequency\": 1500"));

        exit.Should().Be(1, output);
        output.Should().Contain("the one-run change did not start; going back to the saved file");
        File.Exists(pending).Should().BeFalse("it was consumed before it was tried");
    }

    [Fact]
    public void A_One_Run_File_That_Does_Not_Load_Exits_1_Too()
    {
        (int exit, string output, _) = Run("{ not json");

        exit.Should().Be(1, output);
        output.Should().Contain("going back to the saved file");
    }

    [Fact]
    public void A_Saved_File_That_Is_Wrong_Still_Exits_2_So_It_Is_Not_Retried_For_Ever()
    {
        // No one-run file: the saved file itself (written wrong by Run) is refused, finally.
        (int exit, string output, _) = Run(pending: null);

        exit.Should().Be(2, output);
        output.Should().NotContain("going back to the saved file");
    }
}
