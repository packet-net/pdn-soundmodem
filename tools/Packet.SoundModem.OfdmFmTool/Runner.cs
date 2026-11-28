using M0LTE.Radio.Audio;
using Packet.SoundModem.CarrierSense;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Daemon;
using Packet.SoundModem.FlexRadio;
using Packet.SoundModem.Rig;
using DaemonStation = Packet.SoundModem.Daemon.Station;
using Packet.SoundModem.Modems.OfdmFmStation;
using Packet.SoundModem.OfdmFmTool.Ax25;

namespace Packet.SoundModem.OfdmFmTool;

/// <summary>
/// Runs one command against a real device: opens the audio and the PTT through the daemon's own device kinds, builds the channel and the station host, and closes everything on every way out.
/// This is the only class that touches hardware; the commands themselves are <see cref="ToolSession"/>'s.
/// </summary>
/// <remarks>
/// <para><b>The longest key-up.</b> The tool keys only through the channel's transmitter, from what the station asks for. The station's bursts end at 2.5 s of audio
/// (<c>Station.BurstCeilingSamples</c> in M0LTE.OfdmFm, 120000 samples at 48 kHz), and a datagram too big for one burst is refused before anything is queued. The channel adds the key-up delay and tail
/// (300 ms and 20 ms unless set). The station's identification burst is at most 1.5 s, and the Morse identification is a callsign of at most 16 characters. No command asks for anything else.</para>
/// <para><b>Release.</b> The PTT is wrapped so that, once released, nothing keys it again. It is released when the command ends, when the process exits for any reason, and on an unhandled exception.</para>
/// </remarks>
internal static class Runner
{
    private const int Rate = 48000;

    public static async Task<int> RunAsync(ToolOptions options, TimeProvider time, CancellationToken stop, Action<string> console)
    {
        FileStream? logFile = null;
        StreamWriter? fileWriter = null;
        try
        {
            if (options.LogFile is not null)
            {
                logFile = new FileStream(options.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                fileWriter = new StreamWriter(logFile) { AutoFlush = true };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            console(StampedLog.Format(time, $"cannot open the log file: {e.Message}"));
            return 2;
        }

        Action<string>[] sinks = fileWriter is null ? [console] : [console, fileWriter.WriteLine];
        await using var log = new StampedLog(time, sinks);
        log.Write(Versions.Line());
        try
        {
            return await RunOpenAsync(options, time, stop, log);
        }
        finally
        {
            await log.DisposeAsync();
            fileWriter?.Dispose();
            logFile?.Dispose();
        }
    }

    /// <summary>
    /// Opens the radio's carrier source through the daemon's own code, which asks the radio once to report carrier changes and sends nothing else (no RSSI polling is set up). What it prints goes through the log.
    /// </summary>
    private static IDisposable? OpenCarrier(string? spec, StampedLog log, ref IChannelBusySource? carrier, ref bool audioFallback)
    {
        if (spec is null || CarrierSpec.Parse(spec, out string port) is not null)
        {
            return null;
        }

        var said = new StringWriter();
        TextWriter outWas = Console.Out;
        TextWriter errWas = Console.Error;
        Console.SetOut(said);
        Console.SetError(said);
        IChannelBusySource? source;
        IDisposable? owned;
        try
        {
            source = StationCarrierSense.Open(new CarrierSenseConfig { Radio = "tait", Port = port }, text => log.Write(text), out owned, out audioFallback);
        }
        finally
        {
            Console.SetOut(outWas);
            Console.SetError(errWas);
        }

        foreach (string line in said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The one line about where the indication comes from is the tool's own, below.
            if (!line.StartsWith("carrier sense: from the radio on", StringComparison.Ordinal))
            {
                log.Write(line);
            }
        }

        if (source is not null)
        {
            log.Write($"Carrier indication from the radio on {port}, carrier detect only");
            carrier = new LoggedCarrier(source, $"the radio on {port}", log.Write);
        }

        return owned;
    }

    private static async Task<int> RunOpenAsync(ToolOptions options, TimeProvider time, CancellationToken stop, StampedLog log)
    {
        bool canTransmit = options.Ptt is not null;
        PttConfig? pttConfig = null;
        if (options.Ptt is not null)
        {
            pttConfig = PttSpec.TryParse(options.Ptt, out string pttProblem);
            if (pttConfig is null)
            {
                log.Write(pttProblem);
                return 2;
            }
        }

        StationDevice device;
        try
        {
            device = DeviceKinds.Resolve(options.Device, null, new DeviceSettings(null, HasWaterfall: false));
        }
        catch (InvalidDataException malformed)
        {
            log.Write(malformed.Message);
            return 2;
        }

        if (pttConfig is not null && device.PttRefusal is string pttRefusal)
        {
            log.Write(pttRefusal);
            return 2;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        IChannelBusySource? carrier = null;
        bool audioFallback = true;
        using IDisposable? carrierLifetime = OpenCarrier(options.Carrier, log, ref carrier, ref audioFallback);
        var channel = new SoundModemChannel(Rate, time, channelBusySource: carrier, audioFallback: audioFallback);
        if (!canTransmit)
        {
            ListenOnly.Apply(channel);
        }
        else if (device.ReceiveOnlyReason is string deviceReason)
        {
            channel.ReceiveOnlyReason = deviceReason;
            canTransmit = false;
        }

        // Whatever the device prints while it opens is part of the run: it goes through the log, stamped.
        DeviceOpening opened;
        var said = new StringWriter();
        TextWriter outWas = Console.Out;
        TextWriter errWas = Console.Error;
        Console.SetOut(said);
        Console.SetError(said);
        try
        {
            opened = await device.OpenAsync(new DeviceOpenContext
            {
                DspRate = Rate,
                ConfigPath = null,
                Journal = new StationJournal("", log.Write, log.Write),
                Channel = channel,
                Waterfall = null,
                RadioLost = () => cancellation.Cancel(),
                Cancellation = cancellation.Token,
                Sideband = "usb",
                ReceiveDialHz = null,
                FlexPacketBuffer = 3,
                FlexTuning = new FlexTuning(),
                TransmitBands = [],
                CaptureRate = Rate,
                CaptureDeviceKey = null,
                PlaybackDeviceKey = null,
                Alsa = null,
                Ptt = pttConfig,
                Rig = null,
            });
        }
        finally
        {
            Console.SetOut(outWas);
            Console.SetError(errWas);
        }

        string[] openLines = said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (opened.ExitCode is int refused)
        {
            log.Write(openLines.Length > 1 && openLines[0].StartsWith("cannot open", StringComparison.Ordinal)
                ? $"{openLines[0]}: {openLines[1]}"
                : openLines.FirstOrDefault() ?? "cannot open the device");
            return refused;
        }

        foreach (string line in openLines)
        {
            log.Write(line);
        }

        var releasable = new ReleasablePtt(opened.Ptt);
        EventHandler exitRelease = (_, _) => releasable.Release();
        UnhandledExceptionEventHandler crashRelease = (_, _) => releasable.Release();
        AppDomain.CurrentDomain.ProcessExit += exitRelease;
        AppDomain.CurrentDomain.UnhandledException += crashRelease;
        Task transmitter = Task.CompletedTask;
        Thread? listening = null;
        AudioRecorder? recorder = null;
        OfdmFmStationHost? host = null;
        ToolSession? session = null;
        Ax25Link? ax25 = null;
        DaemonStation? station = null;
        bool faulted = false;
        try
        {
            if (options.RecordFile is not null)
            {
                try
                {
                    recorder = new AudioRecorder(options.RecordFile, Rate);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Write($"cannot open the recording file: {e.Message}");
                    return 2;
                }

                channel.AddReceiveTap(recorder.Tap);
            }

            channel.PttFailed += failure => log.Write($"ptt: {failure.Message}");
            if (canTransmit)
            {
                transmitter = channel.RunTransmitterAsync(opened.Playback, releasable, cancellation.Token);
                _ = transmitter.ContinueWith(
                    failed =>
                    {
                        log.Write($"transmit: the audio output failed: {failed.Exception!.GetBaseException().Message}");
                        faulted = true;
                        cancellation.Cancel();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }

            IdentificationNote? note = canTransmit && StateFiles.NotePath(options.Call, options.Device, Environment.GetEnvironmentVariable) is { } notePath
                ? new IdentificationNote(notePath, time, log.Write)
                : null;
            host = new OfdmFmStationHost(channel, new Random(), new OfdmFmStationHostOptions { LastIdentification = note?.Read(), TimeProvider = time });
            session = new ToolSession(host, options.Call, options.To, options.Detail, canTransmit, log, time, note) { Ax25Mode = options.Verb is Verb.Answer or Verb.Call };
            if (session.Start() is string notReady)
            {
                log.Write(notReady);
                return 2;
            }

            station = new DaemonStation(
                new StationOptions
                {
                    Channel = channel,
                    Input = opened.Input,
                    DspRate = Rate,
                    Journal = new StationJournal("", log.Write, log.Write),
                    DeviceKind = device.DeadFeedKind,
                    SessionLive = opened.SessionLive,
                    XrunCounters = opened.AlsaIn is null && opened.AlsaOut is null ? null : () => (opened.AlsaIn?.Xruns ?? 0, opened.AlsaOut?.Xruns ?? 0),
                    InputLossCount = opened.AlsaIn is { } alsaIn ? () => alsaIn.Xruns : opened.InputLossCount,
                    StarvationMessage = opened.StarvationMessage,
                    TimeProvider = time,
                },
                cancellation.Token);
            station.Faulted += fault =>
            {
                log.Write(fault.Reason);
                faulted = true;
                cancellation.Cancel();
            };
            listening = new Thread(station.Run) { IsBackground = true, Name = "receive" };
            listening.Start();

            int code = 0;
            switch (options.Verb)
            {
                case Verb.Answer or Verb.Call:
                    ax25 = new Ax25Link(host, channel, options.Call, time, log);
                    await ax25.StartAsync();
                    code = options.Verb == Verb.Answer
                        ? await ax25.AnswerAsync(cancellation.Token)
                        : await ax25.CallAsync(options.To!, options.Get, options.Get is null ? Console.In : null, cancellation.Token);
                    break;
                case Verb.Listen:
                    await session.ListenAsync(cancellation.Token);
                    break;
                case Verb.Send:
                    code = await session.SendTextAsync(System.Text.Encoding.UTF8.GetBytes(options.Text), cancellation.Token) ? 0 : 1;
                    break;
                default:
                    PingSummary summary = await session.PingAsync(options.Count, options.Size, cancellation.Token);
                    code = summary.Answered > 0 ? 0 : 1;
                    break;
            }

            return faulted ? 1 : code;
        }
        finally
        {
            // The order matters: stop what keys the radio first, release the PTT, then close the devices the loops were using.
            await cancellation.CancelAsync();
            try
            {
                await transmitter;
            }
            catch (Exception)
            {
                // Ended by the cancellation, or already said.
            }

            releasable.Release();
            listening?.Join(TimeSpan.FromSeconds(5));
            if (ax25 is not null)
            {
                await ax25.DisposeAsync();
            }

            host?.Dispose();
            station?.Dispose();
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            if (!device.ClosesItsOwnStreams)
            {
                (opened.Ptt as IDisposable)?.Dispose();
                (opened.Playback as IDisposable)?.Dispose();
                (opened.Input as IDisposable)?.Dispose();
            }

            opened.Mixer?.Dispose();
            opened.PlaybackMixer?.Dispose();
            if (recorder is not null)
            {
                await recorder.DisposeAsync();
            }

            AppDomain.CurrentDomain.ProcessExit -= exitRelease;
            AppDomain.CurrentDomain.UnhandledException -= crashRelease;
        }
    }
}
