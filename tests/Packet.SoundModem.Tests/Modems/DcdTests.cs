using Packet.SoundModem.Hdlc;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

public class DcdTests
{
    private const int SampleRate = 12000;

    private static float[] Noise(int count, float sigma, Random random)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            samples[i] = sigma * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return samples;
    }

    private static float[] PacketAudio()
    {
        var frame = new byte[40];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(1).NextBytes(frame.AsSpan(16));
        return new Afsk1200Modulator(SampleRate).Modulate(
            HdlcFramer.FrameBits(frame, openingFlags: 30, closingFlags: 2));
    }

    [Fact]
    public void Channel_Busy_Tracks_A_Packet_Burst_And_Releases_After_It()
    {
        var random = new Random(2);
        float[] burst = PacketAudio();
        int lead = SampleRate; // 1 s of noise floor either side
        var audio = new float[lead + burst.Length + lead];
        Noise(audio.Length, 0.02f, random).CopyTo(audio, 0);
        for (int i = 0; i < burst.Length; i++)
        {
            audio[lead + i] += burst[i];
        }

        var demodulator = new Afsk1200Demodulator(SampleRate, _ => { });

        // Feed in blocks, recording the busy state timeline.
        var busyAt = new List<bool>();
        const int block = 120; // 10 ms
        for (int position = 0; position + block <= audio.Length; position += block)
        {
            demodulator.Process(audio.AsSpan(position, block));
            busyAt.Add(demodulator.ChannelBusy);
        }

        int burstStartBlock = lead / block;
        int burstEndBlock = (lead + burst.Length) / block;

        // Quiet channel before the burst (after floor seeding settles).
        busyAt.Take(burstStartBlock - 1).Skip(5).Should().NotContain(true, "leading noise is not busy");

        // Asserts within 100 ms of the burst starting (energy path; filters add ~20 ms).
        busyAt.Skip(burstStartBlock).Take(10).Should().Contain(true, "busy asserts quickly");

        // Solidly busy through the middle of the burst.
        int mid = (burstStartBlock + burstEndBlock) / 2;
        busyAt.Skip(mid).Take(10).Should().NotContain(false, "mid-burst is busy");

        // Releases within ~400 ms after the burst ends (hold + hysteresis + DCD decay).
        busyAt.Skip(burstEndBlock + 40).Take(20).Should().NotContain(true, "releases after the burst");
    }

    [Fact]
    public void Packet_Dcd_Asserts_On_Packet_Signals()
    {
        float[] burst = PacketAudio();
        var audio = new float[SampleRate / 2 + burst.Length];
        burst.CopyTo(audio, SampleRate / 2);

        var demodulator = new Afsk1200Demodulator(SampleRate, _ => { });
        demodulator.Process(audio);

        demodulator.CarrierDetect.Should().BeTrue("a packet signal was in progress at end of audio");
    }

    [Fact]
    public void A_Steady_Carrier_Is_Busy_But_Not_Packet_Dcd()
    {
        // A dead carrier / tone in the passband: the energy detector must flag it, the
        // packet DCD must not. This is exactly the case headless QtSoundModem misses
        // (its energy detector lives in the GUI waterfall path).
        var random = new Random(3);
        var audio = Noise(2 * SampleRate, 0.02f, random);
        for (int i = SampleRate; i < 2 * SampleRate; i++)
        {
            audio[i] += 0.5f * (float)Math.Sin(2 * Math.PI * 1700 * i / SampleRate);
        }

        var demodulator = new Afsk1200Demodulator(SampleRate, _ => { });
        demodulator.Process(audio);

        demodulator.ChannelBusy.Should().BeTrue("a strong in-band carrier is busy");
        demodulator.CarrierDetect.Should().BeFalse("a steady tone is not a packet signal");
    }

    [Fact]
    public void Noise_Alone_Is_Not_Busy()
    {
        var demodulator = new Afsk1200Demodulator(SampleRate, _ => { });
        demodulator.Process(Noise(3 * SampleRate, 0.05f, new Random(4)));

        demodulator.ChannelBusy.Should().BeFalse();
        demodulator.CarrierDetect.Should().BeFalse();
    }

    [Fact]
    public void Reset_Clears_Carrier_State()
    {
        var demodulator = new Afsk1200Demodulator(SampleRate, _ => { });
        demodulator.Process(PacketAudio());
        demodulator.ChannelBusy.Should().BeTrue();

        demodulator.ResetCarrierState();

        demodulator.ChannelBusy.Should().BeFalse();
        demodulator.CarrierDetect.Should().BeFalse();
    }

    [Fact]
    public void Bpsk_Channel_Busy_Tracks_A_Burst_Too()
    {
        byte[] ax25 = Convert.FromHexString("968264888AAEE4969668908A946F81");
        byte[] wire = Packet.SoundModem.Il2p.Il2pCodec.Encode(ax25, appendCrc: true);
        byte[] bits = Packet.SoundModem.Il2p.Il2pFramer.FrameBits(
            wire, 96, Packet.SoundModem.Il2p.Il2pFramer.PreambleStyle.Zeros);
        float[] burst = new BpskModulator(SampleRate).Modulate(bits);

        var random = new Random(5);
        var audio = Noise(SampleRate + burst.Length + SampleRate, 0.02f, random);
        for (int i = 0; i < burst.Length; i++)
        {
            audio[SampleRate + i] += burst[i];
        }

        var demodulator = new BpskDemodulator(SampleRate, _ => { });

        demodulator.Process(audio.AsSpan(0, SampleRate - 1200));
        demodulator.ChannelBusy.Should().BeFalse("quiet before the burst");

        demodulator.Process(audio.AsSpan(SampleRate - 1200, burst.Length));
        demodulator.ChannelBusy.Should().BeTrue("busy during the burst");

        demodulator.Process(audio.AsSpan(SampleRate - 1200 + burst.Length));
        demodulator.ChannelBusy.Should().BeFalse("released after the burst");
    }
}
