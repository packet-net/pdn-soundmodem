using System.Text;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The performance criteria, as tests. The bar is a real NinoTNC (firmware 3.44), whose
/// numbers come from the wired TNC-to-TNC TXDELAY survey of 2026-07-16: its receiver
/// acquires from a single 16-bit word of preamble in 13 of 15 modes, and from ~6 words
/// (10 ms) on 9600 GFSK AX.25. "Match or better NinoTNC performance" — Tom, 2026-07-16.
/// </summary>
/// <remarks>
/// <para>
/// Tests in this class PASS and must stay passing: each one is parity already achieved,
/// and a red here means a regression below reference hardware. Criteria not yet met live
/// in <see cref="NinoTncAspirationTests"/> under the <c>Aspiration</c> category, which CI
/// runs non-blocking — a red there is a to-do, not a break.
/// </para>
/// <para>
/// All figures are clean-channel, our-TX to our-RX, cold receiver — the same conditions
/// as the offline sweep they encode. Hardware-facing criteria (the NinoTNC decoding our
/// new training preamble at short TXDELAY, noise margin against its RX) belong to the
/// bench rig, not unit tests.
/// </para>
/// </remarks>
public class NinoTncParityTests
{
    /// <summary>Frames per point; the acquisition criterion is all-of-them.</summary>
    private const int Frames = 10;

    private static byte[] Frame(int seq)
    {
        var payload = new byte[40];
        byte[] tag = Encoding.ASCII.GetBytes($"PDN RX{seq:D2} ");
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = i < tag.Length ? tag[i] : (byte)('A' + ((i - tag.Length) % 26));
        }

        static byte[] Addr(string call, bool last, int command)
        {
            var b = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                b[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            b[6] = (byte)((command << 7) | 0x60 | (last ? 1 : 0));
            return b;
        }

        return [.. Addr("TEST", false, 1), .. Addr("Q0AAA", true, 0), 0x03, 0xF0, .. payload];
    }

    internal static IModem Create(
        string mode, int rate, Action<byte[]> sink, PskDetector psk = PskDetector.Coherent) => mode switch
    {
        "fsk9600" => FskModem.Fsk9600(rate, sink, FskFraming.ClassicHdlc),
        "fsk9600-il2p" => FskModem.Fsk9600(rate, sink, FskFraming.Il2pCrc),
        "fsk4800-il2p" => FskModem.Fsk4800(rate, sink),
        "c4fsk9600" => C4fskModem.C4fsk9600(rate, sink),
        "c4fsk19200" => C4fskModem.C4fsk19200(rate, sink),
        "qpsk3600" => QpskModem.Qpsk3600(rate, sink, detector: psk),
        "afsk1200" => new Afsk1200Modem(rate, sink),
        "afsk1200-il2p" => new Afsk1200Il2pModem(rate, sink),
        "bpsk300" => BpskModem.Bpsk300(rate, sink, detector: psk),
        "qpsk600" => QpskModem.Qpsk600(rate, sink, detector: psk),
        "bpsk1200" => BpskModem.Bpsk1200(rate, sink, detector: psk),
        "qpsk2400" => QpskModem.Qpsk2400(rate, sink, detector: psk),
        "afsk300" => new Afsk300Modem(rate, sink, Afsk300Framing.Ax25),
        "afsk300-il2p" => new Afsk300Modem(rate, sink, Afsk300Framing.Il2p),
        "afsk300-il2pc" => new Afsk300Modem(rate, sink, Afsk300Framing.Il2pCrc),
        _ => throw new ArgumentException($"unknown mode '{mode}'", nameof(mode)),
    };

    private static int Decoded(
        string mode, int rate, int txDelayMs, PskDetector psk = PskDetector.Coherent)
    {
        IModem tx = Create(mode, rate, _ => { }, psk);
        var audio = new List<float>();
        audio.AddRange(new float[rate / 2]);
        for (int i = 0; i < Frames; i++)
        {
            audio.AddRange(tx.Modulate(Frame(i), txDelayMs));
            audio.AddRange(new float[rate / 2]);
        }

        int decoded = 0;
        IModem rx = Create(mode, rate, _ => decoded++, psk);
        rx.Process(audio.ToArray());
        return decoded;
    }

    /// <summary>
    /// Modes where the NinoTNC's receiver acquires from its firmware-floor preamble
    /// (one 16-bit word). Ours must decode every frame at TXDELAY 0 — cold receiver,
    /// no traffic history — to claim parity. The PSK modes are NOT here: their coherent
    /// (Costas) default needs preamble to pull the loop in after idle, exactly like the
    /// NinoTNC — see <see cref="Coherent_Psk_Acquires_After_Idle_Within_Ninotnc_Preamble"/>
    /// for the coherent criterion and <see cref="Differential_Psk_Acquires_At_Txdelay_Zero"/>
    /// for the differential opt-in that keeps the 0 ms property.
    /// </summary>
    [Theory]
    [InlineData("fsk9600-il2p", 48000)]
    [InlineData("fsk4800-il2p", 48000)]
    [InlineData("c4fsk9600", 48000)]
    [InlineData("c4fsk19200", 48000)]
    [InlineData("afsk1200", 12000)]
    [InlineData("afsk1200-il2p", 12000)]
    [InlineData("afsk300", 12000)]
    [InlineData("afsk300-il2p", 12000)]
    [InlineData("afsk300-il2pc", 12000)]
    public void Acquires_At_Txdelay_Zero_Like_A_NinoTNC(string mode, int rate)
    {
        Decoded(mode, rate, txDelayMs: 0).Should().Be(
            Frames,
            "a NinoTNC's receiver acquires mode '{0}' from one 16-bit word of preamble", mode);
    }

    /// <summary>
    /// The differential detector — the opt-in for short-preamble links (#5) — keeps the
    /// "decode at TXDELAY 0, cold" property for every PSK mode: no carrier recovery to pull
    /// in, so it acquires from the phase change directly. This is the trade the coherent
    /// default gives up (a ~1–2 dB noise cost) to match the NinoTNC's margin; the opt-in
    /// buys the instant acquisition back where a link needs it.
    /// </summary>
    [Theory]
    [InlineData("bpsk300")]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("qpsk2400")]
    [InlineData("qpsk3600")]
    public void Differential_Psk_Acquires_At_Txdelay_Zero(string mode)
    {
        Decoded(mode, 12000, txDelayMs: 0, psk: PskDetector.Differential).Should().Be(
            Frames, "the differential opt-in acquires mode '{0}' with no preamble", mode);
    }

    /// <summary>
    /// The coherent (Costas) default — matching the NinoTNC's detection method and its noise
    /// margin — acquires each PSK mode within the NinoTNC's own preamble (~100 ms for QPSK)
    /// even from a cold receiver between idle gaps. Measured floors (10-frame survey, 0.5 s
    /// gaps): qpsk2400 full by 50 ms, qpsk3600 by 80 ms, the 300-baud and BPSK modes at 0 ms;
    /// 100 ms clears them all. This is the deliberate, accepted trade of #5 — match the
    /// NinoTNC, do not beat it on the acquisition axis that carries no operational value.
    /// </summary>
    [Theory]
    [InlineData("bpsk300")]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("qpsk2400")]
    [InlineData("qpsk3600")]
    public void Coherent_Psk_Acquires_After_Idle_Within_Ninotnc_Preamble(string mode)
    {
        Decoded(mode, 12000, txDelayMs: 100).Should().Be(
            Frames, "coherent detection acquires mode '{0}' within a NinoTNC-length preamble", mode);
    }

    /// <summary>
    /// 9600 GFSK AX.25 is the one mode where the reference hardware itself needs a real
    /// preamble: the NinoTNC decodes 40 % at TXDELAY 0 and 100 % from 10 ms — consistent
    /// with the G3RUH x¹⁷ scrambler needing more than 16 bits to flush. Parity is 10 ms.
    /// </summary>
    [Fact]
    public void Fsk9600_Classic_Acquires_At_10ms_Like_A_NinoTNC()
    {
        Decoded("fsk9600", 48000, txDelayMs: 10).Should().Be(
            Frames, "the NinoTNC's own floor for 9600 GFSK AX.25 is 10 ms");
    }
    /// <summary>
    /// The mode-11 idle-gap acquisition we measured on the NinoTNC (2/10 at a 20 ms
    /// preamble after 4 s of silence) is a bar we should hold OURSELVES to from the
    /// other side: a receiver, gone quiet for 4 s, must still acquire a short-preamble
    /// qpsk2400 burst, with channel noise at ~20 dB SNR present throughout. This is the
    /// <b>differential opt-in's</b> defining strength (#5): with no carrier loop to pull in,
    /// it acquires from the phase change at a 20 ms preamble the coherent default would need
    /// ~100 ms for. Red here means the short-preamble opt-in got worse at the thing it exists
    /// to be good at.
    /// </summary>
    [Fact]
    public void Qpsk2400_Differential_Acquires_A_Short_Preamble_After_Idle_With_Noise()
    {
        const int Rate = 12000;
        const int Frames = 10;
        var random = new Random(11);

        IModem tx = QpskModem.Qpsk2400(Rate, _ => { }, detector: PskDetector.Differential);
        var audio = new List<float>();
        audio.AddRange(new float[Rate * 4]);          // 4 s idle
        for (int i = 0; i < Frames; i++)
        {
            audio.AddRange(tx.Modulate([0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0, 1, 2, 3, 4], 20));
            audio.AddRange(new float[Rate * 4]);      // 4 s between bursts — always cold
        }

        float[] samples = audio.ToArray();
        for (int i = 0; i < samples.Length; i++)
        {
            // Gaussian noise ~20 dB below the 0.8 peak signal. Determinism note: the only
            // stochastic input is this seeded sequence, and .NET reserves the right to
            // change Random's algorithm across major versions — so the margin matters
            // more than the seed. Measured 2026-07-16: the decode cliff is at sigma ~0.20
            // (9-6/10 across seeds 11/7/42/1/99; every seed 10/10 at <= 0.15), so this
            // assertion runs 4x amplitude (12 dB) inside it. Neither a different noise
            // sequence nor last-ulp transcendental differences between architectures can
            // cross that. If this test ever flakes, something real moved.
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            samples[i] += 0.05f * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        int decoded = 0;
        IModem rx = QpskModem.Qpsk2400(Rate, _ => decoded++, detector: PskDetector.Differential);
        rx.Process(samples);

        decoded.Should().Be(Frames, "an idle noisy channel is the normal case, not the special one");
    }
}
