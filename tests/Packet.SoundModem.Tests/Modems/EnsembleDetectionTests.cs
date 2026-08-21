using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The ensemble decode-any knob (rx-roadmap workstream 2): a bpsk bank with
/// <see cref="ModemOptions.SecondDetector"/> runs every diversity position under both
/// detectors and delivers the union through the existing content dedupe. These tests pin
/// the mechanism - the union's measured value on real traffic (+1.1 % deliverable on the
/// capture campaign's opening evening) lives in docs/rx-roadmap.md, not in a fixture, since
/// no deterministic single burst separates the detectors (the workstream 5 finding).
/// </summary>
public class EnsembleDetectionTests
{
    private const int SampleRate = 12000;

    private static readonly byte[] TestFrame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    private static float[] CleanBurst()
    {
        IModem tx = ModemCatalog.Create("bpsk300", SampleRate, static _ => { });
        float[] audio = tx.Modulate(TestFrame, txDelayMilliseconds: 100);
        var padded = new float[audio.Length + SampleRate];
        audio.CopyTo(padded, SampleRate / 2);
        return padded;
    }

    [Fact]
    public void A_Clean_Frame_Is_Delivered_Exactly_Once_By_An_Ensemble_Bank()
    {
        // Both detectors decode a clean burst on several branches each; the dedupe must
        // still reduce all of it to one delivery, exactly as a single-detector bank does.
        var frames = new List<byte[]>();
        IModem rx = ModemCatalog.Create("bpsk300", SampleRate, frames.Add,
            new ModemOptions(SecondDetector: PskDetector.Mlse));

        rx.Process(CleanBurst());

        frames.Should().ContainSingle().Which.Should().Equal(TestFrame);
    }

    [Fact]
    public void The_Ensemble_Bank_Doubles_Its_Branches_And_Transmits_From_The_Primary_Centre()
    {
        var single = new BpskMultiModem(SampleRate, static _ => { });
        var ensemble = new BpskMultiModem(SampleRate, static _ => { },
            secondDetector: PskDetector.Mlse);

        ensemble.Mode.Should().Be("bpsk300-il2pc-multi18",
            "9 positions x 2 detectors; the mode string carries the branch count");
        single.Mode.Should().Be("bpsk300-il2pc-multi9");
        // Transmit must be byte-identical whichever bank is receiving - the ensemble is a
        // receiver arrangement, not a wire change.
        ensemble.Modulate(TestFrame, 100).Should().Equal(single.Modulate(TestFrame, 100));
    }

    [Fact]
    public void A_Second_Detector_Equal_To_The_First_Collapses_To_The_Single_Bank()
    {
        var bank = new BpskMultiModem(SampleRate, static _ => { },
            detector: PskDetector.Differential, secondDetector: PskDetector.Differential);

        bank.Mode.Should().Be("bpsk300-il2pc-multi9",
            "a duplicate detector would double CPU for exactly nothing");
    }

    [Theory]
    [InlineData("afsk300-il2pc")]
    [InlineData("freedv-datac3")]
    public void Non_Psk_Modes_Refuse_An_Ensemble_Rather_Than_Silently_Ignoring_It(string mode)
    {
        Action act = () => ModemCatalog.Create(mode, ModemCatalog.DspRateFor(mode),
            static _ => { }, new ModemOptions(SecondDetector: PskDetector.Coherent));

        act.Should().Throw<ArgumentException>(
            "a measurement that asks for an ensemble must never silently get the single bank");
    }

    [Fact]
    public void A_Qpsk_Bank_Takes_A_Coherent_Second_Detector()
    {
        // Issue #326: the QPSK family has the bank, and with it the ensemble knob. Coherent is
        // the only other detector it has - the MLSE stage is BPSK-only.
        IModem rx = ModemCatalog.Create("qpsk600", SampleRate, static _ => { },
            new ModemOptions(SecondDetector: PskDetector.Coherent));
        rx.Mode.Should().Be("qpsk600-il2pc-multi18");

        Action mlse = () => ModemCatalog.Create("qpsk600", SampleRate, static _ => { },
            new ModemOptions(SecondDetector: PskDetector.Mlse));
        mlse.Should().Throw<ArgumentException>("the QPSK demodulator has no equaliser to hand symbols to");
    }
}
