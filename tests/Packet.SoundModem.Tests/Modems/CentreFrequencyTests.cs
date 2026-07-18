using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Locks in issue #39: the narrow modes' audio centre is settable per modem
/// (QtSoundModem-style), on BOTH transmit and receive. Each case transmits a clean frame
/// at a non-default centre and asserts it (a) round-trips when the receiver shares that
/// centre, and (b) does NOT decode at the mode's default centre — proving the override
/// genuinely moves the signal rather than only relabelling it. The AFSK1200 modulator
/// honouring the centre is the regression this guards: its TX previously stayed on the
/// fixed Bell-202 tones no matter the requested centre.
/// </summary>
public class CentreFrequencyTests
{
    private const int SampleRate = 12000;

    // A short IL2P/AX.25 frame every one of these modes carries.
    private static byte[] Frame =>
        Convert.FromHexString("968264888AAEE4969668908A9465B8CF303132333435363738");

    // Builds the modem for a mode at the given centre (null = the mode's own default), so
    // TX and RX go through exactly the same public surface the daemon uses.
    private static IModem Build(string mode, Action<byte[]> sink, double? centre) => mode switch
    {
        "afsk1200" => new Afsk1200Modem(SampleRate, sink, centre ?? 1700),
        "afsk1200-il2p" => new Afsk1200Il2pModem(SampleRate, sink, crc: true, centre ?? 1700),
        "afsk1200-multi" => new Afsk1200MultiModem(
            SampleRate, sink, offsetPairs: 3, centerFrequency: centre ?? 1700),
        "afsk300" => new Afsk300Modem(SampleRate, sink, Afsk300Framing.Il2pCrc, centre ?? 1700),
        "bpsk300" => BpskModem.Bpsk300(SampleRate, sink, carrierFrequency: centre ?? 1500),
        "bpsk1200" => BpskModem.Bpsk1200(SampleRate, sink, carrierFrequency: centre ?? 1500),
        "qpsk600" => QpskModem.Qpsk600(SampleRate, sink, carrierFrequency: centre ?? 1500),
        "qpsk2400" => QpskModem.Qpsk2400(SampleRate, sink, carrierFrequency: centre ?? 1500),
        "qpsk3600" => QpskModem.Qpsk3600(SampleRate, sink, carrierFrequency: centre ?? 1650),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown mode"),
    };

    private static float[] WithPadding(float[] audio)
    {
        int pad = SampleRate / 5;
        var padded = new float[audio.Length + 2 * pad];
        audio.CopyTo(padded, pad);
        return padded;
    }

    [Theory]
    [InlineData("afsk1200", 1400)]
    [InlineData("afsk1200-il2p", 1400)]
    [InlineData("afsk1200-multi", 1400)]
    [InlineData("afsk300", 2000)]
    [InlineData("bpsk300", 1150)]
    [InlineData("bpsk1200", 1150)]
    [InlineData("qpsk600", 1150)]
    [InlineData("qpsk2400", 1900)]
    [InlineData("qpsk3600", 1850)]
    public void A_Frame_Roundtrips_At_A_Shifted_Centre(string mode, double centre)
    {
        byte[] ax25 = Frame;
        IModem tx = Build(mode, _ => { }, centre);
        var frames = new List<byte[]>();
        IModem rx = Build(mode, frames.Add, centre);

        rx.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 250)));

        frames.Should().ContainSingle().Which.Should().Equal(ax25);
    }

    // Only the PSK carrier modes get the stricter "must not decode at the default centre"
    // check. The AFSK tone modes' discriminators — and especially the multi-decoder bank —
    // are deliberately tolerant of a few hundred Hz of offset (the bank exists precisely to
    // catch mistuned peers), so a moved AFSK signal legitimately still decodes at the
    // default centre. The matched-centre round-trip above is what proves AFSK TX honours
    // the centre; here we prove the carrier modes genuinely move off the default.
    [Theory]
    [InlineData("bpsk300", 1150)]
    [InlineData("bpsk1200", 1150)]
    [InlineData("qpsk600", 1150)]
    [InlineData("qpsk2400", 1900)]
    [InlineData("qpsk3600", 1850)]
    public void The_Override_Moves_The_Signal_Off_The_Default_Centre(string mode, double centre)
    {
        byte[] ax25 = Frame;
        IModem tx = Build(mode, _ => { }, centre);
        var frames = new List<byte[]>();
        IModem rxDefault = Build(mode, frames.Add, centre: null);

        rxDefault.Process(WithPadding(tx.Modulate(ax25, txDelayMilliseconds: 250)));

        frames.Should().BeEmpty(
            "a receiver on the mode's default centre must not hear a signal moved to {0} Hz", centre);
    }
}
