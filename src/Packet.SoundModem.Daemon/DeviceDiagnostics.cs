namespace Packet.SoundModem.Daemon;

/// <summary>
/// Operator-facing explanations for a device the configuration names but the machine does not
/// have (or will not let the service user open).
/// </summary>
/// <remarks>
/// This is the message a fresh install produces: the seeded config names a sound card and a
/// CM108 PTT that almost certainly do not match the box, so the very first thing an admin sees
/// in <c>journalctl</c> is one of these. It has to name the setting, name the file to edit, and
/// give the command that lists what is actually present.
///
/// Unlike a malformed config these do NOT exit 2, because they are not always the operator's
/// mistake — a USB interface may simply not have enumerated yet at boot. The service keeps
/// restarting so it comes up on its own once the hardware appears.
/// </remarks>
internal static class DeviceDiagnostics
{
    private const string Retry =
        "  The service will keep retrying: this clears by itself if the device is merely late\n"
        + "  appearing (a USB interface can still be enumerating at boot).";

    /// <summary>
    /// Where the setting lives — the config file if there is one, else the CLI flag. On its own
    /// line: a full /etc path plus prose wraps mid-sentence in the journal and reads badly.
    /// </summary>
    private static string Source(string? configPath, string key, string flag) =>
        configPath is null
            ? $"  Set by `{flag}` on the command line."
            : $"  Set by \"{key}\" in {configPath}";

    internal static string Audio(string device, string? configPath, Exception error) =>
        $"""
        cannot open the sound device "{device}"
          {error.Message}

        {Source(configPath, "device", "--device")}
          This is the ALSA device used for both capture and playback. List what this
          machine actually has:
            aplay -l ; arecord -l ; aplay -L
          Prefer a stable name such as plughw:CARD=Device,DEV=0 over plughw:1,0 — card
          numbers move when devices are re-plugged.
        {Retry}
        """;

    internal static string Ptt(PttConfig ptt, string? configPath, Exception error)
    {
        string where = Source(configPath, "ptt", "--ptt");
        string specific = ptt.Type switch
        {
            "cm108" =>
                """
                  List what this machine actually has:
                    ls -l /dev/hidraw*
                  /dev/hidraw* is root-only by default, so the unprivileged service user cannot
                  open it without a udev rule granting the audio group access — see the
                  Permissions section of INSTALL.md. "Permission denied" here almost always
                  means the rule is missing rather than the wrong device.
                """,
            "serial" =>
                """
                  List what this machine actually has:
                    ls -l /dev/serial/by-id/ /dev/ttyUSB* /dev/ttyACM*
                  /dev/serial/by-id/ names are stable across re-plugging; /dev/ttyUSB0 is not.
                  The service user must be in the dialout group (the packaged unit already is).
                """,
            _ => "",
        };

        return $"""
            cannot open the {ptt.Type} PTT device "{ptt.Device}"
              {error.Message}

            {where}
              This selects how the radio is keyed; omit it entirely for VOX, or for a
              FlexRadio, which keys itself.
            {specific}
            {Retry}
            """;
    }
}
