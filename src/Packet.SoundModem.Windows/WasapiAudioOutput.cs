using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using M0LTE.Radio.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Packet.SoundModem.Windows;

/// <summary>
/// A WASAPI render endpoint as an <see cref="IAudioOutput"/>: blocking, device-paced writes of
/// mono float samples, and a <see cref="Drain"/> that returns only once the last sample written
/// has left the device, which is what the transmitter unkeys on.
/// </summary>
/// <remarks>
/// <para><b>The stream runs all the time, playing silence between transmissions.</b> The channel
/// writes only while it is keyed, and a WASAPI stream that was stopped would need starting (and
/// its buffer priming) at every keyup; a running one has a fixed, small delay instead, which the
/// preamble absorbs. The same shared-mode, Windows-converts arrangement as
/// <see cref="WasapiAudioInput"/>.</para>
/// <para><b>How drain knows the audio has gone.</b> The render thread counts every frame it hands
/// the endpoint, silence included, and remembers the count at the end of the last real sample.
/// Frames the endpoint has played are that total less its current padding. Drain waits until
/// the queue is empty and the played count has passed the last real sample. That is sample
/// accurate as far as the audio engine knows; what the USB hardware adds after that is a few
/// milliseconds, well inside any sensible TXTAIL.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioOutput : IAudioOutput, IDisposable
{
    private readonly object _gate = new();
    private readonly SampleRing _ring;
    private readonly Thread _thread;
    private readonly string? _endpointId;
    private volatile bool _stopping;
    private Exception? _fault;

    // Render-thread accounting, read under _gate: frames handed to the endpoint (silence
    // included), the frame count at the end of the last real sample, and frames played.
    private long _submitted;
    private long _lastRealEnd;
    private long _played;

    /// <summary>Opens and starts the render stream (playing silence). Throws if the endpoint
    /// cannot be opened.</summary>
    /// <param name="endpointId">The endpoint's ID (see <see cref="AudioEndpoints"/>), or null for
    /// the default communications render device.</param>
    /// <param name="sampleRate">The rate samples are written at.</param>
    /// <param name="queueLength">How much audio a writer can get ahead of the device before
    /// <see cref="Write"/> blocks. Default 500 ms.</param>
    public WasapiAudioOutput(string? endpointId = null, int sampleRate = WasapiAudioInput.DefaultSampleRate, TimeSpan? queueLength = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _endpointId = endpointId;
        SampleRate = sampleRate;
        TimeSpan length = queueLength ?? TimeSpan.FromMilliseconds(500);
        _ring = new SampleRing(Math.Max(1, (int)(sampleRate * length.TotalSeconds)));

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(opened)) { IsBackground = true, Name = "wasapi-render" };
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

    /// <summary>Raised once, on the render thread, if the device fails or goes away.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Raised on the render thread with each block of real (non-silence) audio as it is
    /// handed to the endpoint - a transmit monitor's tap. Keep handlers cheap.</summary>
    public event Action<ReadOnlyMemory<float>>? Rendered;

    /// <inheritdoc />
    /// <exception cref="IOException">The device has failed or gone away.</exception>
    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            while (!samples.IsEmpty)
            {
                ThrowIfFaulted();
                int n = _ring.Write(samples);
                samples = samples[n..];
                if (!samples.IsEmpty)
                {
                    Monitor.Wait(_gate, TimeSpan.FromMilliseconds(200));
                }
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="IOException">The device has failed or gone away.</exception>
    public void Drain()
    {
        lock (_gate)
        {
            while (_ring.Count > 0 || _played < _lastRealEnd)
            {
                ThrowIfFaulted();
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(200));
            }
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

    private void ThrowIfFaulted()
    {
        if (_fault is not null)
        {
            throw new IOException("audio output device failed: " + _fault.Message, _fault);
        }

        ObjectDisposedException.ThrowIf(_stopping, this);
    }

    private void Run(TaskCompletionSource opened)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = _endpointId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications)
                : enumerator.GetDevice(_endpointId);
            using AudioClient client = device.AudioClient;
            using var wake = new AutoResetEvent(false);

            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                bufferDuration: TimeSpan.FromMilliseconds(60).Ticks,
                periodicity: 0,
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1),
                Guid.Empty);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
            AudioRenderClient render = client.AudioRenderClient;
            int bufferFrames = client.BufferSize;
            var scratch = new float[bufferFrames];

            // Prime with silence so the first period has something to play.
            render.GetBuffer(bufferFrames);
            render.ReleaseBuffer(bufferFrames, AudioClientBufferFlags.Silent);
            _submitted = bufferFrames;
            client.Start();
            opened.SetResult();

            try
            {
                while (!_stopping)
                {
                    wake.WaitOne(200);
                    int padding = client.CurrentPadding;
                    int free = bufferFrames - padding;
                    if (free <= 0)
                    {
                        continue;
                    }

                    int real;
                    lock (_gate)
                    {
                        _played = _submitted - padding;
                        real = _ring.Read(scratch.AsSpan(0, free));
                        if (real > 0)
                        {
                            _lastRealEnd = _submitted + real;
                        }

                        _submitted += free;
                        Monitor.PulseAll(_gate);
                    }

                    IntPtr buffer = render.GetBuffer(free);
                    if (real == 0)
                    {
                        render.ReleaseBuffer(free, AudioClientBufferFlags.Silent);
                        continue;
                    }

                    Array.Clear(scratch, real, free - real);
                    Marshal.Copy(scratch, 0, buffer, free);
                    render.ReleaseBuffer(free, AudioClientBufferFlags.None);
                    Rendered?.Invoke(scratch.AsMemory(0, real));
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
}
