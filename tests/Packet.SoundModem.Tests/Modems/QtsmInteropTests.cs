using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Deterministic QtSoundModem cross-validation: decode QtSM-generated reference WAVs
/// (<c>samples/qtsm/</c>, captured off the snd-aloop rig from QtSoundModem 0.0.0.76 —
/// docs/qtsm-loop.md) with our modems and assert the frames. This is the reproducible,
/// checked-in half of the QtSM interop matrix — the <b>qtsm→ours</b> direction — mirroring
/// <see cref="DirewolfCrossValidationTests"/> and <see cref="Dsp.OccupiedBandwidthTests"/>
/// (which decode Dire Wolf / NinoTNC reference recordings at test time). The live headless
/// QtSM rig stays manual (root, a patched build, the lossy 48 kHz path); only the WAV-corpus
/// decode runs here.
/// </summary>
/// <remarks>
/// <para>
/// Every PSK WAV is decoded with the <b>coherent</b> detector (the default since issue #5).
/// The re-measured qtsm→ours matrix under coherent is 10/10 on every mode; these WAVs are the
/// checked-in evidence. The QtSM TX WAVs carry 10 UI frames with self-describing
/// <c>QTSM-&lt;mode&gt;-NN</c> payloads.
/// </para>
/// <para>Two characterisation cases are pinned as regressions rather than clean interop:</para>
/// <list type="bullet">
///   <item><b>#6</b> — our V.26A <c>qpsk2400</c> decodes QtSM's <b>V26A/DW2400</b> (type 12)
///     transmission but <b>not</b> its legacy "QPSK AX.25 2400bd" (type 10): different phase
///     maps. Both directions of the pinned pair are asserted.</item>
///   <item><b>#10</b> — QtSM's <b>RUH-4800</b> transmission decodes on our 4800 GFSK receiver
///     (the qtsm→ours 4800 direction, which always worked). The ours→QtSM 4800 direction is
///     verified live on the rig, not here (QtSM is not in-process).</item>
/// </list>
/// </remarks>
[Trait("Category", "Interop")]
public class QtsmInteropTests
{
    private static string SamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        return Path.Combine(root, "samples", "qtsm");
    }

    /// <summary>Decodes a QtSM reference WAV with the given modem (built at the WAV's own
    /// rate) and returns every AX.25 frame it recovers. A flush tail lets a frame that ends
    /// flush with the file drain out of the demodulator pipeline.</summary>
    private static List<byte[]> Decode(string wav, Func<int, Action<byte[]>, IModem> makeModem)
    {
        var (samples, rate) = WavFile.ReadMono(Path.Combine(SamplesDir(), wav));
        Array.Resize(ref samples, samples.Length + rate / 2);

        var frames = new List<byte[]>();
        IModem modem = makeModem(rate, frames.Add);
        modem.Process(samples);
        return frames;
    }

    private static string Ascii(byte[] frame) => System.Text.Encoding.Latin1.GetString(frame);

    // (file, mode label, factory) for the modes that interoperate cleanly qtsm→ours.
    public static TheoryData<string, string, string> CleanModes => new()
    {
        { "qtsm-afsk1200.wav", "afsk1200", "afsk1200" },
        { "qtsm-bpsk300.wav", "bpsk300", "bpsk300" },
        { "qtsm-bpsk1200.wav", "bpsk1200", "bpsk1200" },
        { "qtsm-qpsk600.wav", "qpsk600", "qpsk600" },
        { "qtsm-qpsk3600.wav", "qpsk3600", "qpsk3600" },
    };

    [Theory]
    [MemberData(nameof(CleanModes))]
    public void QtSm_Transmission_Decodes_On_Our_Receiver(string wav, string mode, string factory)
    {
        List<byte[]> frames = Decode(wav, ModemFor(factory));

        // QtSM transmitted 10 UI frames; our receiver (coherent PSK detector) recovers all 10.
        frames.Should().HaveCount(10, "QtSM's {0} transmission carries 10 frames", mode);
        for (int seq = 0; seq < 10; seq++)
        {
            frames.Should().Contain(
                f => Ascii(f).Contains($"QTSM-{mode}-{seq:D2}"),
                "frame #{0} of QtSM's {1} transmission", seq, mode);
        }
    }

    [Fact]
    public void Qpsk2400_Decodes_QtSms_V26A_But_Not_Its_Legacy_Map()
    {
        // #6: our qpsk2400 is the V.26A symbol map (as NinoTNC and Dire Wolf use), so it pairs
        // with QtSM's V26A/DW2400 (ModemType 12), not its legacy "QPSK AX.25 2400bd" (type 10).
        List<byte[]> v26a = Decode("qtsm-qpsk2400-v26a.wav", ModemFor("qpsk2400"));
        List<byte[]> legacy = Decode("qtsm-qpsk2400-legacy.wav", ModemFor("qpsk2400"));

        v26a.Should().HaveCountGreaterThanOrEqualTo(8, "the V26A/DW2400 map is the correct pairing");
        legacy.Should().BeEmpty("the legacy UZ7HO 2400 map is a different phase map (#6)");
    }

    [Fact]
    public void QtSm_Ruh4800_Transmission_Decodes_On_Our_4800_Receiver()
    {
        // #10 (qtsm→ours direction): QtSM's Dire-Wolf RUH-4800 transmission decodes on our
        // NinoTNC-derived 4800 GFSK receiver. (The ours→QtSM 4800 direction is verified live
        // on the rig — 10/10 under current code — not here; QtSM is not in-process.)
        List<byte[]> frames = Decode(
            "qtsm-ruh4800.wav", (rate, sink) => FskModem.Fsk4800(rate, sink));

        frames.Should().HaveCountGreaterThanOrEqualTo(
            5, "our 4800 GFSK receiver decodes QtSM's RUH-4800 transmission");
    }

    private static Func<int, Action<byte[]>, IModem> ModemFor(string factory) => factory switch
    {
        "afsk1200" => (rate, sink) => new Afsk1200Modem(rate, sink),
        "bpsk300" => (rate, sink) => BpskModem.Bpsk300(rate, sink),
        "bpsk1200" => (rate, sink) => BpskModem.Bpsk1200(rate, sink),
        "qpsk600" => (rate, sink) => QpskModem.Qpsk600(rate, sink),
        "qpsk2400" => (rate, sink) => QpskModem.Qpsk2400(rate, sink),
        "qpsk3600" => (rate, sink) => QpskModem.Qpsk3600(rate, sink),
        _ => throw new ArgumentException($"unknown factory '{factory}'", nameof(factory)),
    };
}
