using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Ota;

/// <summary>
/// The mode-generic half of the simulation harness: everything the sim ladder needs to know about a
/// <see cref="ModemCatalog"/> mode - its id string, its DSP audio rate, how to render a burst
/// carrying a known frame, and how to score a captured burst back to that frame - behind the mode
/// name alone.
/// </summary>
/// <remarks>
/// <para>This is the generalization of the MS110D-coupled ladder: where <see cref="LadderPass"/>
/// reaches straight into <c>Ms110dModulator</c>/<c>Ms110dReferenceBits</c>, this drives the mode
/// entirely through the <see cref="IModem"/> seam that <see cref="ModemCatalog.Create"/> hands back,
/// so the same generate → channel → score loop covers every catalogue mode - the FreeDV datac OFDM
/// family above all, but AFSK/PSK/FSK/MS110D too. The MS110D <c>ladder</c>/<c>score</c> commands are
/// untouched and keep their richer LLR/uncoded instrumentation; this path trades that for
/// mode-independence, which is exactly what a cross-mode baseline needs.</para>
/// <para><b>Frame-level figure of merit.</b> Every catalogue mode delivers whole AX.25 frames behind
/// a CRC (IL2P+CRC for the datac family), so a burst either recovers its frame bit-exact or it does
/// not - coded BER is degenerate (0 or "frame lost"). The honest metric at this layer is therefore
/// the <em>frame error rate</em>, which is what FreeDV itself publishes (packets-decoded / sent).
/// Pre-FEC ("uncoded") BER is not observable through <see cref="IModem"/>; the raw datac
/// <see cref="DatacPacketProbe"/> reaches one layer deeper for the packet-level coded BER the
/// published FreeDV cross-check is quoted in.</para>
/// </remarks>
internal sealed class SimModem
{
    /// <summary>Modem options (detector override etc.) applied to both directions.</summary>
    public ModemOptions Options { get; init; }

    /// <summary>Builds the adapter for a catalogue mode.</summary>
    /// <param name="mode">A <see cref="ModemCatalog"/> mode string (e.g. <c>freedv-datac0</c>).</param>
    /// <param name="rate">DSP sample rate; defaults to <see cref="ModemCatalog.DspRateFor"/>. The
    /// datac family accepts its engine-native 8000&#160;Hz here (the cleanest, resampler-free path,
    /// and the rate FreeDV's own published figures are measured at) as well as the 48000&#160;Hz
    /// deployment path.</param>
    public SimModem(string mode, int? rate = null)
    {
        if (!ModemCatalog.IsKnown(mode))
        {
            throw new ArgumentException($"unknown mode '{mode}'", nameof(mode));
        }

        Mode = mode;
        Rate = rate ?? ModemCatalog.DspRateFor(mode);
    }

    /// <summary>The mode's id string.</summary>
    public string Mode { get; }

    /// <summary>The DSP audio rate the mode's mod/demod run at.</summary>
    public int Rate { get; }

    /// <summary>
    /// Renders one burst carrying <paramref name="frame"/>, with modulator silence trimmed so the
    /// channel calibrates its noise against the burst's own power (the trailing guard tail and any
    /// leading pad are exact zeros and are cut here).
    /// </summary>
    /// <param name="frame">The AX.25 frame to carry.</param>
    /// <param name="txDelayMilliseconds">
    /// TXDELAY - the flag/preamble run-in ahead of the frame, in milliseconds. Zero (the default,
    /// used by the datac/sim baseline) is fine for waveforms that carry their own sync (IL2P, OFDM)
    /// or acquire in a couple of symbols (AFSK 1200). The fast baseband FSK detectors (classic-HDLC
    /// <c>fsk9600</c>/<c>c4fsk9600</c> at 9600 baud) need a real run-in to lock clock and DCD before
    /// the frame - 2 opening flags is not enough - so the over-the-air FM ladder renders with a
    /// realistic TXDELAY. The run-in is signal, not silence, so it is not trimmed and does not
    /// dilute the SNR calibration.</param>
    public float[] RenderBurst(ReadOnlySpan<byte> frame, int txDelayMilliseconds = 0)
    {
        IModem tx = ModemCatalog.Create(Mode, Rate, static _ => { }, Options);
        float[] audio = tx.Modulate(frame, txDelayMilliseconds);
        return TrimSilence(audio);
    }

    /// <summary>
    /// Feeds a captured burst through a fresh receiver and reports whether the sent frame came back.
    /// </summary>
    /// <param name="audio">Post-channel audio, fed in daemon-sized (100&#160;ms) blocks so the
    /// streaming path is exercised, not one giant span.</param>
    /// <param name="sentFrame">The frame that went out, for an exact bit comparison.</param>
    public SimDecode Decode(ReadOnlySpan<float> audio, byte[] sentFrame)
    {
        var received = new List<byte[]>();
        int correctedBytes = 0;
        IModem rx = ModemCatalog.Create(Mode, Rate, received.Add, Options);
        rx.FrameDecoded += (frame, quality) =>
        {
            if (BytesEqual(frame, sentFrame) && quality.CorrectedBytes is int c)
            {
                correctedBytes = c;
            }
        };

        int block = Math.Max(1, Rate / 10);
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            rx.Process(audio.Slice(pos, Math.Min(block, audio.Length - pos)));
        }

        bool matched = false;
        for (int i = 0; i < received.Count && !matched; i++)
        {
            matched = BytesEqual(received[i], sentFrame);
        }

        return new SimDecode(received.Count, matched, correctedBytes);
    }

    /// <summary>A deterministic pseudo-random frame of <paramref name="bytes"/> bytes for
    /// <paramref name="seed"/> - an AX.25-looking UI header then a seeded random body, so a point
    /// reproduces from its seed and the body is incompressible (a fair load for the FEC).</summary>
    public static byte[] Frame(int bytes, int seed)
    {
        byte[] frame = new byte[bytes];
        ReadOnlySpan<byte> header =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0,
        ];
        header[..Math.Min(header.Length, bytes)].CopyTo(frame);
        if (bytes > header.Length)
        {
            new Random(seed).NextBytes(frame.AsSpan(header.Length));
        }

        return frame;
    }

    private static float[] TrimSilence(float[] audio)
    {
        int start = 0;
        while (start < audio.Length && audio[start] == 0f)
        {
            start++;
        }

        int end = audio.Length;
        while (end > start && audio[end - 1] == 0f)
        {
            end--;
        }

        return start == 0 && end == audio.Length ? audio : audio[start..end];
    }

    private static bool BytesEqual(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}

/// <summary>What one burst decoded to.</summary>
/// <param name="FramesDecoded">Frames the receiver emitted (0, 1, or - spuriously - more).</param>
/// <param name="Matched">Whether the sent frame came back bit-exact.</param>
/// <param name="CorrectedBytes">FEC-repaired bytes on the matching frame (a channel-stress floor);
/// 0 when nothing matched or the framing carries no correction count.</param>
internal readonly record struct SimDecode(int FramesDecoded, bool Matched, int CorrectedBytes);
