using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using M0LTE.Radio.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Packet.SoundModem.Windows;

/// <summary>
/// A WASAPI capture endpoint as an <see cref="IAudioInput"/>: mono float samples at a fixed rate,
/// read with the blocking contract the soundmodem's receive pump expects.
/// </summary>
/// <remarks>
/// <para><b>Shared mode, with Windows doing the conversion.</b> The stream is opened as mono
/// IEEE float at <see cref="SampleRate"/> with AUTOCONVERTPCM and SRC_DEFAULT_QUALITY, so the
/// audio engine converts from whatever the endpoint's mix format is (commonly 48 kHz stereo).
/// On a 48 kHz device asked for 48 kHz that is only a channel downmix. Exclusive mode would
/// avoid even that, but it takes the device away from every other application on the machine,
/// which is not a thing a station should do by default.</para>
/// <para><b>All COM lives on one thread.</b> The endpoint is opened, started and read on a
/// dedicated MTA thread that runs for the life of this object; callers only ever touch the ring.
/// That keeps WASAPI off a WPF UI thread (an STA) entirely.</para>
/// <para><b>A slow reader loses the oldest audio, never the newest.</b> The ring holds
/// <c>bufferLength</c> of audio; if the reader falls further behind than that the oldest samples
/// are dropped and <see cref="Overruns"/> counts them. A capture thread that blocked instead
/// would make WASAPI drop audio on its own, silently, which is worse.</para>
/// <para><b>Device loss ends the stream.</b> When the endpoint goes away (unplugged, disabled,
/// format changed under it) the capture thread records <see cref="Fault"/>, raises
/// <see cref="Faulted"/>, and <see cref="Read"/> returns 0 from then on, which is what the
/// interface defines as a source that is closing.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInput : IAudioInput, IDisposable
{
    /// <summary>The rate streams are opened at when nobody says: what USB codecs run natively.</summary>
    public const int DefaultSampleRate = 48000;

    private readonly object _gate = new();
    private readonly SampleRing _ring;
    private readonly Thread _thread;
    private readonly string? _endpointId;
    private volatile bool _stopping;
    private Exception? _fault;
    private long _overruns;

    /// <summary>Opens and starts capture. Throws if the endpoint cannot be opened.</summary>
    /// <param name="endpointId">The endpoint's ID (see <see cref="AudioEndpoints"/>), or null for
    /// the default communications capture device.</param>
    /// <param name="sampleRate">The rate samples are delivered at.</param>
    /// <param name="bufferLength">How much audio is held for a reader that falls behind.
    /// Default 1 s.</param>
    public WasapiAudioInput(string? endpointId = null, int sampleRate = DefaultSampleRate, TimeSpan? bufferLength = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _endpointId = endpointId;
        SampleRate = sampleRate;
        TimeSpan length = bufferLength ?? TimeSpan.FromSeconds(1);
        _ring = new SampleRing(Math.Max(1, (int)(sampleRate * length.TotalSeconds)));

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(opened)) { IsBackground = true, Name = "wasapi-capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try
        {
            opened.Task.GetAwaiter().GetResult();
        }
        catch
        {
            _stopping = true;
            _thread.Join(TimeSpan.FromSeconds(2));
            throw;
        }
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <summary>Samples dropped because the reader fell more than the buffer length behind.</summary>
    public long Overruns => Interlocked.Read(ref _overruns);

    /// <summary>Why the stream stopped, once it has; null while it is running.</summary>
    public Exception? Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    /// <summary>Raised once, on the capture thread, if the device fails or goes away.</summary>
    public event Action<Exception>? Faulted;

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            while (_ring.Count == 0)
            {
                if (_stopping || _fault is not null)
                {
                    return 0;
                }

                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(200));
            }

            return _ring.Read(destination);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run(TaskCompletionSource opened)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = _endpointId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(_endpointId);
            using AudioClient client = device.AudioClient;
            using var wake = new AutoResetEvent(false);

            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                bufferDuration: TimeSpan.FromMilliseconds(100).Ticks,
                periodicity: 0,
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1),
                Guid.Empty);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
            AudioCaptureClient capture = client.AudioCaptureClient;
            var scratch = new float[client.BufferSize];
            client.Start();
            opened.SetResult();

            try
            {
                while (!_stopping)
                {
                    wake.WaitOne(200);
                    while (!_stopping && capture.GetNextPacketSize() > 0)
                    {
                        IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                        if (scratch.Length < frames)
                        {
                            scratch = new float[frames];
                        }

                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            Array.Clear(scratch, 0, frames);
                        }
                        else
                        {
                            Marshal.Copy(data, scratch, 0, frames);
                        }

                        capture.ReleaseBuffer(frames);
                        Deliver(scratch.AsSpan(0, frames));
                    }
                }
            }
            finally
            {
                client.Stop();
            }
        }
        catch (Exception ex) when (!opened.Task.IsCompleted)
        {
            opened.SetException(ex);
        }
        catch (Exception ex) when (!_stopping)
        {
            lock (_gate)
            {
                _fault = ex;
                Monitor.PulseAll(_gate);
            }

            Faulted?.Invoke(ex);
        }
        catch (Exception) when (_stopping)
        {
            // A device error racing a dispose is the dispose.
        }
    }

    private void Deliver(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length > _ring.Capacity)
            {
                samples = samples[^_ring.Capacity..];
            }

            int excess = samples.Length - _ring.Free;
            if (excess > 0)
            {
                Interlocked.Add(ref _overruns, _ring.Discard(excess));
            }

            _ring.Write(samples);
            Monitor.PulseAll(_gate);
        }
    }
}
