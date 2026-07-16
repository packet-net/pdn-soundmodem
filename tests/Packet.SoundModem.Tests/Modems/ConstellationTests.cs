using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The per-symbol constellation side channel (issue #9): the PSK demodulators expose their
/// decision points via <see cref="IConstellationSource"/>, and <see cref="ConstellationSource"/>
/// batches them into scope frames. These assert the geometry a clean signal must produce —
/// the diagnostic's whole value is that a healthy constellation looks healthy — plus the
/// batching/encoding contract and that the channel wires it only for the PSK modes.
/// </summary>
public class ConstellationTests
{
    private const int SampleRate = 12000;

    private static float[] WithPadding(float[] audio)
    {
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    /// <summary>The well-formed symbols: those whose matched-filter output is within 60 % of
    /// the burst peak. Gating on a fraction of the peak (not the median) is deliberate —
    /// genuinely low-amplitude symbols carry real per-symbol phase noise and belong to the
    /// smear the diagnostic is meant to reveal, so the "is the core tight?" assertion should
    /// look at the strong symbols that define the cluster centres.</summary>
    private static List<ConstellationPoint> StrongPoints(List<ConstellationPoint> all)
    {
        double peak = all.Max(p => Math.Sqrt(p.I * p.I + p.Q * p.Q));
        return all.Where(p => Math.Sqrt(p.I * p.I + p.Q * p.Q) >= 0.6 * peak).ToList();
    }

    // Both detectors produce a four-cluster QPSK constellation, so #9 is validated for each.
    // The differential product plots phase *changes* landing on exact multiples of 90° and so
    // clusters tightest (>0.94); the coherent detector plots the recovered *absolute*
    // constellation, whose four diagonal points carry the Costas loop's residual phase jitter
    // and so cluster a little looser (~0.87–0.91) — both far above the ~0.05 of phase noise.
    [Theory]
    [InlineData(2400, PskDetector.Differential, 0.90)]
    [InlineData(3600, PskDetector.Differential, 0.90)]
    [InlineData(2400, PskDetector.Coherent, 0.80)]
    [InlineData(3600, PskDetector.Coherent, 0.80)]
    public void Qpsk_Symbols_Cluster_At_Four_Phases(int bitRate, PskDetector detector, double minCoherence)
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        QpskModem tx = bitRate == 2400
            ? QpskModem.Qpsk2400(SampleRate, _ => { }, detector: detector)
            : QpskModem.Qpsk3600(SampleRate, _ => { }, detector: detector);
        var frames = new List<byte[]>();
        QpskModem rx = bitRate == 2400
            ? QpskModem.Qpsk2400(SampleRate, frames.Add, detector: detector)
            : QpskModem.Qpsk3600(SampleRate, frames.Add, detector: detector);

        var points = new List<ConstellationPoint>();
        ((IConstellationSource)rx).SymbolPlotted += points.Add;
        rx.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
        var strong = StrongPoints(points);
        strong.Count.Should().BeGreaterThan(50);

        // Offset-invariant 4-fold phase coherence: raising each symbol's angle to the 4th
        // multiple collapses four clusters 90° apart onto one direction whatever their
        // absolute orientation, so the mean unit vector's length is ~1 for a tight
        // constellation and ~0 for phase noise.
        double sumRe = 0, sumIm = 0;
        foreach (ConstellationPoint p in strong)
        {
            double a = Math.Atan2(p.Q, p.I);
            sumRe += Math.Cos(4 * a);
            sumIm += Math.Sin(4 * a);
        }

        double coherence = Math.Sqrt(sumRe * sumRe + sumIm * sumIm) / strong.Count;
        coherence.Should().BeGreaterThan(minCoherence);
    }

    [Fact]
    public void Bpsk_Symbols_Are_One_Dimensional_And_Bimodal()
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        BpskModem tx = new(SampleRate, _ => { }, crc: true, carrierFrequency: 1500);
        var frames = new List<byte[]>();
        BpskModem rx = new(SampleRate, frames.Add, crc: true, carrierFrequency: 1500);

        var points = new List<ConstellationPoint>();
        ((IConstellationSource)rx).SymbolPlotted += points.Add;
        rx.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);

        // BPSK is a 1-D constellation: the decision is purely real.
        points.Should().OnlyContain(p => p.Q == 0f);

        var strong = StrongPoints(points);
        strong.Count.Should().BeGreaterThan(50);

        // Both rails present (random data visits both) and the magnitude is consistent —
        // a clean eye, not a smear toward zero.
        strong.Should().Contain(p => p.I > 0).And.Contain(p => p.I < 0);
        double meanAbs = strong.Average(p => Math.Abs(p.I));
        double stdev = Math.Sqrt(strong.Average(p => Math.Pow(Math.Abs(p.I) - meanAbs, 2)));
        (stdev / meanAbs).Should().BeLessThan(0.35);
    }

    [Fact]
    public void ConstellationSource_Auto_Scales_A_Frame_To_Its_Peak()
    {
        var frames = new List<byte[]>();
        var source = new ConstellationSource(f => frames.Add(f.ToArray()), pointsPerFrame: 4);
        source.FrameLength.Should().Be(8);

        source.Add(new ConstellationPoint(0.5f, 0f));
        source.Add(new ConstellationPoint(-0.5f, 0f));
        source.Add(new ConstellationPoint(0f, 0.5f));
        frames.Should().BeEmpty("a frame is only emitted once pointsPerFrame points arrive");
        source.Add(new ConstellationPoint(0f, -0.5f));

        // Peak component 0.5 maps to ±127; the byte pairs are (I, Q) two's-complement.
        frames.Should().ContainSingle().Which.Should().Equal(
            127, 0, unchecked((byte)-127), 0, 0, 127, 0, unchecked((byte)-127));
    }

    [Fact]
    public void ConstellationSource_Emits_Zeros_For_A_Silent_Frame()
    {
        var frames = new List<byte[]>();
        var source = new ConstellationSource(f => frames.Add(f.ToArray()), pointsPerFrame: 2);

        source.Add(new ConstellationPoint(0f, 0f));
        source.Add(new ConstellationPoint(1e-9f, 0f));

        frames.Should().ContainSingle().Which.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Channel_Wires_Constellation_For_A_Psk_Modem()
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        var captured = new List<(int Sub, byte[] Frame)>();
        var channel = new SoundModemChannel(
            SampleRate, constellationSink: (sub, frame) => captured.Add((sub, frame.ToArray())));
        channel.AddModem(2, sink => new BpskModem(SampleRate, sink, crc: true, carrierFrequency: 1500));

        BpskModem tx = new(SampleRate, _ => { }, crc: true, carrierFrequency: 1500);
        channel.ProcessReceive(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 200)));

        captured.Should().NotBeEmpty();
        captured.Should().OnlyContain(c => c.Sub == 2);
        captured.Should().OnlyContain(c => c.Frame.Length == 512);
    }

    [Fact]
    public void Channel_Leaves_Constellation_Unwired_For_A_Non_Psk_Modem()
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");
        var captured = new List<(int, byte[])>();
        var channel = new SoundModemChannel(
            SampleRate, constellationSink: (sub, frame) => captured.Add((sub, frame.ToArray())));
        channel.AddModem(0, sink => new Afsk1200Modem(SampleRate, sink, 1700));

        var tx = new Afsk1200Modem(SampleRate, _ => { }, 1700);
        channel.ProcessReceive(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 200)));

        captured.Should().BeEmpty("AFSK is not a phase constellation and does not implement IConstellationSource");
    }
}
