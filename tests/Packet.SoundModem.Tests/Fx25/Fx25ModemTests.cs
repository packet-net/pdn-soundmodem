using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Fx25;

public class Fx25ModemTests
{
    private const int SampleRate = 12000;

    private static byte[] SampleFrame()
    {
        var frame = new byte[45];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(9).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static float[] WithPadding(float[] audio)
    {
        var padded = new float[audio.Length + 2 * (SampleRate / 5)];
        audio.CopyTo(padded, SampleRate / 5);
        return padded;
    }

    [Fact]
    public void Fx25_Transmission_Decodes_Exactly_Once_On_An_Fx25_Receiver()
    {
        // A clean FX.25 block decodes via both the FX.25 and the embedded-HDLC paths;
        // the modem must deduplicate to a single delivery.
        byte[] frame = SampleFrame();
        var tx = new Afsk1200Modem(SampleRate, _ => { }, fx25: Fx25Mode.TransmitReceive);
        var frames = new List<byte[]>();
        var rx = new Afsk1200Modem(SampleRate, frames.Add, fx25: Fx25Mode.Receive);

        rx.Process(WithPadding(tx.Modulate(frame, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Fx25_Transmission_Still_Decodes_On_A_Plain_Receiver()
    {
        // Transparency: a non-FX.25 station reads the embedded HDLC frame.
        byte[] frame = SampleFrame();
        var tx = new Afsk1200Modem(SampleRate, _ => { }, fx25: Fx25Mode.TransmitReceive);
        var frames = new List<byte[]>();
        var rx = new Afsk1200Modem(SampleRate, frames.Add);

        rx.Process(WithPadding(tx.Modulate(frame, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public void Plain_Transmission_Decodes_On_An_Fx25_Receiver()
    {
        byte[] frame = SampleFrame();
        var tx = new Afsk1200Modem(SampleRate, _ => { });
        var frames = new List<byte[]>();
        var rx = new Afsk1200Modem(SampleRate, frames.Add, fx25: Fx25Mode.Receive);

        rx.Process(WithPadding(tx.Modulate(frame, txDelayMilliseconds: 200)));

        frames.Should().ContainSingle().Which.Should().Equal(frame);
    }
}
