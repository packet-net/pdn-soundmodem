using M0LTE.Radio.Audio;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Rig;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The sound card kind: an ALSA device name. The default, so it claims every device string no
/// other kind does.
/// </summary>
internal sealed class AlsaDeviceKind : DeviceKind
{
    /// <inheritdoc/>
    public override string Name => "alsa";

    /// <inheritdoc/>
    public override string Spelling => "plughw:CARD=Device,DEV=0";

    /// <inheritdoc/>
    public override bool Matches(string device) => true;

    /// <inheritdoc/>
    public override StationDevice Create(string device, DeviceSettings settings) =>
        new AlsaStationDevice(this, device);

    /// <inheritdoc/>
    public override bool HasMixer => true;
}

/// <summary>
/// An ALSA sound card, on a radio keyed by a PTT line (serial, CM108 or rigctld) or not at all.
/// The one device with a mixer and xrun counters.
/// </summary>
/// <remarks>
/// <b>All mixer work finishes before the PCM is opened.</b> See the comment at the mixer block
/// below and CLAUDE.md; <c>StartUpOrderTests</c> pins the order in this file.
/// </remarks>
internal sealed class AlsaStationDevice(DeviceKind kind, string spec) : StationDevice(kind, spec)
{
    /// <inheritdoc/>
    public override Task<DeviceOpening> OpenAsync(DeviceOpenContext context) =>
        Task.FromResult(Open(context));

    private DeviceOpening Open(DeviceOpenContext context)
    {
        int dspRate = context.DspRate;
        int captureRate = context.CaptureRate;
        string? configPath = context.ConfigPath;
        string? captureDeviceKey = context.CaptureDeviceKey;
        string? playbackDeviceKey = context.PlaybackDeviceKey;
        AlsaConfig? alsaConfig = context.Alsa;
        PttConfig? pttConfig = context.Ptt;
        RigControl? rig = context.Rig;
        IAudioOutput playback;
        IAudioInput input;
        AlsaAudioOutput? alsaOut = null;
        AlsaAudioInput? alsaIn = null;
        AlsaMixer? mixer = null;
        AlsaMixer? playbackMixer = null;
        MixerRuntime? mixerRuntime = null;
        string mixerWhyNot = DeviceOpening.NoMixer;

        IPttControl ptt = new NullPtt();

        // Hardware the config names but the box does not have is the single most likely thing to
        // go wrong on a first install (the seeded config points at a CM108 on /dev/hidraw0). Say
        // which setting, which file, and how to list what is really there - but exit 1, not 2, so
        // the unit keeps retrying and comes up by itself if the device was only slow to appear.
        try
        {
            switch (pttConfig?.Type)
            {
                case null:
                    break;
                case "serial":
                    string serialLine = pttConfig.Line ?? "rts";
                    ptt = new SerialPtt(pttConfig.Device, useRts: serialLine != "dtr", useDtr: serialLine == "dtr");
                    Console.WriteLine($"ptt: serial {pttConfig.Device} ({serialLine})");
                    break;
                case "cm108":
                    int gpio = pttConfig.Gpio ?? 3;
                    ptt = new Cm108Ptt(pttConfig.Device, gpio);
                    Console.WriteLine($"ptt: cm108 {pttConfig.Device} (gpio {gpio})");
                    break;
                case "rigctld" when rig is not null:
                    ptt = rig.KeyingPtt();
                    Console.WriteLine($"ptt: rigctld {rig.Endpoint} (T 1 to key, T 0 to unkey)");
                    break;
                default:
                    Console.Error.WriteLine($"unknown ptt type '{pttConfig.Type}'");
                    return DeviceOpening.Refused(2);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(DeviceDiagnostics.Ptt(pttConfig!, configPath, e));
            return DeviceOpening.Refused(1);
        }

        // The card's mixer, BEFORE the PCM is opened, and finished with before it is.
        //
        // The order is the point, and it was found the hard way on radio1 (2026-09-06): with the
        // mixer pass between the open and the first read, the station stopped receiving about a
        // second after start-up on 10 runs out of 13, with "receive feed dead: the input device
        // failed (snd_pcm_readi: Input/output error)".
        //
        // What that window really is, from the strace of the same failure on a qpsk3600 station
        // later the same day: everything between the open and the first steady reads is time the
        // capture stream can overrun in, and an overrun the recovery cannot get out of comes back
        // from snd_pcm_readi as -EIO, which reads as a dead device rather than as a late reader. It
        // was blamed at the time on a mixer control transfer colliding with the URB submission the
        // first read makes; that was a guess from the symptom, and the qpsk3600 failure had no mixer
        // traffic in this window at all. AlsaPcm holds the actual fix (a 500 ms capture buffer, a
        // start threshold of 1 so the stream starts on the first read, and a recovery that prepares,
        // starts, pauses and retries), and this ordering is what keeps the gap short to begin with.
        // Nothing here needs the PCM - reading a mixer never did, which is why --mixer-show works on
        // a running station - so the window costs nothing to close.
        //
        // Read even when the configuration asks for nothing, because a station's capture gain is the
        // difference between clean audio and clipped audio and the start-up log should say what it
        // is; written only where a key said so, so a file with no "alsa" section leaves every control
        // alone.
        //
        // A station that receives on one card and transmits through another ("captureDevice",
        // "playbackDevice") has two mixers: the capture gain, AGC and mic boost are on the receive
        // card, the playback level on the transmit card. Both are opened here, above the PCMs, for
        // the same reason. "alsa"."mixer"."card" names one mixer for both sides when it is set.
        string captureDevice = captureDeviceKey ?? Spec;
        string playbackDevice = playbackDeviceKey ?? Spec;
        string mixerCard = alsaConfig?.Mixer?.Card ?? AlsaMixer.CardFor(captureDevice);
        string playbackMixerCard = alsaConfig?.Mixer?.Card ?? AlsaMixer.CardFor(playbackDevice);
        bool mixerSplit = !string.Equals(mixerCard, playbackMixerCard, StringComparison.Ordinal);
        if (mixerSplit)
        {
            if (AlsaMixer.TryOpen(playbackMixerCard, out AlsaMixer? openedPlayback, out string playbackWhy))
            {
                playbackMixer = openedPlayback;
            }
            else
            {
                Console.WriteLine(
                    $"{MixerSetup.JournalPrefix}{playbackMixerCard} (transmit) has no mixer "
                    + $"({playbackWhy}); the transmit level is left as the card has it");
            }
        }

        bool captureMixerOpened = AlsaMixer.TryOpen(mixerCard, out AlsaMixer? openedMixer, out string mixerWhy);
        if (mixerSplit && !captureMixerOpened)
        {
            Console.WriteLine(
                $"{MixerSetup.JournalPrefix}{mixerCard} (receive) has no mixer ({mixerWhy}); the "
                + "capture gain is left as the card has it, and there is no AGC or mic boost to "
                + "switch off");
        }

        if (captureMixerOpened || playbackMixer is not null)
        {
            mixer = openedMixer;

            // A split station whose one card has no mixer still sets the other: the missing side is
            // a stand-in with no controls, so its levels are simply not found.
            IAlsaMixer captureSide = openedMixer is not null ? openedMixer : new AbsentMixer(mixerCard);
            IAlsaMixer playbackSide = !mixerSplit ? captureSide
                : playbackMixer is not null ? playbackMixer
                : new AbsentMixer(playbackMixerCard);

            // Guarded, not bare: these are top-level statements with nothing above them to catch
            // anything, and TryOpen only proves the ten entry points it uses itself. A libasound
            // missing one of the twenty the apply reaches would otherwise be a crash at every
            // start-up and a systemd restart loop, over a mixer. It costs the mixer instead.
            //
            // This is also where the state file is read and the precedence is decided: what
            // "alsa"."mixer" pins is applied and wins, what it says nothing about comes from a
            // change made on the page in some earlier run, and the rest is left as the card has it.
            // "." for a station configured entirely on the command line, which puts the state file
            // in the working directory. Nothing writes it on such a station anyway - the config API
            // refuses to be served without a --config file - but the read still has to have a path.
            mixerRuntime = MixerRuntime.Start(
                captureSide, playbackSide, alsaConfig?.Mixer, configPath ?? ".",
                MixerStateFile.StampFor(captureDevice, playbackDevice), Console.WriteLine,
                out string applyWhy);
            if (mixerRuntime is null)
            {
                string unread = !mixerSplit || playbackMixer is null ? mixerCard
                    : openedMixer is null ? playbackMixerCard
                    : $"{mixerCard} and {playbackMixerCard}";
                mixerWhyNot = $"{unread} could not be read or set: {applyWhy}";
                openedMixer?.Dispose();
                mixer = null;
                playbackMixer?.Dispose();
                playbackMixer = null;
            }
        }
        else if (mixerSplit)
        {
            // Both lines are already in the journal, one per card.
            mixerWhyNot = $"neither {mixerCard} nor {playbackMixerCard} has a mixer";
        }
        else
        {
            // Not a failure. A card with no mixer at all is a real thing (a bare I2S codec, a loopback
            // device), and so is a libasound with no mixer functions in it - neither is a reason for
            // a station to stop receiving.
            mixerWhyNot = $"{mixerCard} has no mixer: {mixerWhy}";
            Console.WriteLine(
                $"{MixerSetup.JournalPrefix}{mixerCard} has no mixer ({mixerWhy}); the capture gain "
                + "and the transmit level are left as the card has them, and there is no AGC or mic "
                + "boost to switch off");
        }

        // Which key chose the device being opened, so a failure names the one to fix: on a split
        // station either side can be the card that is missing.
        string opening = playbackDevice;
        string openingKey = playbackDeviceKey is null ? "device" : "playbackDevice";
        bool openingCapture = false;
        try
        {
            // Transmit: modulate at the DSP rate; play at the card-native capture rate through the
            // image-rejecting upsampler (cards commonly refuse to open 12 kHz playback directly).
            var alsaPlayback = new AlsaAudioOutput(
                playbackDevice, captureRate == dspRate ? dspRate : captureRate);
            alsaOut = alsaPlayback;
            playback = captureRate == dspRate
                ? alsaPlayback
                : new UpsamplingAudioOutput(alsaPlayback, dspRate);
            // Receive: capture at the card-native rate, into the half-second buffer AlsaAudioInput
            // asks for by default. It used to be 120 ms here, with 500 ms for ARDOP alone on the
            // grounds that only snd-aloop hiccupped; a qpsk3600 start-up on the bench CM108 overran
            // the 120 ms one on every run, so every station gets the deep buffer now.
            opening = captureDevice;
            openingKey = captureDeviceKey is null ? "device" : "captureDevice";
            openingCapture = true;
            var alsaInput = new AlsaAudioInput(captureDevice, captureRate);
            alsaIn = alsaInput;
            input = alsaInput;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(DeviceDiagnostics.Audio(
                opening, configPath, e, openingKey,
                split: !string.Equals(captureDevice, playbackDevice, StringComparison.Ordinal),
                capture: openingCapture));
            return DeviceOpening.Refused(1);
        }

        Console.WriteLine(string.Equals(captureDevice, playbackDevice, StringComparison.Ordinal)
            ? $"audio: {captureDevice} capture {captureRate} Hz -> {dspRate} Hz"
            : $"audio: capture {captureDevice} {captureRate} Hz -> {dspRate} Hz, "
                + $"playback {playbackDevice}");

        // What the card actually gave us, because the buffer is the difference between a station
        // that survives a slow first pass through the modem and one that dies at every start-up,
        // and "what did it negotiate" was previously only answerable with a strace. Said even when
        // the answer is that it would not say, for the same reason.
        if (alsaIn is AlsaAudioInput openedInput)
        {
            Console.WriteLine(openedInput.BufferMilliseconds > 0
                ? $"audio: capture buffer {openedInput.BufferMilliseconds} ms, "
                  + $"period {openedInput.PeriodMilliseconds} ms"
                : "audio: capture buffer: the card would not say");
        }

        // And what it refused, if it refused anything. A card that will not take the deep buffer
        // still runs, on the configuration the daemon always used, but it is now a station one
        // stalled start-up away from the bug this was all about, so it says so rather than leaving
        // the next person to strace it.
        if (alsaIn?.ConfigurationWarning is string captureRefusal)
        {
            Console.Error.WriteLine($"audio: {captureRefusal}");
        }

        if (alsaOut?.ConfigurationWarning is string playbackRefusal)
        {
            Console.Error.WriteLine($"audio: {playbackRefusal}");
        }

        return new DeviceOpening
        {
            Ptt = ptt,
            Playback = playback,
            Input = input,
            AlsaOut = alsaOut,
            AlsaIn = alsaIn,
            Mixer = mixer,
            PlaybackMixer = playbackMixer,
            MixerRuntime = mixerRuntime,
            MixerWhyNot = mixerWhyNot,
        };
    }
}
