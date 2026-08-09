using System.Globalization;
using System.Runtime.InteropServices;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// A virtual audio link made of two named pipes: raw 32-bit float samples in one, out of the
/// other. Two daemons pointed at each other's pipes are on the same "air" with no sound card, no
/// kernel module and no radio.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> Proving two stations can actually hear each other - through
/// modulation, an audio stream and demodulation, rather than through a loopback inside one
/// process. That is a different claim from "the modem round-trips a buffer", and until you have
/// made it you do not know that the transmit path and the receive path agree about anything. It
/// also runs anywhere: a CI container has no sound hardware, and <c>snd-aloop</c> needs a kernel
/// module a container cannot load.</para>
/// <para><b>Not a radio.</b> There is no channel here at all - no noise, no filtering, no
/// deviation, nothing. Samples arrive exactly as they were written. That makes it the right tool
/// for "do these two agree" and the wrong one for any performance question; those belong to the
/// Watterson and FM channel models, which put a measured path in the way on purpose.</para>
/// <para><b>Spelling:</b> <c>pipe:&lt;in&gt;,&lt;out&gt;[,&lt;rate&gt;]</c>. Station A takes
/// <c>pipe:/tmp/a-to-b,/tmp/b-to-a</c> reversed against station B's
/// <c>pipe:/tmp/b-to-a,/tmp/a-to-b</c> - what one writes, the other reads.</para>
/// </remarks>
internal static class PipeAudio
{
    /// <summary>True for a device string this module handles.</summary>
    public static bool IsPipe(string? device) =>
        device is not null && device.StartsWith("pipe:", StringComparison.Ordinal);

    /// <summary>Splits a <c>pipe:</c> device string, or throws with what it should look like.</summary>
    public static (string In, string Out, int Rate) Parse(string device)
    {
        string[] parts = device["pipe:".Length..].Split(',');
        if (parts.Length is < 2 or > 3
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidDataException(
                $"\"{device}\" is not a pipe device. It is pipe:<in>,<out>[,<rate>] - the FIFO to "
                + "read capture audio from, the FIFO to write transmit audio to, and optionally "
                + "the sample rate (48000 by default). Two daemons take each other's reversed.");
        }

        int rate = 48000;
        if (parts.Length == 3 && !int.TryParse(
            parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out rate))
        {
            throw new InvalidDataException($"\"{parts[2]}\" is not a sample rate");
        }

        return (parts[0], parts[1], rate);
    }

    /// <summary>Creates <paramref name="path"/> as a FIFO if it is not one already.</summary>
    public static void EnsureFifo(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        int made = Mkfifo(path, 0b110_110_110);
        if (made != 0 && !File.Exists(path))
        {
            throw new IOException($"could not create the FIFO {path}");
        }
    }

    // DllImport rather than LibraryImport: the source generator that backs the newer attribute
    // emits unsafe code, and turning unsafe on for the whole daemon to reach two syscalls is a
    // poor trade.
    [DllImport("libc", EntryPoint = "mkfifo", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Mkfifo(string path, uint mode);
}

/// <summary>
/// Capture side of a <see cref="PipeAudio"/> link: whatever the other station wrote, paced to
/// wall clock, with silence in the gaps.
/// </summary>
/// <remarks>
/// <b>The silence matters.</b> A FIFO delivers nothing between transmissions, and a capture device
/// that simply blocked there would stall the whole receive loop - the DCD would freeze at whatever
/// it last saw and the dead-feed watch would eventually call the input dead, which it is not. A
/// real sound card hands up a continuous stream carrying silence when the band is quiet, so this
/// does too.
/// </remarks>
internal sealed class PipeAudioInput : IAudioInput, IDisposable
{
    private readonly FileStream _fifo;
    private readonly byte[] _bytes;
    private long _delivered;
    private long _startMilliseconds = -1;

    public PipeAudioInput(string path, int sampleRate)
    {
        PipeAudio.EnsureFifo(path);

        // Opened read-write, which on a FIFO is the one mode that neither blocks waiting for a
        // writer nor ever reports end of file when the writer goes away. Two daemons starting in
        // either order therefore both come up, which read-only would not manage: each would block
        // in its constructor waiting for the other to open the far end.
        _fifo = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0, useAsync: false);
        SampleRate = sampleRate;
        _bytes = new byte[sampleRate * sizeof(float)];
    }

    public int SampleRate { get; }

    public int Read(Span<float> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (_startMilliseconds < 0)
        {
            _startMilliseconds = Environment.TickCount64;
        }

        // Paced like a capture device: hand back no more than wall clock has had time to produce,
        // so a burst written in one go is heard over the time it actually occupies.
        long due = ((Environment.TickCount64 - _startMilliseconds) * SampleRate / 1000) - _delivered;
        while (due <= 0)
        {
            Thread.Sleep(5);
            due = ((Environment.TickCount64 - _startMilliseconds) * SampleRate / 1000) - _delivered;
        }

        int want = (int)Math.Min(Math.Min(due, destination.Length), _bytes.Length / sizeof(float));
        int available = Available();
        int taken = Math.Min(want, available);
        if (taken > 0)
        {
            int wanted = taken * sizeof(float);
            int got = 0;
            while (got < wanted)
            {
                int read = _fifo.Read(_bytes, got, wanted - got);
                if (read <= 0)
                {
                    break;
                }

                got += read;
            }

            taken = got / sizeof(float);
            for (int n = 0; n < taken; n++)
            {
                destination[n] = BitConverter.ToSingle(_bytes, n * sizeof(float));
            }
        }

        // The rest of what wall clock is owed is silence, which is what a quiet band sounds like.
        destination[taken..want].Clear();
        _delivered += want;
        return want;
    }

    /// <summary>Whole samples sitting in the FIFO right now, without blocking for more.</summary>
    private int Available()
    {
        try
        {
            int bytes = FionRead(_fifo.SafeFileHandle.DangerousGetHandle());
            return bytes <= 0 ? 0 : bytes / sizeof(float);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return 0;
        }
    }

    private static int FionRead(nint handle)
    {
        int bytes = 0;
        return Ioctl(handle, 0x541B /* FIONREAD */, ref bytes) < 0 ? 0 : bytes;
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(nint fd, nuint request, ref int argument);

    public void Dispose() => _fifo.Dispose();
}

/// <summary>Transmit side of a <see cref="PipeAudio"/> link: raw float samples into the FIFO.</summary>
internal sealed class PipeAudioOutput : IAudioOutput, IDisposable
{
    private readonly FileStream _fifo;
    private readonly byte[] _bytes = new byte[8192 * sizeof(float)];

    public PipeAudioOutput(string path, int sampleRate)
    {
        PipeAudio.EnsureFifo(path);

        // Read-write again, for the same reason: opening a FIFO write-only blocks until somebody
        // is reading it, and the station at the far end may not have started yet.
        _fifo = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0, useAsync: false);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public void Write(ReadOnlySpan<float> samples)
    {
        for (int at = 0; at < samples.Length;)
        {
            int chunk = Math.Min(samples.Length - at, _bytes.Length / sizeof(float));
            for (int n = 0; n < chunk; n++)
            {
                BitConverter.TryWriteBytes(_bytes.AsSpan(n * sizeof(float)), samples[at + n]);
            }

            _fifo.Write(_bytes, 0, chunk * sizeof(float));
            at += chunk;
        }

        _fifo.Flush();
    }

    public void Drain() => _fifo.Flush();

    public void Dispose() => _fifo.Dispose();
}
