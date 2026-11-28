using Packet.SoundModem.OfdmFmTool;

TimeProvider time = TimeProvider.System;
ParseResult parsed = CommandLine.Parse(args);
if (parsed.Help)
{
    Console.WriteLine(Usage.Text);
    return 0;
}

if (parsed.Options is not { } options)
{
    Console.Error.WriteLine(StampedLog.Format(time, parsed.Error!));
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });

return await Runner.RunAsync(options, time, stop.Token, Console.WriteLine);
