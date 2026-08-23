namespace Packet.SoundModem.Tests;

/// <summary>
/// A directory for one test to stage files in: a stable parent so a person can go and find what a
/// failure left behind, and a unique child so that no two runs ever meet.
/// </summary>
/// <remarks>
/// <para>Both halves are load-bearing, for different reasons, and a fix that only does one of them
/// leaves half the fault standing.</para>
/// <para>The parent is stable so there is a predictable place to look, and it carries the user name
/// because a box can host more than one self-hosted runner under different Unix accounts. A single
/// shared directory belongs to whichever account created it first, and every later run under a
/// different account then gets "Permission denied" writing a file it has every right to write. That
/// is not flaky, it is permanent for the loser until somebody clears the temp directory, and it
/// failed the v0.43.0 release build with the change under test entirely innocent (issue #349).</para>
/// <para>The child is unique per run because two runs under the same account collide too, and that
/// collision is the nastier one. Fixed filenames mean one process reads a file another is halfway
/// through rewriting, so instead of a permission error you get a truncated stream or a half-written
/// header, reported as though the instrument had failed to decode. A wrong answer is worse than a
/// hard error, because it looks like a result.</para>
/// <para>A passing test takes its staging away with it. A failing one leaves it behind on purpose:
/// the files the code under test was given, and the files it produced, are exactly what you want in
/// your hands when an instrument says something surprising. An empty directory tells nobody
/// anything, so that goes either way.</para>
/// </remarks>
public sealed class ScratchDirectory : IDisposable
{
    /// <param name="name">The stable half of the name, e.g. "ardop-monitor-tests".</param>
    public ScratchDirectory(string name)
    {
        FullName = Path.Combine(
            Path.GetTempPath(),
            $"{name}-{Environment.UserName}",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullName);
    }

    /// <summary>The directory, which exists from construction until the test is done with it.</summary>
    public string FullName { get; }

    public void Dispose()
    {
        if (!Directory.Exists(FullName))
        {
            return;
        }

        bool keep = TestContext.Current.TestState?.Result != TestResult.Passed
            && Directory.EnumerateFileSystemEntries(FullName).Any();
        if (!keep)
        {
            Directory.Delete(FullName, recursive: true);
        }
    }
}
