using Packet.SoundModem.Modems;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Tests.Ms110d;

public class Ms110dModemTests
{
    private static byte[] TestFrame(int length, int seed)
    {
        var random = new Random(seed);
        var frame = new byte[length];
        random.NextBytes(frame);
        return frame;
    }

    [Theory]
    [InlineData(9600, 6)]
    [InlineData(48000, 6)]
    [InlineData(48000, 2)]
    public void Ax25_Frame_Round_Trips_Through_The_IModem_Surface(int sampleRate, int wn)
    {
        byte[] frame = TestFrame(120, 7 + wn);
        var received = new List<byte[]>();
        var modem = new Ms110dModem(
            sampleRate, received.Add, new Ms110dTxSettings { WaveformNumber = wn });

        float[] audio = modem.Modulate(frame, txDelayMilliseconds: 50);
        modem.Process(new float[sampleRate / 10]);
        modem.Process(audio);
        modem.Process(new float[sampleRate / 2]);

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void FrameDecoded_Reports_Il2p_Quality_And_Cfo()
    {
        byte[] frame = TestFrame(60, 3);
        var modem = new Ms110dModem(9600, _ => { });
        FrameQuality? quality = null;
        modem.FrameDecoded += (_, q) => quality = q;

        float[] audio = modem.Modulate(frame, 0);
        modem.Process(new float[1000]);
        modem.Process(audio);
        modem.Process(new float[4800]);

        quality.Should().NotBeNull();
        quality!.Value.Mode.Should().Be("ms110d-wn6");
        quality.Value.CrcValid.Should().BeTrue();
        quality.Value.FrequencyOffsetHz.Should().NotBeNull();
        Math.Abs(quality.Value.FrequencyOffsetHz!.Value).Should().BeLessThan(3);
    }

    [Fact]
    public void Receive_Is_Autobaud_Across_Waveform_Numbers()
    {
        // A WN6-configured modem must decode a WN1 transmission — the WID announces it.
        byte[] frame = TestFrame(40, 5);
        var txModem = new Ms110dModem(9600, _ => { }, new Ms110dTxSettings { WaveformNumber = 1 });
        var received = new List<byte[]>();
        var rxModem = new Ms110dModem(9600, received.Add, new Ms110dTxSettings { WaveformNumber = 6 });

        rxModem.Process(new float[1000]);
        rxModem.Process(txModem.Modulate(frame, 0));
        rxModem.Process(new float[4800]);

        received.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Noise_Only_Input_Produces_No_False_Frames()
    {
        // False-lock characterization at the modem surface (design §2.8 test 6, house
        // bar): 60 s of in-band noise must produce no frame and no burst.
        var received = new List<byte[]>();
        var modem = new Ms110dModem(9600, received.Add);
        var random = new Random(99);
        var noise = new float[9600 * 60];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)((random.NextDouble() - 0.5) * 0.4);
        }

        modem.Process(noise);
        received.Should().BeEmpty();
    }

    [Fact]
    public void Twelve_Kilohertz_Path_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new Ms110dModem(12000, _ => { }));
    }
}
