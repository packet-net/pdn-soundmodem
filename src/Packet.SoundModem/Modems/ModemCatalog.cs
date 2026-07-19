using Packet.SoundModem.Fx25;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Modems;

/// <summary>
/// The single source of truth mapping a mode name to a concrete <see cref="IModem"/>, its DSP
/// sample rate, and whether it accepts an audio-centre frequency. The daemon and every in-process
/// consumer (e.g. the PDN node) build modems through here, so the accepted mode set, the rate
/// derivation, and the frequency-gating rule can never drift between them.
/// </summary>
/// <remarks>
/// The mode strings are the same ones accepted by the daemon's <c>--modem N:MODE[:FREQ]</c> and
/// the node's <c>soundmodem</c> port <c>mode</c> field.
/// </remarks>
public static class ModemCatalog
{
    private static readonly string[] _knownModes =
    {
        "afsk1200", "afsk1200-fx25", "afsk1200-fx25rx", "afsk1200-multi",
        "afsk1200-il2p", "afsk1200-il2p-nocrc",
        "afsk300", "afsk300-il2p", "afsk300-il2pc",
        "bpsk300", "bpsk300-multi", "bpsk300-nocrc", "bpsk1200", "bpsk1200-multi",
        "qpsk600", "qpsk2400", "qpsk3600",
        "fsk9600", "fsk9600-il2p", "fsk4800-il2p",
        "c4fsk9600", "c4fsk19200",
        "freedv-datac0", "freedv-datac1", "freedv-datac3", "freedv-datac4",
        "freedv-datac13", "freedv-datac14",
        "ms110d-wn0", "ms110d-wn1", "ms110d-wn2", "ms110d-wn3", "ms110d-wn4",
        "ms110d-wn5", "ms110d-wn6", "ms110d-wn7", "ms110d-wn8", "ms110d-wn13",
    };

    private static readonly HashSet<string> _knownSet = new(_knownModes, StringComparer.Ordinal);

    /// <summary>Every accepted mode string, in catalogue order.</summary>
    public static IReadOnlyList<string> KnownModes { get; } = _knownModes;

    /// <summary>True if <paramref name="mode"/> is a recognised mode string (ordinal, case-sensitive).</summary>
    public static bool IsKnown(string mode) => _knownSet.Contains(mode);

    /// <summary>
    /// The DSP sample rate a mode's demod/mod chain runs at: 48000 for the wideband baseband
    /// families (fsk*/c4fsk*/anything at 9600) and the OFDM/MS110D waveforms, 12000 otherwise.
    /// A soundcard capture rate must be an integer multiple of this.
    /// </summary>
    public static int DspRateFor(string mode) =>
        mode.Contains("9600", StringComparison.Ordinal)
        || mode.StartsWith("fsk", StringComparison.Ordinal)
        || mode.StartsWith("c4fsk", StringComparison.Ordinal)
        || mode.StartsWith("freedv-", StringComparison.Ordinal)
        || mode.StartsWith("ms110d-", StringComparison.Ordinal) ? 48000 : 12000;

    /// <summary>
    /// Whether a mode has a settable audio-centre frequency. Only the variable-centre families —
    /// the AFSK tone-pair and the BPSK/QPSK carrier — do. The baseband fsk*/c4fsk* (DC-to-Nyquist)
    /// and the spec-fixed freedv-*/ms110d- waveforms do not: passing a centre frequency to
    /// <see cref="Create"/> for one of those throws (issue #39).
    /// </summary>
    public static bool AcceptsCentreFrequency(string mode) =>
        mode.StartsWith("afsk", StringComparison.Ordinal)
        || mode.StartsWith("bpsk", StringComparison.Ordinal)
        || mode.StartsWith("qpsk", StringComparison.Ordinal);

    /// <summary>
    /// The default PSK detector for a mode when the caller does not override it: BPSK differential
    /// (measured best on real off-air HF), QPSK coherent (V.26A interop validated coherent). Only
    /// meaningful for the <c>bpsk*</c>/<c>qpsk*</c> modes.
    /// </summary>
    public static PskDetector DefaultDetectorFor(string mode) =>
        mode.StartsWith("qpsk", StringComparison.Ordinal) ? PskDetector.Coherent : PskDetector.Differential;

    /// <summary>
    /// Builds the modem for <paramref name="mode"/> at <paramref name="dspRate"/>, delivering decoded
    /// frames to <paramref name="frameReceived"/>. <paramref name="options"/> supplies the optional
    /// per-mode knobs (centre frequency, BPSK bank width/step, PSK detector); omit it for defaults.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="mode"/> is not a known mode, or a centre frequency was supplied for a
    /// fixed-centre mode (see <see cref="AcceptsCentreFrequency"/>).
    /// </exception>
    public static IModem Create(string mode, int dspRate, Action<byte[]> frameReceived, ModemOptions options = default)
    {
        double? frequency = options.CentreFrequencyHz;
        if (frequency is not null && !AcceptsCentreFrequency(mode))
        {
            throw new ArgumentException(
                $"mode '{mode}' has a fixed centre frequency — drop the frequency override " +
                "(only the afsk*/bpsk*/qpsk* modes accept one)",
                nameof(options));
        }

        int? offsetPairs = options.OffsetPairs;
        double? offsetStepHz = options.OffsetStepHz;
        PskDetector detector = options.Detector ?? DefaultDetectorFor(mode);

        return mode switch
        {
            "afsk1200" => new Afsk1200Modem(dspRate, frameReceived, frequency ?? 1700),
            "afsk1200-fx25" => new Afsk1200Modem(dspRate, frameReceived, frequency ?? 1700, Fx25Mode.TransmitReceive),
            "afsk1200-fx25rx" => new Afsk1200Modem(dspRate, frameReceived, frequency ?? 1700, Fx25Mode.Receive),
            "afsk1200-multi" => new Afsk1200MultiModem(dspRate, frameReceived, offsetPairs: 3, centerFrequency: frequency ?? 1700),
            "afsk1200-il2p" => new Afsk1200Il2pModem(dspRate, frameReceived, crc: true, frequency ?? 1700),
            "afsk1200-il2p-nocrc" => new Afsk1200Il2pModem(dspRate, frameReceived, crc: false, frequency ?? 1700),
            "afsk300" => new Afsk300Modem(dspRate, frameReceived, Afsk300Framing.Ax25, frequency ?? 1700),
            "afsk300-il2p" => new Afsk300Modem(dspRate, frameReceived, Afsk300Framing.Il2p, frequency ?? 1700),
            "afsk300-il2pc" => new Afsk300Modem(dspRate, frameReceived, Afsk300Framing.Il2pCrc, frequency ?? 1700),
            // BPSK defaults to the differential frequency-diversity bank — offsetPairs/offsetStepHz
            // tune it (offsetPairs:0 gives a plain single modem).
            "bpsk300" or "bpsk300-multi" => new BpskMultiModem(dspRate, frameReceived, crc: true, frequency ?? 1500,
                baud: 300, offsetPairs: offsetPairs ?? 4, offsetHz: offsetStepHz, detector: detector),
            "bpsk300-nocrc" => new BpskMultiModem(dspRate, frameReceived, crc: false, frequency ?? 1500,
                baud: 300, offsetPairs: offsetPairs ?? 4, offsetHz: offsetStepHz, detector: detector),
            "bpsk1200" or "bpsk1200-multi" => new BpskMultiModem(dspRate, frameReceived, crc: true, frequency ?? 1500,
                baud: 1200, offsetPairs: offsetPairs ?? 4, offsetHz: offsetStepHz, detector: detector),
            "qpsk600" => QpskModem.Qpsk600(dspRate, frameReceived, detector: detector, carrierFrequency: frequency ?? 1500),
            "qpsk2400" => QpskModem.Qpsk2400(dspRate, frameReceived, detector: detector, carrierFrequency: frequency ?? 1500),
            "qpsk3600" => QpskModem.Qpsk3600(dspRate, frameReceived, detector: detector, carrierFrequency: frequency ?? 1650),
            "fsk9600" => FskModem.Fsk9600(dspRate, frameReceived, FskFraming.ClassicHdlc),
            "fsk9600-il2p" => new FskModem(dspRate, frameReceived, FskFraming.Il2pCrc),
            "fsk4800-il2p" => FskModem.Fsk4800(dspRate, frameReceived),
            "c4fsk9600" => C4fskModem.C4fsk9600(dspRate, frameReceived),
            "c4fsk19200" => C4fskModem.C4fsk19200(dspRate, frameReceived),
            "freedv-datac0" => FreeDvDatacModem.Datac0(dspRate, frameReceived),
            "freedv-datac1" => FreeDvDatacModem.Datac1(dspRate, frameReceived),
            "freedv-datac3" => FreeDvDatacModem.Datac3(dspRate, frameReceived),
            "freedv-datac4" => FreeDvDatacModem.Datac4(dspRate, frameReceived),
            "freedv-datac13" => FreeDvDatacModem.Datac13(dspRate, frameReceived),
            "freedv-datac14" => FreeDvDatacModem.Datac14(dspRate, frameReceived),
            "ms110d-wn0" or "ms110d-wn1" or "ms110d-wn2" or "ms110d-wn3" or "ms110d-wn4"
                or "ms110d-wn5" or "ms110d-wn6" or "ms110d-wn7" or "ms110d-wn8"
                or "ms110d-wn13" => new Ms110dModem(
                    dspRate, frameReceived,
                    new Ms110dTxSettings { WaveformNumber = int.Parse(mode["ms110d-wn".Length..]) }),
            _ => throw new ArgumentException($"unknown mode '{mode}'", nameof(mode)),
        };
    }
}
