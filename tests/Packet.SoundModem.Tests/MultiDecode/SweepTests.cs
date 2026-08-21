using AwesomeAssertions;
using M0LTE.Dsp;
using Packet.SoundModem.Modems;
using Packet.SoundModem.MultiDecode;

namespace Packet.SoundModem.Tests.MultiDecode;

/// <summary>
/// The pdn-decode sweep, against every packet mode's own modulator.
/// </summary>
/// <remarks>
/// The tool's whole claim is "hand it a recording and it will tell you what mode it is", so the
/// test that matters is the round trip: modulate a known frame in one mode, render it at the card
/// rate a real capture arrives at, and check the sweep finds those exact bytes and names that
/// mode among the modes that read them. Anything less tests the plumbing and not the claim.
/// </remarks>
public class SweepTests
{
    private const int CardRate = 48000;

    /// <summary>Every mode the default sweep runs, one theory case each.</summary>
    public static TheoryData<string> PacketModes()
    {
        var data = new TheoryData<string>();
        foreach (string mode in ModemCatalog.KnownModes.Where(Sweep.IsPacketMode))
        {
            data.Add(mode);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PacketModes))]
    public void Every_Packet_Mode_Is_Recovered_From_A_Capture_Of_Its_Own_Modulation(string mode)
    {
        byte[] sent = UiFrame($"PDN {mode} sweep round trip");
        float[] audio = RenderAtCardRate(mode, sent);

        SweepResult result = Sweep.Run(audio, CardRate, Sweep.PacketModes());

        Decode[] copies = [.. result.Decodes.Where(d => d.Frame.SequenceEqual(sent))];
        copies.Should().NotBeEmpty($"the sweep should recover a frame sent in {mode}");
        copies.Select(d => d.Label).Should().Contain(
            label => label.StartsWith(mode, StringComparison.Ordinal),
            $"{mode} should be named among the modes that read its own modulation");
    }

    [Fact]
    public void A_Capture_At_An_Awkward_Rate_Still_Decodes()
    {
        // 44100 is not a multiple of either DSP rate, which is exactly why the library refuses it
        // on a live station and why the tool carries its own resampler. A soundcard left at the
        // CD rate is the single most likely thing to be handed this tool, so the polyphase path
        // is pinned against a real modem rather than against a spectrum.
        byte[] sent = UiFrame("PDN awkward rate");
        float[] atCardRate = RenderAtCardRate("afsk1200", sent);
        float[] atCdRate = Resampler.Resample(atCardRate, CardRate, 44100);

        SweepResult result = Sweep.Run(atCdRate, 44100, Sweep.PacketModes());

        result.Decodes.Select(d => d.Frame).Should().Contain(
            frame => frame.SequenceEqual(sent),
            "44100 Hz audio should resample cleanly enough for the modem to read it");
    }

    [Fact]
    public void Silence_Decodes_As_Nothing()
    {
        SweepResult result = Sweep.Run(new float[CardRate * 2], CardRate, Sweep.PacketModes());

        result.Decodes.Should().BeEmpty();
        result.Failures.Should().BeEmpty("every swept mode should at least be constructible");
        result.Silent.Should().HaveCount(Sweep.PacketModes().Count);
    }

    [Fact]
    public void A_Frame_Read_By_Several_Modes_Names_The_Most_Confident_One()
    {
        // A verified CRC beats Reed-Solomon standing alone, and both beat a frame the receiver
        // read and would not have handed to its host. That ordering is what stops the report
        // attributing a burst to whichever mode happened to run first in the sweep.
        Decode crc = Decode("fsk9600-il2p", new FrameQuality("m", 1, 0, CrcValid: true));
        Decode plain = Decode("c4fsk9600", new FrameQuality("m", 1, 0, null, PlainIl2p: true));
        Decode held = Decode(
            "c4fsk19200", new FrameQuality("m", 1, 0, null, PlainIl2p: true, MonitorOnly: true));

        new[] { held, plain, crc }.MinBy(Sweep.Confidence)!.Label.Should().Be("fsk9600-il2p");
        new[] { held, plain }.MinBy(Sweep.Confidence)!.Label.Should().Be("c4fsk9600");
    }

    [Fact]
    public void The_Packet_Sweep_Leaves_Out_Only_The_Hf_Data_Waveforms()
    {
        // Stated as an exclusion so a mode added to the catalogue joins this sweep automatically
        // - the safe direction for a tool whose failure mode is a silent miss. This asserts the
        // exclusion has not quietly grown into a hand-written allow-list.
        string[] swept = [.. Sweep.PacketModes().Select(e => e.Mode).Distinct()];
        string[] missing = [.. ModemCatalog.KnownModes.Except(swept)];

        missing.Should().OnlyContain(
            mode => Sweep.HfWaveformPrefixes.Any(p => mode.StartsWith(p, StringComparison.Ordinal)),
            "the only modes --packet skips should be the HF data waveforms");
        missing.Should().NotBeEmpty("freedv-* and ms110d-* are in the catalogue and are skipped");
        Sweep.AllModes().Select(e => e.Mode).Distinct().Should().BeEquivalentTo(
            ModemCatalog.KnownModes, "the default sweep is every mode the modem has");

        Sweep.PacketModes().Select(e => e.Label).Should().OnlyHaveUniqueItems(
            "two entries sharing a label would be indistinguishable in the report");
    }

    [Fact]
    public void The_Shaped_Psk_Modes_An_Fm_Radio_Carries_Survive_Every_Narrowing_But_Fm_Native()
    {
        // The regression that matters, and it is not hypothetical. The first version of this tool
        // defaulted to FmModeProfiles.IsFmMode, which answers "which modes reach the air as
        // frequency modulation" - a question about modulators. The question asked here is what can
        // arrive through an FM receiver, and Nino's switch map groups the shaped-PSK modes
        // "Shaped PSK - SSB radios, or FM radios" (docs/mode-modulation-reference.md). The first
        // real off-air corpus was bpsk1200 through an FM radio and the FM-native sweep read none
        // of it.
        string[] swept = [.. Sweep.PacketModes().Select(e => e.Mode)];

        swept.Should().Contain(["bpsk300", "bpsk1200", "qpsk600", "qpsk2400"]);
        Sweep.FmNativeModes().Select(e => e.Mode).Should().NotContain("bpsk1200",
            "which is exactly why --fm is not the default");
    }

    private static Decode Decode(string label, FrameQuality quality) =>
        new([1, 2, 3], quality, label, 0);

    /// <summary>Modulates <paramref name="frame"/> in <paramref name="mode"/> and renders it at
    /// 48 kHz through the same upsampler the daemon transmits with, which is what a capture of
    /// that transmission looks like.</summary>
    private static float[] RenderAtCardRate(string mode, byte[] frame)
    {
        int dspRate = ModemCatalog.DspRateFor(mode);
        IModem modem = ModemCatalog.Create(mode, dspRate, _ => { });
        float[] burst = modem.Modulate(frame, txDelayMilliseconds: 50);

        // Lead-in and tail: a demodulator needs somewhere to settle, and the tool's own flush
        // padding should not be the only thing standing between a frame and the end of the file.
        var padded = new float[burst.Length + dspRate];
        burst.CopyTo(padded, dspRate / 2);

        if (dspRate == CardRate)
        {
            return padded;
        }

        var upsampler = new Upsampler(CardRate, CardRate / dspRate);
        var rendered = new float[upsampler.OutputLength(padded.Length)];
        upsampler.Process(padded, rendered);
        return rendered;
    }

    /// <summary>
    /// A UI command frame from Q0AAA to TEST carrying <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// The command bit is set on the destination and clear on the source, which is what an AX.25
    /// 2.x command frame looks like and what every real transmitter sends. It matters here: IL2P's
    /// Type 1 header carries one command/response bit for the pair, so a frame with both bits
    /// clear - legal in AX.25 1.x, and what a hand-built test frame falls into by accident - comes
    /// back from the plain-IL2P (CRC-less) reading normalised to a response, and a byte-exact
    /// round-trip assertion then fails on a frame nobody would ever transmit. Building the frame
    /// correctly is the fix; the modem is not wrong.
    /// </remarks>
    private static byte[] UiFrame(string text) =>
    [
        .. Address("TEST", last: false, commandBit: 1),
        .. Address("Q0AAA", last: true, commandBit: 0),
        0x03, 0xF0,
        .. System.Text.Encoding.ASCII.GetBytes(text),
    ];

    private static byte[] Address(string call, bool last, int commandBit)
    {
        var address = new byte[7];
        for (int i = 0; i < 6; i++)
        {
            address[i] = (byte)((i < call.Length ? char.ToUpperInvariant(call[i]) : ' ') << 1);
        }

        address[6] = (byte)((commandBit << 7) | 0x60 | (last ? 1 : 0));
        return address;
    }
}
