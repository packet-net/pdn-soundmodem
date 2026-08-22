using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Packet DCD on the differential QPSK path through the public API, polled per 100 ms block
/// the way the daemon's carrier sense polls it - the gate of issue #329. Before that issue
/// DCD asserted for a qpsk600 burst only with the carrier on frequency and noise present:
/// never on a clean burst, never under a bank-step offset, never on the 16-symbol
/// zero-TXDELAY preamble, while the frame decoded in every one of those cases.
/// </summary>
public class QpskDcdTests
{
    private const int Rate = 12000;
    private const int Baud = 300;
    private static readonly byte[] Frame = Enumerable.Range(0, 60).Select(i => (byte)(i * 7 + 3)).ToArray();

    private static QpskModem Modem(Action<byte[]> sink, double carrierOffsetHz = 0) =>
        QpskModem.Qpsk600(Rate, sink, detector: PskDetector.Differential, carrierFrequency: 1500 + carrierOffsetHz);

    /// <summary>Noise sigma for an SNR in a 3 kHz bandwidth against the burst's mean power.</summary>
    private static double Sigma(float[] burst, double snrDb)
    {
        double power = 0;
        foreach (float s in burst)
        {
            power += s * (double)s;
        }

        return Math.Sqrt(2 * (power / burst.Length) / Math.Pow(10, snrDb / 10));
    }

    private static void AddNoise(float[] samples, double sigma, Random random)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            samples[i] += (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }

    /// <summary>One burst in a 2.5 s window of noise: frames decoded, DCD blocks of 25, and
    /// the DCD timeline in symbols (first assert from the burst start, last assert from its end).</summary>
    private static (int Frames, int DcdBlocks, int FirstSymbol, int ReleaseSymbol) Run(
        double snrDb, int txDelayMs, double carrierOffsetHz)
    {
        float[] burst = Modem(_ => { }, carrierOffsetHz).Modulate(Frame, txDelayMs);
        int leadIn = Rate / 2;
        var samples = new float[(int)(2.5 * Rate)];
        burst.CopyTo(samples, leadIn);
        AddNoise(samples, Sigma(burst, snrDb), new Random(1));

        int frames = 0;
        QpskModem rx = Modem(_ => frames++);
        int symbol = Rate / Baud;
        int burstEnd = leadIn + burst.Length;
        int dcdBlocks = 0, first = -1, last = -1;
        for (int pos = 0; pos < samples.Length; pos += symbol)
        {
            rx.Process(samples.AsSpan(pos, symbol));
            int end = pos + symbol;
            if (rx.CarrierDetect)
            {
                first = first < 0 ? end : first;
                last = end;
                if (end % (Rate / 10) == 0)
                {
                    dcdBlocks++;
                }
            }
        }

        return (frames, dcdBlocks, first < 0 ? -1 : (first - leadIn) / symbol, last < 0 ? -1 : (last - burstEnd) / symbol);
    }

    [Theory]
    [InlineData(8, 150, 0)]
    [InlineData(8, 150, -7.5)]
    [InlineData(8, 150, 7.5)]
    [InlineData(8, 150, -15)]
    [InlineData(8, 150, 15)]
    [InlineData(8, 0, 0)]
    [InlineData(40, 150, 0)]
    [InlineData(60, 150, 0)]
    [InlineData(0, 150, 0)]
    public void A_Decodable_Burst_Asserts_Dcd_And_Releases_After_It(double snrDb, int txDelayMs, double offsetHz)
    {
        (int frames, int dcdBlocks, int first, int release) = Run(snrDb, txDelayMs, offsetHz);

        frames.Should().Be(1, "the burst decodes in every one of these conditions");
        dcdBlocks.Should().BeGreaterThanOrEqualTo(10,
            "a burst of some 440 symbols holds DCD for most of its 14 blocks");
        first.Should().BeInRange(0, 250,
            "DCD asserts within the burst, early on frequency and later for a lone modem two bank steps out");
        release.Should().BeInRange(0, 160,
            "DCD releases within a few memories of the burst ending: 60 to 130 symbols measured, the noise floor setting the spread");
    }

    [Fact]
    public void Noise_Alone_Never_Asserts_Dcd()
    {
        float[] burst = Modem(_ => { }).Modulate(Frame, 150);
        var random = new Random(7);
        QpskModem rx = Modem(_ => { });
        var block = new float[Rate / 10];
        foreach (double snrDb in new[] { 8.0, 0.0, -10.0 })
        {
            double sigma = Sigma(burst, snrDb);
            for (int b = 0; b < 100; b++)
            {
                Array.Clear(block);
                AddNoise(block, sigma, random);
                rx.Process(block);
                rx.CarrierDetect.Should().BeFalse("noise at the {0} dB row's level is not a carrier (block {1})", snrDb, b);
            }
        }
    }

    [Fact]
    public void Reset_Drops_Dcd_At_Once()
    {
        float[] burst = Modem(_ => { }).Modulate(Frame, 150);
        QpskModem rx = Modem(_ => { });
        rx.Process(burst.AsSpan(0, burst.Length / 2));
        rx.CarrierDetect.Should().BeTrue("half a clean burst is plenty");
        rx.ResetCarrierState();
        rx.CarrierDetect.Should().BeFalse("our own transmitter keying drops it");
    }
}
