using System.Runtime.InteropServices;

namespace Packet.SoundModem.Audio;

/// <summary>
/// Thin ALSA PCM wrapper over <c>libasound.so.2</c> P/Invoke — capture and playback of
/// interleaved S16_LE. Linux-only by design (headless Pi is the primary deployment; a
/// cross-platform backend can join behind the same shape later). Uses
/// <c>snd_pcm_set_params</c> for configuration; finer period control via hw_params can
/// come with the Phase 2 latency work on real deployment hardware.
/// </summary>
public sealed class AlsaPcm : IDisposable
{
    /// <summary>PCM direction.</summary>
    public enum Direction
    {
        /// <summary>Audio out.</summary>
        Playback = 0,

        /// <summary>Audio in.</summary>
        Capture = 1,
    }

    private const string Lib = "libasound.so.2";
    private const int FormatS16Le = 2;
    private const int AccessRwInterleaved = 3;

    private IntPtr _pcm;

    private AlsaPcm(IntPtr pcm, Direction direction, int channels, int sampleRate)
    {
        _pcm = pcm;
        Dir = direction;
        Channels = channels;
        SampleRate = sampleRate;
    }

    /// <summary>The direction this PCM was opened for.</summary>
    public Direction Dir { get; }

    /// <summary>Interleaved channel count.</summary>
    public int Channels { get; }

    /// <summary>Configured sample rate (the device may resample via the plug layer).</summary>
    public int SampleRate { get; }

    /// <summary>Opens and configures a PCM device (e.g. "default", "plughw:0,0").</summary>
    /// <param name="device">ALSA device name.</param>
    /// <param name="direction">Capture or playback.</param>
    /// <param name="channels">Interleaved channels (1 or 2).</param>
    /// <param name="sampleRate">Requested rate; capture at the card-native 48000 and
    /// decimate in the DSP rather than letting the plug layer's linear resampler run.</param>
    /// <param name="latencyMicroseconds">Overall buffer target. Smaller = lower DCD/RX
    /// latency but more xrun risk.</param>
    /// <exception cref="InvalidOperationException">The device could not be opened or
    /// configured.</exception>
    public static AlsaPcm Open(
        string device, Direction direction, int channels, int sampleRate,
        int latencyMicroseconds = 120_000)
    {
        int err = snd_pcm_open(out IntPtr pcm, device, (int)direction, 0);
        Throw(err, $"snd_pcm_open({device})");

        err = snd_pcm_set_params(
            pcm, FormatS16Le, AccessRwInterleaved, (uint)channels, (uint)sampleRate,
            softResample: 1, latency: (uint)latencyMicroseconds);
        if (err < 0)
        {
            snd_pcm_close(pcm);
            Throw(err, $"snd_pcm_set_params({device}, {sampleRate} Hz, {channels} ch)");
        }

        return new AlsaPcm(pcm, direction, channels, sampleRate);
    }

    /// <summary>Reads interleaved frames (capture PCM). Blocks until the span is filled.
    /// Recovers from overruns transparently.</summary>
    /// <returns>Frames read (normally the full span).</returns>
    public int Read(Span<short> interleaved)
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        int frameCount = interleaved.Length / Channels;
        int total = 0;
        while (total < frameCount)
        {
            long got;
            unsafe
            {
                fixed (short* p = interleaved)
                {
                    got = snd_pcm_readi(_pcm, (IntPtr)(p + total * Channels), (ulong)(frameCount - total));
                }
            }

            if (got < 0)
            {
                int recovered = snd_pcm_recover(_pcm, (int)got, 1);
                Throw(recovered, "snd_pcm_readi");
                continue;
            }

            total += (int)got;
        }

        return total;
    }

    /// <summary>Writes interleaved frames (playback PCM). Blocks until consumed —
    /// device-paced, which is exactly what sample-accurate PTT timing needs.
    /// Recovers from underruns transparently.</summary>
    public void Write(ReadOnlySpan<short> interleaved)
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        int frameCount = interleaved.Length / Channels;
        int total = 0;
        while (total < frameCount)
        {
            long put;
            unsafe
            {
                fixed (short* p = interleaved)
                {
                    put = snd_pcm_writei(_pcm, (IntPtr)(p + total * Channels), (ulong)(frameCount - total));
                }
            }

            if (put < 0)
            {
                int recovered = snd_pcm_recover(_pcm, (int)put, 1);
                Throw(recovered, "snd_pcm_writei");
                continue;
            }

            total += (int)put;
        }
    }

    /// <summary>Blocks until everything written has actually played (playback only) —
    /// the sample-domain part of releasing PTT.</summary>
    public void Drain()
    {
        ObjectDisposedException.ThrowIf(_pcm == IntPtr.Zero, this);
        _ = snd_pcm_drain(_pcm);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_pcm != IntPtr.Zero)
        {
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }
    }

    private static void Throw(int err, string operation)
    {
        if (err < 0)
        {
            string message = Marshal.PtrToStringAnsi(snd_strerror(err)) ?? $"error {err}";
            throw new InvalidOperationException($"{operation}: {message}");
        }
    }

    [DllImport(Lib)]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport(Lib)]
    private static extern int snd_pcm_set_params(
        IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latency);

    [DllImport(Lib)]
    private static extern long snd_pcm_readi(IntPtr pcm, IntPtr buffer, ulong frames);

    [DllImport(Lib)]
    private static extern long snd_pcm_writei(IntPtr pcm, IntPtr buffer, ulong frames);

    [DllImport(Lib)]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

    [DllImport(Lib)]
    private static extern int snd_pcm_drain(IntPtr pcm);

    [DllImport(Lib)]
    private static extern int snd_pcm_close(IntPtr pcm);

    [DllImport(Lib)]
    private static extern IntPtr snd_strerror(int error);
}
