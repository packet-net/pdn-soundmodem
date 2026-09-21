using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// Does more deviation buy anything, and where does it stop?
/// </summary>
/// <remarks>
/// An FM discriminator's output noise is set by the IF; the recovered signal amplitude is
/// proportional to deviation. So post-discriminator SNR should rise as deviation squared, until the
/// signal outgrows the IF filter. Carrier-to-noise is held fixed in a fixed IF bandwidth, so every
/// row is the same received power into the same radio, differing only in how hard it is driven.
/// </remarks>
public class DeviationSweepProbe
{
    [Fact]
    public void How_Far_Does_More_Deviation_Take_Us()
    {
        if (Environment.GetEnvironmentVariable("OFDMFM_LADDER") is null)
        {
            return;
        }

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        profile = profile with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        };
        var codec = new OfdmFmBurstCodec(profile);
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s)
            ? s
            : 32;

        foreach (double ifBw in (ReadOnlySpan<double>)[7800, 12600])
        {
            Console.WriteLine($"--- IF {ifBw / 1000:0.0} kHz, {seeds} seeds ---");
            Console.WriteLine("| peak dev | +14 | +12 | +10 | +8 | +6 |");

            foreach (int dev in (ReadOnlySpan<int>)[1500, 2500, 4000, 5000, 6900])
            {
                var row = new List<string>();
                foreach (int cnr in (ReadOnlySpan<int>)[14, 12, 10, 8, 6])
                {
                    int ok = 0;
                    for (int seed = 0; seed < seeds; seed++)
                    {
                        var payload = new byte[64];
                        new Random(4000 + seed).NextBytes(payload);
                        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk);
                        var link = FmLinkProfile.DataPort(dev, ifBandwidthHz: ifBw);
                        float[] heard = new FmChannel(link, profile.SampleRate, seed).Apply(clean, cnr);
                        byte[]? got = codec.Demodulate(heard)?.Payload;
                        if (got is not null && got.AsSpan().SequenceEqual(payload))
                        {
                            ok++;
                        }
                    }

                    row.Add($"{ok,3}");
                }

                Console.WriteLine($"| {dev,5} Hz | {string.Join(" | ", row)} |");
            }
        }
    }
}
