using AwesomeAssertions;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.MultiDecode;

namespace Packet.SoundModem.Tests.MultiDecode;

/// <summary>
/// Pointing the sweep at where a signal actually is, rather than where each mode's catalogue
/// says it lives.
/// </summary>
/// <remarks>
/// The tool's premise is that you do not have to have guessed right, and it had one large hole
/// in it: every mode was tried at its own centre frequency. A signal survey capture is by
/// definition a signal nothing was tuned to, so that is the one file this tool was guaranteed
/// to get wrong - and did. <c>samples/offair/2026-08-24/</c> holds a real one: a plain-AX.25
/// <c>afsk300</c> beacon at 1120 Hz that all forty-six modes missed, because <c>afsk300</c>'s
/// catalogue centre is 1700.
/// </remarks>
public class CentreSweepTests
{
    private const string Capture =
        "samples/offair/2026-08-24/20260824-152242-1134hz-unclaimed.wav";

    /// <summary>PD4R-12>ALL, UI, the beacon the capture carries.</summary>
    private static readonly byte[] Beacon = Convert.FromHexString(
        "829898404040E0A08868A440407903F03A3E3E3E3E3E20504434522D3132203C3C3C3C3C20717276206F6E20"
        + "3134342E3932352028666D20316B3229203134342E3737352028737362292031342E3130352028737362292"
        + "0372E303439202873736229203433382E3137352028666D20396B3629");

    [Fact]
    public void The_Default_Sweep_Misses_A_Signal_Nothing_Was_Tuned_To()
    {
        // Not a defect to fix by widening every mode: a receiver told to listen at 1700 Hz and
        // hearing 1120 Hz is behaving correctly, and a station that moved its modems 600 Hz to
        // catch strays would stop copying the traffic it is there for. What was missing was any
        // way to tell the tool where to listen.
        (float[] samples, int rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), Capture));

        SweepResult result = Sweep.Run(samples, rate, Sweep.PacketModes());

        result.Decodes.Should().BeEmpty("every mode is listening at its own centre, and it is not there");
    }

    [Fact]
    public void The_Measured_Centre_From_The_Survey_Sidecar_Reads_The_Frame()
    {
        (float[] samples, int rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), Capture));
        Sidecar sidecar = CaptureSidecar.Beside(Path.Combine(FindRepoRoot(), Capture))
            .Should().NotBeNull().And.Subject.As<Sidecar>();
        sidecar.Problem.Should().BeNull();
        sidecar.CentreHz.Should().BeApproximately(1134, 1);

        SweepResult result = Sweep.Run(
            samples, rate, Sweep.AtCentres(Sweep.PacketModes(), [sidecar.CentreHz!.Value]));

        Decode decode = result.Decodes.Should().ContainSingle(
            d => d.Frame.SequenceEqual(Beacon)).Subject;
        decode.Label.Should().StartWith("afsk300 @",
            "300 baud AFSK carrying plain AX.25, which is what the station could not read");
        decode.Quality.MonitorOnly.Should().BeFalse("the FCS verified; a real link would deliver it");
    }

    [Fact]
    public void The_Blind_Grid_Is_Fine_Enough_To_Land_On_A_Real_Signal()
    {
        // What sets the 200 Hz step. A grid point is never more than 100 Hz from a signal, and a
        // receiver copies further off than that: this capture is read from three adjacent grid
        // points. A coarser grid would leave holes and a finer one would only multiply the wall
        // clock, which is already the reason --sweep is a flag and not the default.
        (float[] samples, int rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), Capture));
        IReadOnlyList<double> grid = Sweep.BlindCentres();

        SweepResult result = Sweep.Run(
            samples,
            rate,
            Sweep.AtCentres(
                [new SweepEntry("afsk300", "afsk300")], grid, keepDefault: true));

        result.Decodes.Where(d => d.Frame.SequenceEqual(Beacon))
            .Should().NotBeEmpty("the grid should land within a receiver's reach of 1120 Hz");
    }

    [Fact]
    public void A_Mode_With_No_Centre_To_Point_Is_Swept_Once_And_Unchanged()
    {
        // The baseband fsk*/c4fsk* family occupies DC upwards, and ModemCatalog.Create refuses a
        // centre for it outright (issue #39). Multiplying those by a centre grid would be a
        // sweep of identical runs, or eleven exceptions.
        string[] baseband = [.. ModemCatalog.KnownModes.Where(m => !ModemCatalog.AcceptsCentreFrequency(m))];
        baseband.Should().NotBeEmpty("the fsk and c4fsk families are baseband");

        IReadOnlyList<SweepEntry> swept = Sweep.AtCentres(Sweep.AllModes(), [1134]);

        foreach (string mode in baseband)
        {
            swept.Where(e => e.Mode == mode).Should().ContainSingle()
                .Which.Options.CentreFrequencyHz.Should().BeNull();
        }

        swept.Where(e => e.Mode == "afsk300").Should().ContainSingle()
            .Which.Options.CentreFrequencyHz.Should().Be(1134);
        swept.Select(e => e.Label).Should().OnlyHaveUniqueItems(
            "two entries sharing a label would be indistinguishable in the report");
    }

    [Fact]
    public void The_Blind_Sweep_Is_A_Superset_Of_The_Default_One()
    {
        // A wider search that could find less would be a trap. Every mode keeps its own centre,
        // and a grid point that lands on it is dropped rather than run a second time under a
        // decorated name.
        IReadOnlyList<SweepEntry> plain = Sweep.PacketModes();
        IReadOnlyList<SweepEntry> blind =
            Sweep.AtCentres(plain, Sweep.BlindCentres(), keepDefault: true);

        blind.Should().Contain(plain, "the default sweep is still in there, unchanged");
        blind.Select(e => e.Label).Should().OnlyHaveUniqueItems();

        // bpsk300 lives at 1500, which is on the grid: it appears once at 1500, under its own name.
        blind.Where(e => e.Mode == "bpsk300" && e.Options.Detector is null)
            .Count(e => (e.Options.CentreFrequencyHz ?? 1500) == 1500)
            .Should().Be(1, "the mode's own centre and the grid point on it are the same run");
    }

    [Fact]
    public void A_Capture_With_No_Sidecar_Sweeps_As_It_Always_Did()
    {
        CaptureSidecar.Beside(Path.Combine(FindRepoRoot(), "samples/qtsm/qtsm-afsk1200.wav"))
            .Should().BeNull("no JSON beside it, so nothing to say and nothing to change");

        IReadOnlyList<SweepEntry> plain = Sweep.PacketModes();
        Sweep.AtCentres(plain, []).Should().BeSameAs(
            plain, "no centres means the sweep set is untouched, not rebuilt");
    }

    [Fact]
    public void An_Unreadable_Sidecar_Is_Reported_Rather_Than_Silently_Ignored()
    {
        // The tool's failure mode is looking in the wrong place and saying nothing, so a sidecar
        // that cannot be used says so and the sweep carries on at the catalogue centres.
        using var scratch = new ScratchDirectory("pdn-decode-sidecar-tests");
        string wav = Path.Combine(scratch.FullName, "capture.wav");
        string json = Path.Combine(scratch.FullName, "capture.json");
        WavFile.WriteMono(wav, new float[1200], 12000);

        CaptureSidecar.Beside(wav).Should().BeNull("there is no sidecar yet");

        File.WriteAllText(json, "{ this is not json");
        CaptureSidecar.Beside(wav)!.Problem.Should().NotBeNullOrEmpty();

        File.WriteAllText(json, """{ "verdict": "Unclaimed" }""");
        Sidecar noCentre = CaptureSidecar.Beside(wav)!;
        noCentre.CentreHz.Should().BeNull();
        noCentre.Problem.Should().Be("no audioCentreHz in it");

        File.WriteAllText(json, "[]");
        CaptureSidecar.Beside(wav)!.Problem.Should().Be("not a JSON object");

        File.WriteAllText(json, """{ "audioCentreHz": 1133.98, "verdict": "Unclaimed", "widthHz": 230 }""");
        Sidecar good = CaptureSidecar.Beside(wav)!;
        good.Problem.Should().BeNull();
        good.CentreHz.Should().BeApproximately(1133.98, 0.01);
        good.Verdict.Should().Be("Unclaimed");
        good.WidthHz.Should().Be(230);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
