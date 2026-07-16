using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Dsp;

/// <summary>
/// Every transmitter in this library, measured against the occupied bandwidth its mode is
/// allowed. A modem that splatters is a defect whether or not it decodes, and no loopback
/// can see it: every functional test, the Dire Wolf cross-validation and the WA8LMF
/// benchmark all passed while 1200 sym/s QPSK measured 5344 Hz of 99 % OBW against a
/// published 2400 Hz. Only measuring the spectrum against a real NinoTNC caught it.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, both regression guards rather than post-mortems:
/// </para>
/// <list type="number">
///   <item>Never exceed the published figure for the equivalent NinoTNC mode
///         (release-notes.txt, "MODE SWITCH MAPPING v3/4.43"). Where Nino publishes none,
///         the limit is the voice channel the mode rides through (3 kHz) — stated, not
///         inferred.</item>
///   <item><b>Never be wider than a NinoTNC actually is for the same mode.</b> The
///         published figures are ceilings, not descriptions: mode 12 is published at
///         500 Hz and measures 305. Sharing a channel with a TNC that is narrower than us
///         is our problem, not the channel's.</item>
/// </list>
/// <para>
/// The reference numbers in <see cref="NinoTncMeasured"/> are from a real NinoTNC
/// (firmware 3.44) recorded on the CM108 bench loop — see docs/ninotnc-loop.md. They are
/// measured through a 48 kHz codec, which does nothing to a 300-2500 Hz signal, and at
/// ~40 dB SNR, where noise sits far below the 0.5 % tails, so they compare fairly with our
/// synthesised waveform.
/// </para>
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

    /// <summary>
    /// The modes and NinoTNC reference recordings for the never-wider rule. The reference
    /// is MEASURED FROM THE CHECKED-IN RECORDING AT TEST TIME (samples/ninotnc/, firmware
    /// 3.44 on the CM108 loop) rather than pinned as a constant — and, critically, both
    /// sides are measured the same way: whole burst, same 40-byte bench frame the
    /// recording carries, same TXDELAY. The first version of this table compared our
    /// data-section spectrum against the highest-energy window of his short frames (mostly
    /// preamble, which is narrower than data) and mis-read qpsk3600 as "9 % wider than the
    /// TNC"; like-for-like it is narrower (issue #2).
    /// </summary>
    public static TheoryData<string, string> NinoTncReferenceRecordings => new()
    {
        { "afsk1200", "afsk1200.wav" },
        { "afsk1200-il2p", "afsk1200-il2p.wav" },
        { "afsk300", "afsk300.wav" },
        { "afsk300-il2pc", "afsk300-il2pc.wav" },
        { "bpsk300", "bpsk300.wav" },
        { "qpsk600", "qpsk600.wav" },
        { "bpsk1200", "bpsk1200.wav" },
        { "qpsk2400", "qpsk2400.wav" },
        { "qpsk3600", "qpsk3600.wav" },
    };

    [Theory]
    [MemberData(nameof(NinoTncReferenceRecordings))]
    public void Transmitters_Are_Never_Wider_Than_A_NinoTNC_In_The_Same_Mode(string mode, string recording)
    {
        string path = Path.Combine(FindRepoRoot(), "samples", "ninotnc", recording);
        double reference = WholeBurstObw(LoadFirstBurst(path), 48000);

        IModem tx = Create(mode);
        float[] audio = tx.Modulate(BenchFrame(mode), txDelayMilliseconds: 300);
        double ours = WholeBurstObw(Trim(audio), SampleRate);

        // 10 % tolerance, sized to the scatter the references themselves demonstrate
        // (the same NinoTNC modulator measures 305-328 Hz across its two 300 AFSK modes).
        ours.Should().BeLessThanOrEqualTo(
            reference * 1.10,
            "a real NinoTNC transmits mode '{0}' in {1:F0} Hz (measured from {2}) and we share its channel",
            mode, reference, recording);
    }

    private static byte[] BenchFrame(string mode)
    {
        // The exact frame shape the reference recordings carry (nino-bench MakeFrame).
        var payload = new byte[40];
        byte[] tag = System.Text.Encoding.UTF8.GetBytes($"BENCH {mode} #0000 ");
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = i < tag.Length ? tag[i] : (byte)('A' + (i % 26));
        }

        byte[] header = [0x9C, 0x92, 0x9C, 0x9E, 0x40, 0x40, 0xE0, 0x84, 0x8A, 0x9C, 0x86, 0x90, 0x40, 0x63, 0x03, 0xF0];
        return [.. header, .. payload];
    }

    private static float[] LoadFirstBurst(string path)
    {
        var (raw, rate) = Packet.SoundModem.Audio.WavFile.ReadMono(path);
        rate.Should().Be(48000, "the reference recordings are 48 kHz rig captures");
        int window = rate / 100, start = -1;
        for (int i = 0; i + window < raw.Length; i += window)
        {
            float peak = 0;
            for (int k = i; k < i + window; k++)
            {
                peak = Math.Max(peak, Math.Abs(raw[k]));
            }

            if (peak > 0.03f && start < 0)
            {
                start = i;
            }

            if (peak <= 0.03f && start >= 0)
            {
                if (i - start > rate / 10)
                {
                    return raw[start..i];
                }

                start = -1;
            }
        }

        throw new InvalidOperationException($"no burst found in {path}");
    }

    private static float[] Trim(float[] audio)
    {
        int a = 0, b = audio.Length - 1;
        while (a < audio.Length && Math.Abs(audio[a]) < 0.02f)
        {
            a++;
        }

        while (b > a && Math.Abs(audio[b]) < 0.02f)
        {
            b--;
        }

        return audio[a..(b + 1)];
    }

    /// <summary>Whole-burst 99 % OBW. The FFT size scales with the rate so both sides
    /// are measured at the same ~12 Hz bin resolution — comparing spectra measured at
    /// different resolutions is the same class of error this test exists to prevent.</summary>
    private static double WholeBurstObw(float[] burst, int rate) =>
        OccupiedBandwidth.Measure(burst, rate, fftSize: rate == 48000 ? 4096 : 1024).WidthHz;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
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
        foreach ((string label, IModem modem, int baud, double bound) in new (string, IModem, int, double)[]
        {
            ("fsk9600", FskModem.Fsk9600(48000, _ => { }, FskFraming.ClassicHdlc), 9600, 0.75),
            ("fsk4800", FskModem.Fsk4800(48000, _ => { }), 4800, 0.75),
            // C4FSK is shaped at 1.0x its SYMBOL rate (a 4-level eye cannot take the
            // 0.55x squeeze — measured 0/8 vs 8/8), so its bound is per symbol rate.
            ("c4fsk9600", C4fskModem.C4fsk9600(48000, _ => { }), 4800, 1.4),
            ("c4fsk19200", C4fskModem.C4fsk19200(48000, _ => { }), 9600, 1.4),
        })
        {
            float[] audio = modem.Modulate(Frame(), txDelayMilliseconds: 300);
            var (_, hi, _, _) = OccupiedBandwidth.Measure(audio.AsSpan(audio.Length / 3), 48000);
            hi.Should().BeLessThan(baud * bound, "{0}'s baseband is shaped per its symbol rate", label);
        }
    }
}
