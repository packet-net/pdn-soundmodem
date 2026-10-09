using Packet.SoundModem.Daemon;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Mailcast;

/// <summary>
/// The receive-side shift mailcast uses instead of asking the generic band-plan decorator
/// (<c>FrequencyShiftedModem</c>) to move the modem: bandpass to the signal's own width, then
/// shift the audio itself to the modem's native centre, so the modem it feeds never leaves that
/// centre and never reaches the generic decorator's DC/Nyquist guard.
/// </summary>
public sealed class MailcastReceiveShiftTests
{
    private const int Rate = 48000;

    /// <summary>Records whatever is fed to it; nothing else about a real modem matters here.</summary>
    private sealed class RecordingModem : IModem
    {
        public string Mode => "fake";

        public event Action<byte[], FrameQuality>? FrameDecoded
        {
            add { }
            remove { }
        }

        public bool CarrierDetect => false;

        public bool ChannelBusy => false;

        public List<float> Recorded { get; } = [];

        public void Process(ReadOnlySpan<float> samples) => Recorded.AddRange(samples.ToArray());

        public float[] Modulate(ReadOnlySpan<byte> ax25Frame, int txDelayMilliseconds) =>
            throw new NotSupportedException("this fake only records what it is fed");

        public void ResetCarrierState()
        {
        }
    }

    [Fact]
    public void Inactive_At_The_Modems_Own_Native_Centre()
    {
        new MailcastReceiveShift(Rate, 1800, nativeCentreHz: 1800, halfWidthHz: 1450).Active.Should().BeFalse();
    }

    [Theory]
    [InlineData(1278.0)] // the FT-450D worked example
    [InlineData(2000.0)] // the sound-card band's high end
    public void Active_Away_From_The_Modems_Own_Native_Centre(double centreHz)
    {
        new MailcastReceiveShift(Rate, centreHz, nativeCentreHz: 1800, halfWidthHz: 1450).Active.Should().BeTrue();
    }

    [Fact]
    public void When_Inactive_Process_Feeds_The_Modem_Unchanged()
    {
        var shift = new MailcastReceiveShift(Rate, 1800, nativeCentreHz: 1800, halfWidthHz: 1450);
        var modem = new RecordingModem();
        float[] samples = [0.1f, -0.2f, 0.3f, -0.4f];

        shift.Process(samples, modem);

        modem.Recorded.Should().Equal(samples);
    }

    [Theory]
    [InlineData(1278.0)]
    [InlineData(1500.0)]
    [InlineData(2000.0)]
    public void A_Tone_At_The_Signal_Centre_Lands_On_The_Modems_Native_Centre(double centreHz)
    {
        const double NativeHz = 1800;
        var shift = new MailcastReceiveShift(Rate, centreHz, NativeHz, halfWidthHz: 1450);
        var modem = new RecordingModem();

        // Plenty of settling room for the two 639-tap FIRs (bandpass then Hilbert shift), then
        // plenty more for a clean spectrum once they have settled.
        const int Total = 16384;
        var tone = new float[Total];
        for (int i = 0; i < Total; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * centreHz * i / Rate);
        }

        shift.Process(tone, modem);

        float[] settled = modem.Recorded.Skip(Total - 8192).ToArray();
        (double peakHz, double peakDb, double nextDb) = DominantTone(settled);
        peakHz.Should().BeApproximately(NativeHz, 15);
        (peakDb - nextDb).Should().BeGreaterThan(20, "the shifted tone should dominate everything else, including any image");
    }

    /// <summary>The strongest bin of a Hann-windowed DFT, its power in dB, and the next
    /// strongest bin at least 5 bins away (so the peak's own skirt is not mistaken for a rival).</summary>
    private static (double PeakHz, double PeakDb, double NextDb) DominantTone(float[] samples)
    {
        int n = samples.Length;
        var windowed = new double[n];
        for (int i = 0; i < n; i++)
        {
            windowed[i] = samples[i] * (0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (n - 1))));
        }

        double binHz = Rate / (double)n;
        var power = new double[n / 2];
        for (int k = 1; k < n / 2; k++)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = -2 * Math.PI * k * i / n;
                re += windowed[i] * Math.Cos(angle);
                im += windowed[i] * Math.Sin(angle);
            }

            power[k] = (re * re) + (im * im);
        }

        int peakBin = Array.IndexOf(power, power.Max());
        double nextPower = 0;
        for (int k = 1; k < power.Length; k++)
        {
            if (Math.Abs(k - peakBin) > 5)
            {
                nextPower = Math.Max(nextPower, power[k]);
            }
        }

        return (peakBin * binHz, 10 * Math.Log10(power[peakBin]), 10 * Math.Log10(nextPower));
    }
}
