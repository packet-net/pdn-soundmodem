using M0LTE.Il2p;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The MLSE detector (rx-roadmap workstream 5) through the full modem chain: front end,
/// DPLL, equaliser, deframer. The headline test is the miniature of the workstream's whole
/// argument - a static two-path channel whose inter-symbol interference defeats the
/// symbol-by-symbol DF-DD detector while the trellis reads through it.
/// </summary>
public class MlseDetectionTests
{
    private const int SampleRate = 12000;
    private const int SamplesPerSymbol = SampleRate / 300;

    private static (BpskDemodulator Demodulator, List<byte[]> Frames) BuildReceiver(
        PskDetector detector)
    {
        var frames = new List<byte[]>();
        var deframer = new Il2pDeframer((frame, _) => frames.Add(frame), crcMode: true);
        var demodulator = new BpskDemodulator(
            SampleRate, deframer.PushBit, detector: detector,
            softBitSink: (bit, confidence, phase) => { });
        return (demodulator, frames);
    }

    private static float[] ModulateFrame(byte[] ax25, int preambleBits = 96)
    {
        byte[] wire = Il2pCodec.Encode(ax25, appendCrc: true);
        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits, Il2pFramer.PreambleStyle.Zeros);
        return new BpskModulator(SampleRate, rollOff: 0.20).Modulate(bits);
    }

    private static float[] WithPaddingAndNoise(float[] audio, float noiseSigma, int seed)
    {
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + (2 * pad)];
        audio.CopyTo(padded, pad);
        var random = new Random(seed);
        for (int i = 0; i < padded.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            padded[i] += noiseSigma
                * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return padded;
    }

    /// <summary>A static two-path channel: the direct signal plus an echo
    /// <paramref name="echoGain"/> strong, <paramref name="echoSymbols"/> of a symbol
    /// later - CCIR Poor's geometry frozen at its worst instant.</summary>
    private static float[] TwoPath(float[] audio, double echoGain, double echoSymbols)
    {
        int delay = (int)(echoSymbols * SamplesPerSymbol);
        var output = new float[audio.Length + delay];
        for (int i = 0; i < audio.Length; i++)
        {
            output[i] += audio[i];
            output[i + delay] += (float)(echoGain * audio[i]);
        }

        return output;
    }

    private static readonly byte[] TestFrame =
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    [Fact]
    public void A_Clean_Frame_Decodes_Through_The_Mlse_Detector()
    {
        var (demodulator, frames) = BuildReceiver(PskDetector.Mlse);

        demodulator.Process(WithPaddingAndNoise(ModulateFrame(TestFrame), 0.02f, seed: 5));

        frames.Should().ContainSingle().Which.Should().Equal(TestFrame);
    }

    [Fact]
    public void A_Deep_Static_Two_Path_Channel_Decodes_Through_The_Mlse_Detector()
    {
        // An equal-amplitude echo 0.6 of a symbol late - CCIR Poor's equal-power paths
        // frozen at a hostile instant. A finding worth recording, established while
        // pinning this fixture (2026-08-06, the workstream 5 probes): no static two-path
        // setting separates the detectors through the full modem chain - the DF-DD
        // reference plus 16-parity Reed-Solomon digests any static echo this trellis can
        // equalise, and the settings that kill DF-DD (near-antiphase delays, where the
        // composite has a spectral null at the carrier and the DPLL loses its crossings)
        // kill the trellis too, because no symbol-rate equaliser restores timing. So this
        // asserts only that the MLSE detector reads through the deep echo; the fading A/B
        // lives on the sim ladder, where the honest answer today is parity
        // (docs/rx-roadmap.md workstream 5).
        float[] audio = WithPaddingAndNoise(
            TwoPath(ModulateFrame(TestFrame), echoGain: 1.0, echoSymbols: 0.6),
            noiseSigma: 0.10f, seed: 9);

        var (mlse, mlseFrames) = BuildReceiver(PskDetector.Mlse);
        mlse.Process(audio);

        mlseFrames.Should().ContainSingle().Which.Should().Equal(TestFrame);
    }

    [Fact]
    public void Qpsk_Rejects_The_Mlse_Detector()
    {
        Action act = () => QpskModem.Qpsk600(
            SampleRate, static _ => { }, detector: PskDetector.Mlse);

        act.Should().Throw<ArgumentException>();
    }
}
