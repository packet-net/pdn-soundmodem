using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Modems;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// The channel's own burst-SNR instrument (rx-roadmap workstream 9): every frame decoded
/// through a <see cref="SoundModemChannel"/> carries the strength of the burst it arrived
/// on as <see cref="FrameQuality.SnrDb"/> - into the frame log, the journal line and the
/// KISS quality frame - with no waterfall configured at all. The number is the
/// band-tracker convention (in-band power over a rolling minimum floor), NOT the sim
/// ladder's 3 kHz reference; that distinction lives on the property's doc and is the
/// reason this suite never compares the figure against a mask row.
/// </summary>
public class BurstSnrTests
{
    private const int SampleRate = 12000;

    private static float[] Burst(double amplitude = 1.0)
    {
        float[] burst = ModemCatalog.Create("bpsk300", SampleRate, static _ => { })
            .Modulate(Il2pReceiverTests.Gb7bpqBeacon(), txDelayMilliseconds: 300);
        for (int n = 0; n < burst.Length; n++)
        {
            burst[n] = (float)(burst[n] * amplitude);
        }

        return burst;
    }

    /// <summary>Low-level noise so the tracker has a genuine floor to bank - digital
    /// silence would also work, but a real station never hears one.</summary>
    private static float[] Noise(int samples, double amplitude = 0.001)
    {
        var random = new Random(9);
        var noise = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            noise[n] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        }

        return noise;
    }

    [Fact]
    public void A_Decoded_Frame_Carries_The_Strength_Of_Its_Burst()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
        var qualities = new List<FrameQuality>();
        channel.FrameReceivedWithQuality += (_, _, quality) => qualities.Add(quality);

        // Two seconds of quiet to bank the noise floor, then a strong burst.
        channel.ProcessReceive(Noise(SampleRate * 2));
        channel.ProcessReceive(Burst());
        channel.ProcessReceive(Noise(SampleRate / 2));

        qualities.Should().NotBeEmpty("the burst decodes");
        FrameQuality quality = qualities[0];
        quality.SnrDb.Should().NotBeNull("the frame is attributable to visible band energy");
        quality.SnrDb!.Value.Should().BeGreaterThan(20,
            "a full-scale burst over a -60 dB floor is a very strong signal on any convention");
    }

    [Fact]
    public void A_Quiet_Band_Yields_No_Figure_Rather_Than_A_Guess()
    {
        var channel = new SoundModemChannel(SampleRate, randomSeed: 5);
        channel.AddModem(0, sink => ModemCatalog.Create("bpsk300", SampleRate, sink));
        var qualities = new List<FrameQuality>();
        channel.FrameReceivedWithQuality += (_, _, quality) => qualities.Add(quality);

        // The burst opens the stream cold: the tracker's floor warms up from the burst's
        // own level, so no run ever forms and the honest answer is null, not zero. The
        // trailing quiet is the demodulator's flush, not a floor for the tracker - the
        // decode lands before a fresh floor could reclassify anything.
        channel.ProcessReceive(Burst());
        channel.ProcessReceive(Noise(SampleRate / 2));

        qualities.Should().NotBeEmpty("the burst still decodes - SNR is attribution, not gating");
        qualities[0].SnrDb.Should().BeNull(
            "a frame that cannot be attributed to visible energy carries no figure");
    }
}
