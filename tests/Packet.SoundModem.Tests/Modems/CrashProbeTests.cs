using M0LTE.Il2p;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Modems;

public class CrashProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void Probe_Mark_Statistics()
    {
        const int Rate = 12000;
        IModem tx = ModemCatalog.Create("bpsk300", Rate, static _ => { },
            new Packet.SoundModem.Modems.ModemOptions(OffsetPairs: 0));
        byte[] frame = new byte[60];
        new Random(1).NextBytes(frame);
        float[] burst = tx.Modulate(frame, 100);

        for (int seed = 1; seed <= 10; seed++)
        {
            var channel = new WattersonChannel(Rate, seed, []);
            float[] audio = channel.Apply(burst, snrDb: 2.0, noiseBandwidthHz: 3000,
                leadInSamples: Rate / 2, leadOutSamples: Rate,
                impulses: new ImpulseNoiseProfile(120));

            int frames = 0, bits = 0, marked = 0;
            var deframer = new Il2pDeframer((f, _) => frames++, crcMode: true);
            var demod = new BpskDemodulator(Rate, static _ => { },
                softBitSink: (bit, conf) =>
                {
                    bits++;
                    if (conf == 0f) marked++;
                    deframer.PushBit(bit, conf);
                });
            demod.Process(audio);
            output.WriteLine($"seed {seed}: bits {bits}, marked {marked} " +
                $"({100.0 * marked / Math.Max(1, bits):F1} %), decoded {frames}");
        }
    }
}
