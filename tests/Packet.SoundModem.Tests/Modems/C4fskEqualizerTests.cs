using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Regression cover for the c4fsk decision-directed equalizer. Real NinoTNC
/// transmissions carry pattern-dependent ISI that squeezes outer symbols to
/// 0.53–0.67 normalised at the decision instant — under the 2/3 slicing boundary —
/// so whole frames died on payload content alone (2026-07-31 bench corpus:
/// 0011 txd50 decoded 0/3 and txd120 2/3 while txd250 decoded clean, from
/// recordings with indistinguishable levels, spectra and DC; every error in the
/// instrumented trace was an outer→inner demotion). The hermetic stand-in for
/// that channel is our own modulator followed by an extra low-pass tight enough
/// to partially close the 4-level eye: the pre-equalizer slicer loses frames
/// through it, the equalizer recovers them.
/// </summary>
public class C4fskEqualizerTests
{
    [Theory]
    [InlineData("c4fsk9600", 4800, 0.62)]
    [InlineData("c4fsk19200", 9600, 0.65)]
    public void Isi_Closed_Eye_Still_Decodes(string mode, int symbolRate, double cutoffFactor)
    {
        const int rate = 48000;
        var payload = new byte[180];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)('A' + (i % 26));
        }

        byte[] frame =
        [
            0xA0, 0xA4, 0xA6, 0x40, 0x40, 0x40, 0xE0,       // "PRS" (command),
            0x9A, 0x60, 0x98, 0xA8, 0x8A, 0x40, 0x63,       // "M0LTE"-1
            0x03, 0xF0,
            .. payload,
        ];

        IModem tx = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(rate, _ => { })
            : C4fskModem.C4fsk19200(rate, _ => { });
        float[] clean = tx.Modulate(frame, 120);

        // The cutoff (0.62×/0.65× the symbol rate per mode) leaves the binary eye open
        // but drags 4-level outer symbols with opposite-going neighbours toward the
        // inner band — the measured corpus failure mode, reproduced hermetically at
        // the tightest cutoff the equalizer still recovers (0.6× and below closes the
        // eye past what decision-directed adaptation can reopen; 0.65×+ decodes even
        // without it at 4800 sym/s).
        var channel = new FirFilter(FilterDesign.LowPass(cutoffFactor * symbolRate, rate, 48));
        var audio = new float[(rate / 2) + clean.Length + (rate / 2)];
        for (int i = 0; i < clean.Length; i++)
        {
            audio[(rate / 2) + i] = channel.Next(clean[i]);
        }

        int decoded = 0;
        IModem rx = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(rate, _ => decoded++)
            : C4fskModem.C4fsk19200(rate, _ => decoded++);
        rx.Process(audio);

        decoded.Should().Be(1, "the equalizer must recover the ISI-closed 4-level eye for {0}", mode);
    }
}
