using Packet.Mailcast;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The fixed facts of the mailcast transmission (pdn-mailcast's docs/design.md and
/// docs/factsheet.md): what it is addressed to, where it sits above the dial and when it is sent
/// unless the directory says otherwise. Who sends it is configuration,
/// <see cref="MailcastConfig.Sources"/>.
/// </summary>
internal static class MailcastOnAir
{
    /// <summary>The AX.25 destination every frame of it carries.</summary>
    public const string Destination = "MCAST";

    /// <summary>The waveform: MS110D WN4. Receiving is autobaud, so any waveform decodes.</summary>
    public const string Mode = "ms110d-wn4";

    /// <summary>
    /// The signal's own centre frequency: a fixed fact about the transmitter, unmoved by
    /// whatever a receiving station's dial is read as. For a sound-card rig, the best dial puts
    /// the signal's audio centre in the middle of the rig's own receive passband, anywhere from
    /// <see cref="MailcastPlacement.SoundCardCentreLowHz"/> to
    /// <see cref="MailcastPlacement.SoundCardCentreHighHz"/> Hz: an FT-450D at about
    /// 367 to 2190 Hz, centred on 1278 Hz, wants 7.05252 MHz (<see cref="SuggestedDialHz"/>).
    /// SDRs and FlexRadios are untouched by this and stay on 7.052 MHz, 1800 Hz audio, where
    /// pdn-mailcast has recommended since the broadcast began: they have no analogue filter
    /// roll-off for the dial to dodge.
    /// </summary>
    public const double SignalCentreHz = 7_053_800;

    /// <summary>Where the signal's centre falls in a station's audio for a dial of
    /// <paramref name="dialHz"/>: the fixed <see cref="SignalCentreHz"/>, read against that
    /// dial.</summary>
    public static double CentreAudioHz(double dialHz) => SignalCentreHz - dialHz;

    /// <summary>
    /// The best dial for a sound-card rig whose receive passband is centred on
    /// <paramref name="passbandCentreHz"/> of audio: <see cref="SignalCentreHz"/> minus that
    /// centre, rounded to the nearest 10 Hz, which is as fine as an operator tunes by hand. An
    /// FT-450D at about 367 to 2190 Hz is centred on 1278 Hz and gets 7.05252 MHz.
    /// </summary>
    public static double SuggestedDialHz(double passbandCentreHz) =>
        Math.Round((SignalCentreHz - passbandCentreHz) / 10.0) * 10.0;

    /// <summary>The middle of a passband given its edges, the same way a rig's own centre is
    /// read off a measurement or a datasheet: the plain average, to the nearest Hz.</summary>
    public static double PassbandCentreHz(double lowHz, double highHz) => Math.Round((lowHz + highHz) / 2);

    /// <summary>
    /// Half the signal's occupied width when the modem cannot be measured: MS110D with the
    /// standard's pulse shaping fills about 2.9 kHz.
    /// </summary>
    public const double NominalHalfWidthHz = 1450;

    /// <summary>
    /// GB7RDG's timetable until its directory has been heard: every hour on the hour, in
    /// daylight from 2 hours after sunrise to 30 minutes before sunset at IO91lk.
    /// </summary>
    public static SlotTimetable DefaultTimetable { get; } = new(new TimeOnly(0, 0), 60, DaylightRule.Gb7rdg);

    /// <summary>How long before a slot's start a retuned rig is put on the signal.</summary>
    public static readonly TimeSpan ListenBefore = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after a slot's start a retuned rig stays on the signal: GB7RDG's hard stop is 10
    /// minutes, and the rest is room for a late start.
    /// </summary>
    public static readonly TimeSpan ListenAfter = TimeSpan.FromMinutes(12);

    /// <summary>
    /// The window in progress at <paramref name="now"/>, from <paramref name="before"/> before a
    /// slot to <paramref name="after"/> after it, or else the next one, for a timetable; null when
    /// no slot runs within a year (a daylight rule no day satisfies).
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot)? WindowAt(
        DateTimeOffset now, SlotTimetable timetable, TimeSpan before, TimeSpan after)
    {
        if (timetable.ActiveAtOrBefore(now + before) is { } started && now < started + after)
        {
            return (started - before, started + after, started);
        }

        return timetable.NextActiveAtOrAfter(now + before) is { } next
            ? (next - before, next + after, next)
            : null;
    }

    /// <summary>
    /// The longest the modem may stay locked on one burst before it is made to listen afresh,
    /// longer than any burst GB7RDG sends. MS110D can lock on a burst too weak to read and then
    /// demodulate noise for ever (pdn-soundmodem issue #553); pdn-mailcast's receiver lets go
    /// after the same 150 s (its PR #22). Counted in samples, so it does not depend on how fast
    /// the audio arrives.
    /// </summary>
    public static readonly TimeSpan LongestBurst = TimeSpan.FromSeconds(150);

    /// <summary>
    /// The information field of a mailcast frame: an AX.25 UI frame from one of
    /// <paramref name="sources"/> (base callsigns, upper case) to <see cref="Destination"/> (any
    /// SSID on either, any digipeaters) with no layer 3 protocol. False for anything else the
    /// modem hears.
    /// </summary>
    public static bool TryGetPayload(byte[] frame, IReadOnlySet<string> sources, out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        int offset = 0;
        string? destination = null;
        string? source = null;
        for (int field = 0; ; field++)
        {
            if (field > 9 || offset + 7 > frame.Length)
            {
                return false;
            }

            if (field < 2)
            {
                string? call = BaseCall(frame.AsSpan(offset, 7));
                if (call is null)
                {
                    return false;
                }

                if (field == 0)
                {
                    destination = call;
                }
                else
                {
                    source = call;
                }
            }

            bool last = (frame[offset + 6] & 1) != 0;
            offset += 7;
            if (last)
            {
                if (field == 0)
                {
                    return false;
                }

                break;
            }
        }

        // UI (0x03, P/F clear) and PID F0 (no layer 3).
        if (offset + 2 > frame.Length || frame[offset] != 0x03 || frame[offset + 1] != 0xF0
            || destination != Destination || source is null || !sources.Contains(source))
        {
            return false;
        }

        payload = frame.AsMemory(offset + 2);
        return true;
    }

    /// <summary>The callsign of a shifted address field without its SSID, or null if it is not one.</summary>
    private static string? BaseCall(ReadOnlySpan<byte> field)
    {
        Span<char> call = stackalloc char[6];
        int length = 0;
        for (int i = 0; i < 6; i++)
        {
            char c = (char)(field[i] >> 1);
            if (c == ' ')
            {
                continue;
            }

            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                return null;
            }

            call[length++] = c;
        }

        return length == 0 ? null : new string(call[..length]);
    }

    /// <summary>A frequency in Hz as MHz for a person: 7.052, 7.0538.</summary>
    public static string Mhz(double hz) =>
        (hz / 1e6).ToString("0.000###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Text made plain printable ASCII before it reaches the journal or the page.</summary>
    public static string Ascii(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var clean = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            clean.Append(c is >= ' ' and <= '~' ? c : c is '\t' ? ' ' : '?');
        }

        return clean.ToString();
    }
}
