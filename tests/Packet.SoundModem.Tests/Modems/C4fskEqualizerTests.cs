using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Regression cover for the c4fsk decision-directed equalizer. Real NinoTNC
/// transmissions carry pattern-dependent ISI that squeezes outer symbols to
/// 0.53-0.67 normalised at the decision instant - under the 2/3 slicing boundary -
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
    [InlineData("c4fsk9600")]
    public void Noise_Only_Audio_Emits_No_Frames_And_A_Burst_After_It_Decodes(string mode)
    {
        // Idle-channel robustness pinned at the modem surface, implementation-agnostic:
        // a long noisy idle produces no false frames, and a burst arriving after 20 s
        // of it still decodes. (c4fsk19200 is not covered: its after-noise cold start
        // at 5 samples/symbol is an open capability bar tracked in the ledger's
        // 2026-08-01 ungating follow-up.)
        const int rate = 48000;
        var random = new Random(20260801);
        var noise = new float[rate * 20];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = 0.02f * (float)((random.NextDouble() + random.NextDouble()
                + random.NextDouble() - 1.5) / 1.5);
        }

        byte[] frame =
        [
            0xA0, 0xA4, 0xA6, 0x40, 0x40, 0x40, 0xE0,
            0x9A, 0x60, 0x98, 0xA8, 0x8A, 0x40, 0x63,
            0x03, 0xF0, .. System.Text.Encoding.ASCII.GetBytes("after the idle channel"),
        ];
        IModem tx = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(rate, _ => { })
            : C4fskModem.C4fsk19200(rate, _ => { });
        float[] burst = tx.Modulate(frame, 20);

        var got = new List<byte[]>();
        IModem rx = mode == "c4fsk9600"
            ? C4fskModem.C4fsk9600(rate, got.Add)
            : C4fskModem.C4fsk19200(rate, got.Add);
        rx.Process(noise);
        int falseFrames = got.Count;
        rx.Process(burst);
        rx.Process(new float[rate / 2]);

        falseFrames.Should().Be(0, "noise alone must never produce a frame for {0}", mode);
        got.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Theory]
    [InlineData("c4fsk9600", 4800, 0.63)]
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

        // The cutoff (0.63×/0.65× the symbol rate per mode) leaves the binary eye open
        // but drags 4-level outer symbols with opposite-going neighbours toward the
        // inner band - the measured corpus failure mode, reproduced hermetically at
        // the tightest cutoff the equalizer still recovers. (0.62× passed before the
        // preamble adaptation freeze: pre-converging on the rank-deficient preamble
        // bought one extra hundredth of cutoff at the price of unbounded tap drift on
        // long noisy run-ins - see the freeze comment in C4fskModem. The no-equalizer
        // discrimination anchor for c4fsk9600 is the bench corpus: 45/45 with the
        // equalizer, 43/45 without; c4fsk19200's 0.65× row fails outright without it.)
        var channel = new FirFilter(FilterDesign.LowPass(cutoffFactor * symbolRate, rate, 48));
        var audio = new float[(rate / 2) + clean.Length + (rate * 5)];   // TEMP-DIAG long tail
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
