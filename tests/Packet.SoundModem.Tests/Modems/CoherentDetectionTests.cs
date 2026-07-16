using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The measurement gate of issue #5, as regression tests: coherent (Costas) detection — the
/// default, matching the NinoTNC — must recover noise margin over the differential opt-in it
/// replaced. #5's rule was "do not merge on the theory — measure it"; these bake the measured
/// before/after so a regression that quietly gives the margin back turns the suite red.
/// </summary>
/// <remarks>
/// Deterministic by construction: both detectors decode the <em>same</em> fixed set of seeded
/// noise realisations at a per-mode σ chosen in the measured decode gap, so the comparison is
/// like-for-like and cannot flake. The claim is directional (coherent decodes strictly more of
/// the same noisy bursts), which is the whole point of the change.
/// </remarks>
public class CoherentDetectionTests
{
    private const int SampleRate = 12000;

    private static IModem Make(string mode, PskDetector detector, Action<byte[]> sink) => mode switch
    {
        "bpsk300" => BpskModem.Bpsk300(SampleRate, sink, detector: detector),
        "bpsk1200" => BpskModem.Bpsk1200(SampleRate, sink, detector: detector),
        "qpsk600" => QpskModem.Qpsk600(SampleRate, sink, detector: detector),
        "qpsk2400" => QpskModem.Qpsk2400(SampleRate, sink, detector: detector),
        "qpsk3600" => QpskModem.Qpsk3600(SampleRate, sink, detector: detector),
        _ => throw new ArgumentException($"unknown mode '{mode}'", nameof(mode)),
    };

    private static int DecodeCount(string mode, PskDetector detector, float sigma, int trials)
    {
        byte[] baseFrame = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        int ok = 0;
        for (int t = 0; t < trials; t++)
        {
            var frame = (byte[])baseFrame.Clone();
            frame[^1] = (byte)(0x30 + t);   // vary the payload per trial

            IModem tx = Make(mode, detector, _ => { });
            float[] clean = tx.Modulate(frame, txDelayMilliseconds: 200);
            int pad = SampleRate / 5;
            var audio = new float[clean.Length + 2 * pad];
            clean.CopyTo(audio, pad);

            var random = new Random(1000 + t);   // same seed per trial index → same noise for both detectors
            for (int i = 0; i < audio.Length; i++)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                audio[i] += sigma * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }

            var frames = new List<byte[]>();
            IModem rx = Make(mode, detector, frames.Add);
            rx.Process(audio);
            if (frames.Count == 1 && frames[0].AsSpan().SequenceEqual(frame))
            {
                ok++;
            }
        }

        return ok;
    }

    /// <summary>
    /// At a σ inside each mode's measured decode gap, coherent detection decodes strictly more
    /// of the same noisy bursts than the differential detector — the ~1–2 dB margin #5 set out
    /// to recover. σ per mode is chosen where the measured gap is wide and stable.
    /// </summary>
    [Theory]
    [InlineData("bpsk300", 0.60f)]
    [InlineData("bpsk1200", 0.35f)]
    [InlineData("qpsk600", 0.40f)]
    [InlineData("qpsk2400", 0.25f)]
    [InlineData("qpsk3600", 0.12f)]
    public void Coherent_Recovers_Noise_Margin_Over_Differential(string mode, float sigma)
    {
        const int Trials = 30;
        int coherent = DecodeCount(mode, PskDetector.Coherent, sigma, Trials);
        int differential = DecodeCount(mode, PskDetector.Differential, sigma, Trials);

        coherent.Should().BeGreaterThan(
            differential,
            "coherent detection of mode '{0}' must recover margin over the differential opt-in "
            + "(σ={1}: coherent {2}/{3}, differential {4}/{3})",
            mode, sigma, coherent, Trials, differential);
    }

    /// <summary>Both detectors decode a clean frame for every PSK mode — the coherent default
    /// did not regress the easy case while chasing the noisy one.</summary>
    [Theory]
    [InlineData("bpsk300")]
    [InlineData("bpsk1200")]
    [InlineData("qpsk600")]
    [InlineData("qpsk2400")]
    [InlineData("qpsk3600")]
    public void Both_Detectors_Decode_A_Clean_Frame(string mode)
    {
        DecodeCount(mode, PskDetector.Coherent, sigma: 0f, trials: 5).Should().Be(5);
        DecodeCount(mode, PskDetector.Differential, sigma: 0f, trials: 5).Should().Be(5);
    }
}
