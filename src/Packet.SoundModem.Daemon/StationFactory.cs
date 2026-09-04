using Microsoft.Data.Sqlite;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Survey;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// The parts of building a station that both flavours need: the DSP rate, the modems on the
/// channel, the frame log, the journal line per decoded frame and the id-beacon ghosts.
/// </summary>
/// <remarks>
/// <para>These were top-level statements in <c>Program.cs</c>, which is where the single-station
/// daemon assembles itself in the order it prints its start-up banner. The many-receiver flavour
/// has to build the same things fifty times, and a copy of any of them would be a second opinion
/// waiting to disagree with the first - the modem loop most of all, since a difference there is a
/// receiver quietly decoding something other than what the config asked for. So the reusable
/// parts moved here and <c>Program.cs</c> calls them where those statements used to be, in the
/// same order, writing the same lines.</para>
/// <para>Every line goes out through a <see cref="StationJournal"/>. A single station has no tag,
/// so its output is byte for byte what it was; a monitor's stations each carry their slug, which
/// is what keeps fifty of them readable in one journal.</para>
/// <para>What did not move is anything a monitor has no use for: the transmit side, the band
/// planner, KISS, PTT, ARDOP, paging, the survey and the config API all stay where they were.</para>
/// </remarks>
internal static class StationFactory
{
    /// <summary>
    /// Checks every mode name and settles the rate the shared audio channel runs at.
    /// </summary>
    /// <remarks>
    /// Mode names are checked HERE, before the band planner and the transmit-filter plan rather
    /// than in the per-modem loop that builds them. Those two ask the catalogue what a mode
    /// occupies, and the catalogue answers an unknown mode with its defaults - so a plugin that
    /// failed to load used to be planned as a baseband mode and only diagnosed afterwards, by
    /// which time the operator had read a band plan built on a fiction.
    /// </remarks>
    /// <returns>False when a mode is unknown or cannot share the channel; the reason has been
    /// journalled and the caller should exit 2.</returns>
    internal static bool TryResolveDspRate(
        IReadOnlyList<ModemConfig> modems, StationJournal journal, out int dspRate)
    {
        dspRate = 12000;
        foreach (ModemConfig unchecked_ in modems)
        {
            if (DaemonConfig.IsArdop(unchecked_.Mode) || ModemCatalog.IsKnown(unchecked_.Mode))
            {
                continue;
            }

            journal.WriteError($"modem {unchecked_.SubChannel}: unknown mode '{unchecked_.Mode}'");
            ReportUnknownMode(unchecked_.Mode, journal);
            return false;
        }

        // The shared channel runs at one of two rates, so a mode declaring a third has nowhere to
        // run: the decision below would silently hand it 12000 and it would demodulate nothing
        // while looking configured. Only a plugin mode can get here - every built-in declares
        // 12000 or 48000.
        ModemConfig? oddRate = modems.FirstOrDefault(
            m => ModemPluginRegistry.IsRegistered(m.Mode)
                && ModemCatalog.DspRateFor(m.Mode) is not (12000 or 48000));
        if (oddRate is not null)
        {
            journal.WriteError(
                $"modem {oddRate.SubChannel}: mode '{oddRate.Mode}' runs at "
                + $"{ModemCatalog.DspRateFor(oddRate.Mode)} Hz, and the shared audio channel runs at "
                + "12000 or 48000. A plugin mode has to declare one of those and resample internally if "
                + "its own DSP wants something else.");
            return false;
        }

        // ARDOP's engine is native 12 kHz; on a 48 kHz channel (any fsk9600/c4fsk/freedv/ms110d
        // modem present) ArdopChannelBridge decimates its receive audio down and upsamples its
        // bursts back up, so it shares the channel either way.
        int rate = modems.Any(m => ModemCatalog.DspRateFor(m.Mode) == 48000) ? 48000 : 12000;
        dspRate = rate;

        // PLUGIN MODES ONLY, and the distinction is the whole point. Every built-in mode's DSP is
        // rate-parameterised and is built at whatever the channel settled on - afsk1200 beside fsk9600
        // runs the pair at 48 kHz and always has, which is a supported and ordinary arrangement. A plugin
        // mode is different: its descriptor names one rate, the catalogue refuses to build it at any
        // other, and so a 12 kHz plugin mode sharing a channel with any 48 kHz mode simply cannot work.
        // Saying which two modes cannot share beats the exception the build would otherwise throw.
        ModemConfig? wrongRate = modems.FirstOrDefault(
            m => ModemPluginRegistry.IsRegistered(m.Mode) && ModemCatalog.DspRateFor(m.Mode) != rate);
        if (wrongRate is not null)
        {
            string forcedBy = modems.First(m => ModemCatalog.DspRateFor(m.Mode) == rate).Mode;
            journal.WriteError(
                $"modem {wrongRate.SubChannel}: mode '{wrongRate.Mode}' runs at "
                + $"{ModemCatalog.DspRateFor(wrongRate.Mode)} Hz, but this channel runs at {rate} Hz "
                + $"because '{forcedBy}' needs it. They cannot share one sound card - give them separate "
                + "daemons, or drop one.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checked rather than left to ModemCatalog.Create's throw: 38 mode names is plenty to
    /// mistype, and "unknown mode 'fsk9600il2p'" with a stack trace under it does not tell you
    /// that the name you wanted was one hyphen away.
    /// </summary>
    private static void ReportUnknownMode(string mode, StationJournal journal)
    {
        // A qualified name is a plugin mode, and "unknown mode" is the wrong diagnosis for one: the
        // operator did not mistype it, the plugin that provides it is not loaded. Without this the
        // failure sits under a "FAILED /path - no such file" line and reads as two unrelated problems.
        int separator = mode.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0)
        {
            string pluginId = mode[..separator];
            journal.WriteError(
                ModemPluginRegistry.IsPluginRegistered(pluginId)
                    ? $"  modem plugin '{pluginId}' is loaded and does not provide it - it provides: "
                        + string.Join(", ", ModemPluginRegistry.RegisteredModes
                            .Where(m => m.StartsWith(pluginId + ":", StringComparison.Ordinal)))
                    : $"  no modem plugin registered for '{pluginId}' - check the \"modemPlugins\" "
                        + "path, and any plugin failure reported above");
        }

        string[] near = ModemCatalog.NearestModes(mode);
        if (near.Length > 0)
        {
            journal.WriteError($"  did you mean: {string.Join(", ", near)}");
        }

        journal.WriteError(
            $"  the {ModemCatalog.KnownModes.Count} built-in mode names are listed at "
            + "https://github.com/packet-net/pdn-soundmodem/blob/main/docs/modes.md");
    }

    /// <summary>
    /// Builds every configured modem onto <paramref name="channel"/> and says what it built.
    /// </summary>
    /// <remarks>
    /// The one piece of construction a second copy would hurt most: two flavours with their own
    /// modem loops would be two answers to "what does this config actually run", and the
    /// difference would show up as a receiver decoding something other than what was asked for,
    /// with nothing anywhere saying so. ARDOP is skipped - it is not a demodulator but a whole
    /// virtual TNC with its own host protocol, wired by the station that has one.
    /// </remarks>
    /// <returns>False when a modem's configuration is refused; the reason has been journalled
    /// and the caller should exit 2.</returns>
    internal static bool TryAddModems(
        SoundModemChannel channel,
        IReadOnlyList<ModemConfig> modems,
        int dspRate,
        PskDetector? detectorOverride,
        StationJournal journal)
    {
        foreach (ModemConfig modemConfig in modems)
        {
            int subChannel = modemConfig.SubChannel;
            string mode = modemConfig.Mode;
            double? frequency = modemConfig.Frequency;
            if (DaemonConfig.IsArdop(mode))
            {
                // Not a demodulator: a whole virtual TNC with its own host protocol. Wired by the
                // caller, against the same channel, as a receive tap plus a priority transmitter.
                continue;
            }

            // The mode name was checked before the band plan was drawn, so by here it is known.
            if (frequency is not null && !ModemCatalog.AcceptsCentreFrequency(mode))
            {
                // The same wording as ModemCatalog.Create's own refusal: this message had drifted
                // to claim only afsk*/bpsk*/qpsk* accept a frequency, a mode family after the
                // spec-fixed ms110d-*/freedv-* ones learned to take one by decoration.
                journal.WriteError(
                    $"modem {subChannel}: mode '{mode}' occupies the audio band from DC upwards and " +
                    "has no centre frequency to move - drop the frequency override (the " +
                    "afsk*/bpsk*/qpsk* and spec-fixed ms110d-*/freedv-* modes accept one)");
                return false;
            }

            // Refused rather than ignored, same as the frequency override above: an operator who wrote
            // this down believes their modem changed behaviour, and on a mode with no second plain
            // reading to release there is nothing for it to change. Named modes rather than a pattern,
            // because fsk9600-il2p and fsk4800-il2p do run the CRC despite their names.
            if (modemConfig.AcceptPlainIl2p && !ModemCatalog.RunsIl2pCrc(mode))
            {
                journal.WriteError(
                    $"modem {subChannel}: mode '{mode}' does not run IL2P+CRC, so it has no separate "
                    + "plain-IL2P reading to release - drop \"acceptPlainIl2p\"");
                journal.WriteError(
                    "  it applies to the IL2P+CRC modes: afsk300-il2pc, afsk1200-il2p, bpsk*, qpsk*, "
                    + "fsk9600-il2p, fsk4800-il2p, c4fsk*, freedv-*, ms110d-*");
                return false;
            }

            try
            {
                channel.AddModem(subChannel, sink => ModemCatalog.Create(mode, dspRate, sink,
                    new ModemOptions(
                        CentreFrequencyHz: frequency,
                        OffsetPairs: modemConfig.OffsetPairs,
                        OffsetStepHz: modemConfig.OffsetStepHz,
                        Detector: detectorOverride,
                        AcceptPlainIl2p: modemConfig.AcceptPlainIl2p)));
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
            {
                // A built-in factory that threw here would be our bug and a stack trace would be the
                // right output. A plugin's can throw for reasons that are entirely the plugin's own -
                // a geometry it will not accept, a rate it will not run - and that is an operator's
                // problem with somebody else's assembly, not a crash to be reported against this one.
                journal.WriteError($"modem {subChannel}: mode '{mode}' would not build");
                journal.WriteError($"  {failure.Message}");
                return false;
            }

            journal.Write($"modem {subChannel}: {mode}{(frequency is { } f ? $" @ {f} Hz" : "")}");
            if (mode.StartsWith("ms110d-wn", StringComparison.Ordinal)
                && int.TryParse(mode.AsSpan("ms110d-wn".Length), out int wn)
                && Packet.SoundModem.Ms110d.Ms110dModem.PoorStatusNote(wn) is { } poorNote)
            {
                journal.Write($"modem {subChannel}: {poorNote}");
            }

            if (modemConfig.AcceptPlainIl2p)
            {
                // Said out loud because it removes the one check that separates a frame from noise the
                // FEC happened to like, and a host has no way to know: such a frame arrives as an
                // ordinary KISS data frame like any other. Nothing is printed in the default case, where
                // the same frames are read and shown and simply not handed on, because that costs the
                // host nothing and would be a line on every IL2P+CRC modem on the station.
                journal.Write(
                    "  passing plain IL2P (no trailing CRC) to the host: those frames are checked by "
                    + "Reed-Solomon alone");
            }
        }

        return true;
    }

    /// <summary>
    /// What the station is doing, one line per frame, in the journal.
    /// </summary>
    /// <remarks>
    /// FrameReceivedWithQuality rather than FrameReceived: the mode, the CRC verdict, the FEC
    /// corrections and the frequency offset are all already measured per frame, and they are what
    /// turn "something decoded" into something an operator can act on.
    /// </remarks>
    internal static void JournalReceivedFrames(SoundModemChannel channel, StationJournal journal) =>
        channel.FrameReceivedWithQuality += (subChannel, frame, quality) =>
            journal.Write(ActivityLog.Received(subChannel, frame, quality));

    /// <summary>
    /// Opens this station's frame log and wires it to the channel: every frame it hears, written
    /// down. Subscribed to the same event the KISS servers and the waterfall use, so it records
    /// what was actually decoded rather than a second opinion.
    /// </summary>
    /// <returns>False when the log cannot be opened; the reason has been journalled and the
    /// caller should exit 2.</returns>
    internal static bool TryOpenFrameLog(
        string path,
        IReadOnlyList<ModemConfig> modems,
        SoundModemChannel channel,
        StationJournal journal,
        out FrameLog? frameLog)
    {
        frameLog = null;

        // Where each modem sits, for the log's audio_hz/rf_hz columns. Read per frame because both
        // halves of the log - what was heard and what was sent - fill those columns from it.
        Dictionary<int, (double? Audio, double? Rf)> rfByModem = modems.ToDictionary(
            m => m.SubChannel,
            m => (Audio: m.Frequency, Rf: m.RfFrequency));

        FrameLog log;
        try
        {
            log = FrameLog.Open(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            journal.WriteError(
                $"cannot open the frame log at {path}\n"
                + $"  {e.Message}\n"
                + "  Set by \"frameLog\".\"path\". The service user must be able to write to its\n"
                + "  directory; remove the \"frameLog\" section to run without one.");
            return false;
        }

        channel.FrameReceivedWithQuality += (sub, frame, quality) =>
        {
            (double? audio, double? rf) = rfByModem.TryGetValue(sub, out var placement)
                ? placement
                : (null, null);
            log.Record(sub, frame, quality, audio, rf);
        };
        journal.Write($"frame log: {log.Path}");
        frameLog = log;
        return true;
    }

    /// <summary>
    /// What each modem is meant to occupy, for the waterfall's overlay: the centre so the label
    /// reads as the operator placed it rather than as the probe measured it, and a width for
    /// ARDOP, which is a receive tap rather than an <c>IModem</c> and so cannot be probed at all.
    /// </summary>
    internal static IReadOnlyList<DeclaredBand> DeclaredBandsFor(IReadOnlyList<ModemConfig> modems) =>
        [.. modems.Select(m => new DeclaredBand(
            m.SubChannel,
            DaemonConfig.IsArdop(m.Mode) ? "ardop" : m.Mode,
            m.Frequency ?? (DaemonConfig.IsArdop(m.Mode) ? ArdopChannelBridge.NativeCentreHz : 0),
            DaemonConfig.IsArdop(m.Mode)
                ? m.Bandwidth ?? ArdopChannelBridge.WidestBandwidthHz
                : null))];

    /// <summary>
    /// Opens the links pane on the links the station already knows about.
    /// </summary>
    /// <remarks>
    /// <para>The log has the bytes of every frame it heard or sent, so the observer is simply
    /// shown them again, in order and with their own timestamps: a link that was up when this
    /// process last stopped is up on the first page load rather than after its next frame. Two
    /// thousand frames is an afternoon on a busy port and a few milliseconds of work.</para>
    /// <para>Every row but a withheld one. A frame read on Reed-Solomon alone does not make a
    /// link when it is heard, and replaying it out of the log must not make one either, or a
    /// restart would put back exactly the cards the live path refuses. The test is the log's own
    /// <c>monitor_only</c> column rather than a null <c>crc_valid</c>, which is also null on
    /// HDLC, on FX.25 and on our own transmissions - reading it as "withheld" would empty the
    /// pane on every port that does not run IL2P+CRC.</para>
    /// </remarks>
    internal static void BackfillLinks(WaterfallWebServer server, FrameLog frameLog)
    {
        foreach ((LoggedFrame logged, byte[] payload) in frameLog.RecentWithPayload(2000))
        {
            if (logged.MonitorOnly)
            {
                continue;
            }

            server.Links.Observe(
                logged.SubChannel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                payload, logged.HeardAt, logged.Transmitted);
        }
    }

    /// <summary>
    /// Ghost demodulators for the station identifications a NinoTNC sends alongside its PSK SSB
    /// data modes rather than within them (300 AFSK AX.25, 200 Hz above the carrier - see
    /// <see cref="IdBeaconGhost"/>).
    /// </summary>
    /// <remarks>
    /// <para>Without one, an ident is a recurring burst in the middle of the channel that every
    /// station can see and none can read. Called after the waterfall and the frame log exist,
    /// because a ghost reports to both; the journal line stands alone when neither is
    /// configured.</para>
    /// <para>Receive taps, not modems: no KISS sub-channel (a host asking for packet data should
    /// not have to filter idents out of it), no contribution to carrier sense, and nothing drawn
    /// on the waterfall - the ident rides on a modem that already has a band there.</para>
    /// </remarks>
    /// <param name="onDecode">Told about each ident read, for anything that learns decodes from
    /// the channel's own event - a receive tap raises none. Null where there is nothing to tell.</param>
    internal static void WireIdBeacons(
        SoundModemChannel channel,
        IReadOnlyList<ModemConfig> modems,
        int dspRate,
        WaterfallWebServer? waterfallServer,
        FrameLog? frameLog,
        Action<int, byte[], FrameQuality>? onDecode,
        StationJournal journal)
    {
        var ghostCentres = new HashSet<long>();
        foreach (ModemConfig modemConfig in modems)
        {
            // Two PSK modems tuned to the same centre would hear the same TNC's ident twice and list
            // it twice. A band plan gives them different centres, so this is insurance rather than
            // the normal case - but the duplicate would be indistinguishable from two real beacons.
            double centre = IdBeaconGhost.CentreHzFor(modemConfig.Frequency);
            if (!IdBeaconGhost.AppliesTo(modemConfig.Mode) || !ghostCentres.Add((long)Math.Round(centre)))
            {
                continue;
            }

            IdBeaconGhost ghost = IdBeaconGhost.TryCreate(
                modemConfig.SubChannel, modemConfig.Mode, modemConfig.Frequency, dspRate)!;

            // The ident's RF frequency follows its audio offset from the modem it accompanies, so a
            // band-planned station gets a real frequency in the log rather than a blank column.
            double? ghostRfHz = modemConfig.RfFrequency + IdBeaconGhost.BeaconOffsetHz;
            ghost.BeaconHeard += (frame, quality) =>
            {
                Ax25AddressParser.TryParse(frame, out string source, out string destination);

                // Alongside the rx[N] lines, and distinct from them: this is a station saying who it
                // is on a channel where its data is unreadable to us, which is worth its own word.
                journal.Write(
                    $"id[{ghost.SubChannel}] {(source.Length > 0 ? source : "?")}"
                    + $">{(destination.Length > 0 ? destination : "?")} {frame.Length} bytes");

                waterfallServer?.ReportIdBeacon(
                    ghost.SubChannel,
                    quality.Mode,
                    string.IsNullOrWhiteSpace(source) ? null : source,
                    string.IsNullOrWhiteSpace(destination) ? null : destination,
                    frame.Length,
                    // How far the identifying station's dial sits from ours, measured off its carrier.
                    quality.FrequencyOffsetHz);

                frameLog?.Record(
                    ghost.SubChannel, frame, quality, ghost.CentreHz, ghostRfHz,
                    modeName: $"ID beacon ({ModeNames.Display(quality.Mode)})");

                // And anything that learns its decodes from the channel's own event - the signal
                // survey - which a receive tap does not raise. Without this an ident the station
                // successfully read was still filed as a burst nothing decoded, captured, and
                // charged to the capture budget and the frequency cooldown that a real unknown
                // signal needs. Same shape as the ARDOP omission of 2026-08-05, and found the same
                // way: by opening a capture.
                onDecode?.Invoke(ghost.SubChannel, frame, quality);
            };

            channel.AddReceiveTap(ghost.Process);

            // A tap is not one of the channel's modems, so the unkey sweep that resets them misses
            // this: without it the demodulator carries its pre-keyup carrier state across the silence.
            channel.TransmittingChanged += keyed =>
            {
                if (!keyed)
                {
                    ghost.ResetCarrierState();
                }
            };

            journal.Write(
                $"modem {modemConfig.SubChannel}: id beacons - listening in "
                + $"{ghost.Mode} @ {ghost.CentreHz:0.#} Hz");
        }
    }
}
