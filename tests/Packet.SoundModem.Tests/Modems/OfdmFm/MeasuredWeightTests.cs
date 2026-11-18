using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The clock-tracked read weights each carrier's soft bits by the noise it measured there, and
/// that measurement has a blind spot these tests hold shut.
/// </summary>
public class MeasuredWeightTests
{
    /// <summary>
    /// Through the simulated data-port link near the default preset's cliff, where the top of the
    /// band is tens of decibels worse than the bottom.
    /// </summary>
    /// <remarks>
    /// <para>The noise on a carrier is measured from how far its points sit from the points they
    /// were decided to be, and a wrong decision hides its own error: the figure cannot exceed the
    /// size of a decision cell however bad the carrier is. On the bench the weighting was first
    /// measured on, the worst carrier has 23 dB and nothing saturates. On this link the edge
    /// carriers do, and weighted on the saturated figure they were trusted nearly as much as the
    /// good ones: the tracked read decoded 12 of 24 bursts at this level where the shipped
    /// channel-power weight decoded 22. Two things answer it. A saturated carrier is held to
    /// what the channel-power model says of it, and a tracked read that still fails is decoded
    /// once more on channel-power weights alone. With both, the tracked read decodes 47 of 48
    /// here; with neither, 8 of these 16.</para>
    /// <para>Sixteen bursts, and a floor two below what they measure, so the test pins the
    /// behaviour without pinning a noise realisation.</para>
    /// </remarks>
    [Fact]
    public void Measured_Weights_Do_Not_Cost_Bursts_Where_The_Band_Edges_Are_Far_Down()
    {
        var codec = new OfdmFmBurstCodec(OfdmFmPresets.Default8k, OfdmFmPresets.Table);
        FmLinkProfile link = FmLinkProfile.DataPort(4500, ifBandwidthHz: 15000);
        int decoded = 0;
        for (int seed = 0; seed < 16; seed++)
        {
            var payload = new byte[1024];
            new Random(7000 + seed).NextBytes(payload);
            float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qam64, 2);
            float[] heard = new FmChannel(link, 48000, seed).Apply(clean, 30);
            byte[]? got = codec.Demodulate(heard)?.Payload;
            if (got is not null && got.AsSpan().SequenceEqual(payload))
            {
                decoded++;
            }
        }

        decoded.Should().BeGreaterThanOrEqualTo(
            14, "the measured weights must not lose bursts the channel-power weight decodes");
    }
}
