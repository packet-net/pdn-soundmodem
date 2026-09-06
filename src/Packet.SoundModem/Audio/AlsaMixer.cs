using System.Runtime.InteropServices;

namespace Packet.SoundModem.Audio;

/// <summary>
/// The one class in this repository that talks to <c>libasound</c>'s mixer - the same
/// <c>libasound.so.2</c> the PCM in <see cref="AlsaPcm"/> already uses, P/Invoked directly.
/// </summary>
/// <remarks>
/// <para><b>No shell-out.</b> Not <c>amixer</c>: a station that has to fork a process to set its
/// own capture gain has a dependency on a package it does not declare, an exit code to interpret
/// and a locale to be surprised by. The simple mixer API is a dozen entry points and this is all
/// of them we need.</para>
/// <para><b>dB is the unit</b> (Tom, 2026-09-06: "change to dB, far more useful"). A level in dB
/// means the same thing on every card and on the radio at the other end of the lead, while a
/// percentage of a card's own raw range means nothing until you know the range. So the station's
/// levels are set with <c>snd_mixer_selem_set_capture_dB_all</c> and its playback twin, and read
/// back in dB with the card's own range quoted beside them.</para>
/// <para><b>Not every card has a dB scale.</b> Those calls only work where the card publishes
/// one, and plenty publish only raw steps. That is not guessed at: <see cref="ReadDbRange"/>
/// answers null, the layer above says "no dB scale" in the journal and in the API, and the
/// control is left exactly as the card has it. A percentage is still read back for such a card,
/// because it is the only figure there is and it is what <c>alsamixer</c> shows.</para>
/// <para><b>Nothing here throws for a missing control.</b> Card revisions differ - "Mic" against
/// "Mic Capture", an "Auto Gain Control" that some revisions do not have at all - and the layer
/// above (<see cref="MixerSetup"/>) turns absence into a journal line, not a failure.</para>
/// </remarks>
public sealed class AlsaMixer : IAlsaMixer
{
    private const string Lib = "libasound.so.2";

    /// <summary>
    /// <c>SND_MIXER_SCHN_MONO</c>, which alsa-lib defines as <c>SND_MIXER_SCHN_FRONT_LEFT</c>.
    /// Every control here is read from its first channel; the setters are the <c>_all</c> forms,
    /// so the channels cannot drift apart under us.
    /// </summary>
    private const int FirstChannel = 0;

    /// <summary>
    /// alsa-lib's mute sentinel, <c>SND_CTL_TLV_DB_GAIN_MUTE</c> from <c>alsa/control.h</c>, in
    /// hundredths of a dB. It is what <c>snd_tlv_get_dB_range</c> reports as the minimum of a
    /// control whose TLV carries the mute flag, and it is not a level.
    /// </summary>
    private const long MuteSentinel = -9999999;

    /// <summary>
    /// Anything at or below this is the sentinel rather than a reading. Real controls bottom out
    /// in the tens of dB; -10000 dB is not a level any sound card has.
    /// </summary>
    private const long NotALevel = -1000000;

    /// <summary>
    /// How far up the raw range to look for the first step that is a level rather than mute.
    /// One step is the answer on every card seen; the bound is only so that a card answering
    /// nonsense cannot turn this into a long loop at start-up.
    /// </summary>
    private const int MuteSearchSteps = 64;

    private readonly object _gate = new();

    /// <summary>
    /// The dB range per control and side, worked out once.
    /// </summary>
    /// <remarks>
    /// A range is a property of the card, not of where the level currently sits, and alsa-lib
    /// itself caches the parsed TLV behind these calls for the life of the element and never
    /// invalidates it (<c>db_initialized</c> in <c>simple_none.c</c> is set once). So reading it
    /// again per access buys nothing and costs a TLV round trip on every start-up line, every
    /// GET of the API and every move of the operator page's slider.
    /// </remarks>
    private readonly Dictionary<(string Control, MixerDirection Direction), MixerDbRange?> _ranges
        = [];

    private IntPtr _mixer;

    private AlsaMixer(IntPtr mixer, string card, List<string> controls)
    {
        _mixer = mixer;
        Card = card;
        Controls = controls;
    }

    /// <inheritdoc />
    public string Card { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Controls { get; }

    /// <summary>
    /// The mixer card that goes with a PCM device string: <c>plughw:CARD=Device,DEV=0</c> is
    /// mixed by <c>hw:CARD=Device</c>, <c>plughw:1,0</c> by <c>hw:1</c>.
    /// </summary>
    /// <remarks>
    /// A PCM name says which sub-device of which card to stream from; a mixer attaches to the
    /// card. So the rule is: keep what follows the first colon up to the first comma, and put it
    /// behind <c>hw:</c>. A name with no colon at all ("default") is handed over unchanged, which
    /// is what <c>snd_mixer_attach</c> wants for it.
    /// </remarks>
    /// <param name="device">The ALSA PCM device name from the configuration.</param>
    public static string CardFor(string device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return "default";
        }

        int colon = device.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return device;
        }

        string card = device[(colon + 1)..].Split(',')[0].Trim();
        return card.Length == 0 ? "default" : $"hw:{card}";
    }

    /// <summary>
    /// Opens a card's mixer, or explains why there is not one to open.
    /// </summary>
    /// <param name="card">The card, as <see cref="CardFor"/> derives it.</param>
    /// <param name="mixer">The open mixer, or null.</param>
    /// <param name="why">Why not, in an operator's terms, when this returns false.</param>
    /// <returns>True when a mixer was opened. False is a normal outcome - a card with no mixer
    /// at all, or a libasound with no mixer functions in it - and never a reason to stop.</returns>
    public static bool TryOpen(string card, out AlsaMixer? mixer, out string why)
    {
        mixer = null;
        why = "";
        IntPtr handle = IntPtr.Zero;
        try
        {
            int err = snd_mixer_open(out handle, 0);
            if (err < 0)
            {
                why = $"snd_mixer_open: {StrError(err)}";
                return false;
            }

            err = snd_mixer_attach(handle, card);
            if (err < 0)
            {
                why = $"snd_mixer_attach({card}): {StrError(err)}";
                snd_mixer_close(handle);
                return false;
            }

            err = snd_mixer_selem_register(handle, IntPtr.Zero, IntPtr.Zero);
            if (err < 0)
            {
                why = $"snd_mixer_selem_register: {StrError(err)}";
                snd_mixer_close(handle);
                return false;
            }

            err = snd_mixer_load(handle);
            if (err < 0)
            {
                why = $"snd_mixer_load: {StrError(err)}";
                snd_mixer_close(handle);
                return false;
            }

            var names = new List<string>();
            for (IntPtr element = snd_mixer_first_elem(handle);
                 element != IntPtr.Zero;
                 element = snd_mixer_elem_next(element))
            {
                string? name = Marshal.PtrToStringAnsi(snd_mixer_selem_get_name(element));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // Index 0 only. A card can present the same name twice ("Mic",0 and "Mic",1);
                // the first is the one alsamixer puts under the cursor and the one an operator
                // means, and a station has no way to say which of two it wanted. Names only:
                // the element pointers are deliberately not kept, see TryElement.
                if (snd_mixer_selem_get_index(element) != 0 || names.Contains(name))
                {
                    continue;
                }

                names.Add(name);
            }

            if (names.Count == 0)
            {
                why = $"{card} has a mixer with no controls on it";
                snd_mixer_close(handle);
                return false;
            }

            mixer = new AlsaMixer(handle, card, names);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                    or BadImageFormatException)
        {
            // The PCM opened through the same library, so this is close to unreachable - but a
            // libasound too old or too stripped to carry snd_mixer_* must cost the mixer and not
            // the station.
            why = $"this libasound has no mixer functions ({e.Message})";
            if (handle != IntPtr.Zero)
            {
                try
                {
                    snd_mixer_close(handle);
                }
                catch (EntryPointNotFoundException)
                {
                    // Nothing to close it with either; the handle leaks with the process.
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public void Refresh()
    {
        lock (_gate)
        {
            if (_mixer != IntPtr.Zero)
            {
                _ = snd_mixer_handle_events(_mixer);
            }
        }
    }

    /// <inheritdoc />
    public bool TrySetDb(string control, MixerDirection direction, double decibels)
    {
        lock (_gate)
        {
            if (!TryElement(control, out IntPtr element) || !HasVolume(element, direction))
            {
                return false;
            }

            // Refused rather than approximated. A card with no dB scale cannot be told a dB, and
            // converting one into a percentage of its raw range would be inventing a mapping the
            // card never published - the operator would be setting a number that means nothing.
            if (Range(control, element, direction) is null)
            {
                return false;
            }

            // Hundredths of a dB, which is alsa-lib's unit throughout, and dir 0: put the level
            // at the nearest step the card has rather than the one below (-1) or above (+1).
            // 6.0 dB on a card with 1 dB steps is 6.0 dB; 6.4 dB is 6.0 dB, not 6.0 or 7.0
            // depending on which way alsa-lib happened to lean.
            var value = new CLong((nint)(long)Math.Round(decibels * 100.0));
            int err = direction == MixerDirection.Capture
                ? snd_mixer_selem_set_capture_dB_all(element, value, 0)
                : snd_mixer_selem_set_playback_dB_all(element, value, 0);
            return err >= 0;
        }
    }

    /// <inheritdoc />
    public MixerDbRange? ReadDbRange(string control, MixerDirection direction)
    {
        lock (_gate)
        {
            if (!TryElement(control, out IntPtr element) || !HasVolume(element, direction))
            {
                return null;
            }

            return Range(control, element, direction);
        }
    }

    /// <inheritdoc />
    public bool TrySetVolume(string control, MixerDirection direction, int percent)
    {
        lock (_gate)
        {
            if (!TryElement(control, out IntPtr element) || !HasVolume(element, direction))
            {
                return false;
            }

            if (Raw(element, direction) is not (long min, long max))
            {
                return false;
            }

            long raw = min + (long)Math.Round((max - min) * Math.Clamp(percent, 0, 100) / 100.0);
            var value = new CLong((nint)raw);
            int err = direction == MixerDirection.Capture
                ? snd_mixer_selem_set_capture_volume_all(element, value)
                : snd_mixer_selem_set_playback_volume_all(element, value);
            return err >= 0;
        }
    }

    /// <inheritdoc />
    public bool TryReadVolume(string control, MixerDirection direction, out int percent, out double? decibels)
    {
        percent = 0;
        decibels = null;
        lock (_gate)
        {
            if (!TryElement(control, out IntPtr element) || !HasVolume(element, direction))
            {
                return false;
            }

            if (Raw(element, direction) is not (long min, long max))
            {
                return false;
            }

            int err = direction == MixerDirection.Capture
                ? snd_mixer_selem_get_capture_volume(element, FirstChannel, out CLong current)
                : snd_mixer_selem_get_playback_volume(element, FirstChannel, out current);
            if (err < 0)
            {
                return false;
            }

            long raw = current.Value;
            percent = max > min
                ? (int)Math.Round((raw - min) * 100.0 / (max - min))
                : 100;

            // Hundredths of a dB, and only on a card that publishes a scale. Absent is normal.
            int dbErr = direction == MixerDirection.Capture
                ? snd_mixer_selem_get_capture_dB(element, FirstChannel, out CLong hundredths)
                : snd_mixer_selem_get_playback_dB(element, FirstChannel, out hundredths);
            if (dbErr >= 0)
            {
                decibels = hundredths.Value / 100.0;
            }

            return true;
        }
    }

    /// <inheritdoc />
    public bool TrySetSwitch(string control, bool on)
    {
        lock (_gate)
        {
            if (!TryElement(control, out IntPtr element))
            {
                return false;
            }

            int value = on ? 1 : 0;
            if (snd_mixer_selem_has_capture_switch(element) != 0
                && snd_mixer_selem_set_capture_switch_all(element, value) >= 0)
            {
                return true;
            }

            return snd_mixer_selem_has_playback_switch(element) != 0
                && snd_mixer_selem_set_playback_switch_all(element, value) >= 0;
        }
    }

    /// <inheritdoc />
    public bool TryReadSwitch(string control, out bool on)
    {
        on = false;
        lock (_gate)
        {
            if (!TryElement(control, out IntPtr element))
            {
                return false;
            }

            if (snd_mixer_selem_has_capture_switch(element) != 0
                && snd_mixer_selem_get_capture_switch(element, FirstChannel, out int captured) >= 0)
            {
                on = captured != 0;
                return true;
            }

            if (snd_mixer_selem_has_playback_switch(element) != 0
                && snd_mixer_selem_get_playback_switch(element, FirstChannel, out int played) >= 0)
            {
                on = played != 0;
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_mixer != IntPtr.Zero)
            {
                snd_mixer_close(_mixer);
                _mixer = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// The element for a control name, asked of alsa-lib on every access rather than cached.
    /// </summary>
    /// <remarks>
    /// Deliberately not a map built once at load. <c>snd_mixer_handle_events</c> is how a card
    /// tells alsa-lib that its elements have changed, and it frees the ones that have gone - a USB
    /// sound card unplugged while the daemon runs is exactly that - so a pointer kept from load
    /// time is a use-after-free waiting for a re-plug. Looking it up costs one malloc and one free
    /// per access, on a path that runs a handful of times at start-up and once per operator
    /// change.
    /// </remarks>
    private bool TryElement(string control, out IntPtr element)
    {
        element = IntPtr.Zero;
        if (_mixer == IntPtr.Zero || string.IsNullOrEmpty(control))
        {
            return false;
        }

        if (snd_mixer_selem_id_malloc(out IntPtr id) < 0 || id == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            snd_mixer_selem_id_set_index(id, 0);
            snd_mixer_selem_id_set_name(id, control);
            element = snd_mixer_find_selem(_mixer, id);
            return element != IntPtr.Zero;
        }
        finally
        {
            snd_mixer_selem_id_free(id);
        }
    }

    private static bool HasVolume(IntPtr element, MixerDirection direction) =>
        direction == MixerDirection.Capture
            ? snd_mixer_selem_has_capture_volume(element) != 0
            : snd_mixer_selem_has_playback_volume(element) != 0;

    /// <summary>
    /// A control's dB span, worked out once and remembered. Null when the card publishes only
    /// raw steps.
    /// </summary>
    private MixerDbRange? Range(string control, IntPtr element, MixerDirection direction)
    {
        if (_ranges.TryGetValue((control, direction), out MixerDbRange? known))
        {
            return known;
        }

        MixerDbRange? found = DbRange(element, direction);
        _ranges[(control, direction)] = found;
        return found;
    }

    /// <summary>
    /// A control's dB span as the card publishes it, with the mute sentinel resolved into the
    /// lowest step that is actually a level.
    /// </summary>
    /// <remarks>
    /// <para>Two ways to have no scale, and both are ordinary. alsa-lib answers a negative error
    /// for a control with no dB information at all; a few drivers answer 0 with the range
    /// collapsed to a point, which is the same thing said differently and would otherwise look
    /// like a card whose only setting is 0.00 dB.</para>
    /// <para>A third way is not a missing scale at all and must not be treated as one: a control
    /// whose TLV carries the mute flag reports its minimum as
    /// <c>SND_CTL_TLV_DB_GAIN_MUTE</c> (-9999999, i.e. -99999.99 dB), because its bottom step is
    /// silence rather than a level. The bench CM108's "Speaker" is one of these
    /// (<c>dBminmaxmute-min=-37.00dB,max=0.00dB</c>). Reported as-is it makes a journal line that
    /// reads "-99999.99 to 0.00 dB" and a slider a thousand times too long to aim a transmit
    /// level with, so the sentinel is recognised and the card is asked what each raw step above
    /// the bottom actually is until one of them is a level.</para>
    /// </remarks>
    private static MixerDbRange? DbRange(IntPtr element, MixerDirection direction)
    {
        int err = direction == MixerDirection.Capture
            ? snd_mixer_selem_get_capture_dB_range(element, out CLong low, out CLong high)
            : snd_mixer_selem_get_playback_dB_range(element, out low, out high);
        if (err < 0)
        {
            return null;
        }

        long min = low.Value;
        long max = high.Value;
        if (max <= NotALevel)  // MuteSentinel itself, or worse
        {
            // A range whose top is the sentinel is not a scale at all.
            return null;
        }

        if (min > NotALevel)
        {
            return max <= min ? null : new MixerDbRange(min / 100.0, max / 100.0, false);
        }

        // The bottom is mute. Walk up the raw steps for the first one the card calls a level.
        if (Raw(element, direction) is not (long rawMin, long rawMax))
        {
            return null;
        }

        int steps = (int)Math.Min(MuteSearchSteps, Math.Max(0, rawMax - rawMin));
        for (int step = 1; step <= steps; step++)
        {
            if (AskDb(element, direction, rawMin + step) is not long decibels
                || decibels <= NotALevel)
            {
                continue;
            }

            return decibels >= max
                ? null
                : new MixerDbRange(decibels / 100.0, max / 100.0, true);
        }

        return null;
    }

    /// <summary>What the card says a raw step is worth in hundredths of a dB, or null.</summary>
    private static long? AskDb(IntPtr element, MixerDirection direction, long raw)
    {
        var value = new CLong((nint)raw);
        int err = direction == MixerDirection.Capture
            ? snd_mixer_selem_ask_capture_vol_dB(element, value, out CLong decibels)
            : snd_mixer_selem_ask_playback_vol_dB(element, value, out decibels);
        return err < 0 ? null : decibels.Value;
    }

    /// <summary>A control's raw step range, which is what the dB scale is indexed by.</summary>
    private static (long Min, long Max)? Raw(IntPtr element, MixerDirection direction)
    {
        int err = direction == MixerDirection.Capture
            ? snd_mixer_selem_get_capture_volume_range(element, out CLong low, out CLong high)
            : snd_mixer_selem_get_playback_volume_range(element, out low, out high);
        long min = low.Value;
        long max = high.Value;
        return err < 0 || max < min ? null : (min, max);
    }

    private static string StrError(int err) =>
        Marshal.PtrToStringAnsi(snd_strerror(err)) ?? $"error {err}";

    [DllImport(Lib)]
    private static extern int snd_mixer_open(out IntPtr mixer, int mode);

    [DllImport(Lib)]
    private static extern int snd_mixer_attach(IntPtr mixer, string name);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_register(IntPtr mixer, IntPtr options, IntPtr classp);

    [DllImport(Lib)]
    private static extern int snd_mixer_load(IntPtr mixer);

    [DllImport(Lib)]
    private static extern int snd_mixer_handle_events(IntPtr mixer);

    [DllImport(Lib)]
    private static extern void snd_mixer_close(IntPtr mixer);

    [DllImport(Lib)]
    private static extern IntPtr snd_mixer_first_elem(IntPtr mixer);

    [DllImport(Lib)]
    private static extern IntPtr snd_mixer_elem_next(IntPtr element);

    [DllImport(Lib)]
    private static extern IntPtr snd_mixer_selem_get_name(IntPtr element);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_id_malloc(out IntPtr id);

    [DllImport(Lib)]
    private static extern void snd_mixer_selem_id_free(IntPtr id);

    [DllImport(Lib)]
    private static extern void snd_mixer_selem_id_set_index(IntPtr id, uint index);

    [DllImport(Lib)]
    private static extern void snd_mixer_selem_id_set_name(IntPtr id, string name);

    [DllImport(Lib)]
    private static extern IntPtr snd_mixer_find_selem(IntPtr mixer, IntPtr id);

    [DllImport(Lib)]
    private static extern uint snd_mixer_selem_get_index(IntPtr element);

    // Every level below is a C `long`, and a C `long` is NOT a C# long: it is 64-bit on LP64 and
    // 32-bit on 32-bit ARM, which packaging/build-deb.sh maps armhf to and release.yml ships a
    // .deb of. Declared as C# `long` there, an out-param would take a four-byte write into an
    // eight-byte slot and, worse, a by-value argument would be passed as an aligned register pair
    // while the callee read one register - so a configured capture gain would write an arbitrary
    // raw level to the card. CLong is exactly this type on every platform.
    [DllImport(Lib)]
    private static extern int snd_mixer_selem_has_capture_volume(IntPtr element);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_has_playback_volume(IntPtr element);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_has_capture_switch(IntPtr element);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_has_playback_switch(IntPtr element);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_capture_volume_range(
        IntPtr element, out CLong min, out CLong max);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_playback_volume_range(
        IntPtr element, out CLong min, out CLong max);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_set_capture_volume_all(IntPtr element, CLong value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_set_playback_volume_all(IntPtr element, CLong value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_capture_volume(
        IntPtr element, int channel, out CLong value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_playback_volume(
        IntPtr element, int channel, out CLong value);

    // int snd_mixer_selem_ask_*_vol_dB(snd_mixer_elem_t *elem, long value, long *dBvalue) -
    // what one raw step is worth in hundredths of a dB. Used to find the bottom of a range whose
    // lowest step is the card's mute rather than a level.
    [DllImport(Lib)]
    private static extern int snd_mixer_selem_ask_capture_vol_dB(
        IntPtr element, CLong value, out CLong decibels);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_ask_playback_vol_dB(
        IntPtr element, CLong value, out CLong decibels);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_capture_dB_range(
        IntPtr element, out CLong min, out CLong max);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_playback_dB_range(
        IntPtr element, out CLong min, out CLong max);

    // The trailing int is alsa-lib's rounding direction: -1 rounds down, +1 rounds up, 0 takes
    // the nearest step. It is an int and not a C long, unlike the value beside it.
    [DllImport(Lib)]
    private static extern int snd_mixer_selem_set_capture_dB_all(
        IntPtr element, CLong value, int dir);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_set_playback_dB_all(
        IntPtr element, CLong value, int dir);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_capture_dB(
        IntPtr element, int channel, out CLong value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_playback_dB(
        IntPtr element, int channel, out CLong value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_set_capture_switch_all(IntPtr element, int value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_set_playback_switch_all(IntPtr element, int value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_capture_switch(
        IntPtr element, int channel, out int value);

    [DllImport(Lib)]
    private static extern int snd_mixer_selem_get_playback_switch(
        IntPtr element, int channel, out int value);

    [DllImport(Lib)]
    private static extern IntPtr snd_strerror(int error);
}
