using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Dsp;

/// <summary>
/// Every transmitter in this library, measured against the occupied bandwidth its mode is
/// allowed. A modem that splatters is a defect whether or not it decodes, and it is not
/// something a loopback test can see — this suite exists because ours did splatter, for
/// months, while every functional test passed: 1200 sym/s QPSK measured 5344 Hz of 99 %
/// OBW against Nino's published 2400 Hz, and only a spectrum measurement against a real
/// NinoTNC caught it.
/// </summary>
/// <remarks>
/// Limits are Nino's published figures for the equivalent NinoTNC mode (release-notes.txt,
/// "MODE SWITCH MAPPING v3/4.43"), because those modes are what these modems interoperate
/// with and share channels with. Where Nino publishes none, the limit is the voice channel
/// these modes ride through (3 kHz) — stated, not inferred.
/// </remarks>
public class OccupiedBandwidthTests
{
    private const int SampleRate = 12000;

    private static byte[] Frame()
    {
        // A random payload, so the measurement sees representative symbol statistics
        // rather than the spectrum of one lucky pattern.
        var frame = new byte[16 + 300];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(7).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static double MeasureObw(IModem modem)
    {
        float[] audio = modem.Modulate(Frame(), txDelayMilliseconds: 300);

        // Skip the preamble: it is a fixed pattern, and the figure we owe the channel is
        // for the modulation the payload actually produces.
        int skip = Math.Min(audio.Length / 3, SampleRate / 2);
        return OccupiedBandwidth.Measure(audio.AsSpan(skip), SampleRate).WidthHz;
    }

    public static TheoryData<string, double> PublishedLimits => new()
    {
        // NinoTNC "SSB AFSK" modes 12/13/14 — Nino: "Filtered for 500 Hz occupied
        // bandwidth". Ours needs its own band-limiting to hold this; raw phase-continuous
        // FSK on these tones measures ~519 Hz.
        { "afsk300", 500 },
        { "afsk300-il2p", 500 },
        { "afsk300-il2pc", 500 },

        // "SHAPED PSK MODES" — Nino: "300 BPSK, 600 QPSK send 300 symbols/sec, 500 Hz
        // OBW. 1200 BPSK, 2400 QPSK send 1200 symbols/sec, 2400 Hz OBW."
        { "bpsk300", 500 },
        { "qpsk600", 500 },
        { "bpsk1200", 2400 },
        { "qpsk2400", 2400 },
    };

    public static TheoryData<string, double> VoiceChannelModes => new()
    {
        // No published OBW for these. They are Nino's "FM AFSK MODES", fed through a
        // radio's mic input, so the honest limit is the voice channel they ride: 3 kHz.
        { "qpsk3600", 3000 },
        { "afsk1200", 3000 },
        { "afsk1200-il2p", 3000 },
    };

    private static IModem Create(string mode) => mode switch
    {
        "afsk300" => new Afsk300Modem(SampleRate, _ => { }, Afsk300Framing.Ax25),
        "afsk300-il2p" => new Afsk300Modem(SampleRate, _ => { }, Afsk300Framing.Il2p),
        "afsk300-il2pc" => new Afsk300Modem(SampleRate, _ => { }, Afsk300Framing.Il2pCrc),
        "bpsk300" => BpskModem.Bpsk300(SampleRate, _ => { }),
        "qpsk600" => QpskModem.Qpsk600(SampleRate, _ => { }),
        "bpsk1200" => BpskModem.Bpsk1200(SampleRate, _ => { }),
        "qpsk2400" => QpskModem.Qpsk2400(SampleRate, _ => { }),
        "qpsk3600" => QpskModem.Qpsk3600(SampleRate, _ => { }),
        "afsk1200" => new Afsk1200Modem(SampleRate, _ => { }),
        "afsk1200-il2p" => new Afsk1200Il2pModem(SampleRate, _ => { }),
        _ => throw new ArgumentException($"unknown mode '{mode}'", nameof(mode)),
    };

    [Theory]
    [MemberData(nameof(PublishedLimits))]
    public void Transmitters_Hold_The_Occupied_Bandwidth_Their_Mode_Publishes(string mode, double limitHz)
    {
        MeasureObw(Create(mode)).Should().BeLessThanOrEqualTo(
            limitHz,
            "mode '{0}' shares its channel with NinoTNCs held to {1} Hz", mode, limitHz);
    }

    [Theory]
    [MemberData(nameof(VoiceChannelModes))]
    public void Transmitters_Without_A_Published_Figure_Fit_A_Voice_Channel(string mode, double limitHz)
    {
        MeasureObw(Create(mode)).Should().BeLessThanOrEqualTo(
            limitHz, "mode '{0}' is fed through a radio's mic input", mode);
    }

    [Fact]
    public void The_Meter_Agrees_With_A_Known_Signal()
    {
        // Guards the measurement itself: a meter that reads low would pass every test
        // above while we splattered. A pure tone occupies ~nothing; two tones 400 Hz apart
        // occupy ~400 Hz. Validated in the field too — this method reads a real NinoTNC's
        // mode-11 transmission at 1887 Hz against its published 2400 Hz.
        var tone = new float[SampleRate];
        var pair = new float[SampleRate];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 1500 * i / SampleRate);
            pair[i] = 0.4f * ((float)Math.Sin(2 * Math.PI * 1300 * i / SampleRate)
                            + (float)Math.Sin(2 * Math.PI * 1700 * i / SampleRate));
        }

        OccupiedBandwidth.Measure(tone, SampleRate).WidthHz.Should().BeLessThan(30);

        var (lo, hi, width, _) = OccupiedBandwidth.Measure(pair, SampleRate);
        width.Should().BeInRange(380, 420);
        lo.Should().BeApproximately(1300, 20);
        hi.Should().BeApproximately(1700, 20);
    }

    [Fact]
    public void Direct_Fsk_Baseband_Stays_Inside_Its_Shaping_Filter()
    {
        // The 9600/4800 GFSK modes are NOT audio-band: they drive a varactor/discriminator
        // and Nino's 20 kHz / 10 kHz figures are the RF bandwidth after FM modulation, so
        // they are not comparable with the numbers above. What is worth pinning is that
        // our baseband stays inside the 0.55·baud pulse-shaping filter it is built with.
        foreach ((string label, IModem modem, int baud) in new (string, IModem, int)[]
        {
            ("fsk9600", FskModem.Fsk9600(48000, _ => { }, FskFraming.ClassicHdlc), 9600),
            ("fsk4800", FskModem.Fsk4800(48000, _ => { }), 4800),
        })
        {
            float[] audio = modem.Modulate(Frame(), txDelayMilliseconds: 300);
            var (_, hi, _, _) = OccupiedBandwidth.Measure(audio.AsSpan(audio.Length / 3), 48000);
            hi.Should().BeLessThan(baud * 0.75, "{0}'s baseband is shaped at 0.55x baud", label);
        }
    }
}
