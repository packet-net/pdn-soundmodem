using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Gathers about <see cref="TargetSeconds"/> of the station's own received audio, clear of its
/// transmitter and of a mailcast slot, and hands it to <see cref="MailcastFilterAnalysis"/> for
/// "Measure my filter". One measurement at a time.
/// </summary>
/// <remarks>
/// A receive tap, the same way <see cref="MailcastReceiver"/> itself is one: it never transmits
/// and never touches a KISS port, it just looks at audio that is already flowing. Blocks heard
/// while <paramref name="skip"/> (given at construction) says to - the channel is keyed, or a
/// mailcast slot is in progress - are dropped rather than averaged in, because either would shape
/// the spectrum into something that is not the rig's receive filter.
/// </remarks>
internal sealed class MailcastFilterMeasurer
{
    /// <summary>How much of the station's own clear audio one measurement averages.</summary>
    internal const double TargetSeconds = 10.0;

    /// <summary>How long a measurement waits for that much clear audio before giving up: long
    /// enough to ride out one mailcast slot and a modest transmission, not so long a stuck
    /// request never lets go.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private const int LinesPerSecond = 10;

    private readonly Func<bool> _skip;
    private readonly WaterfallSource _spectrum;
    private readonly double[] _sum;
    private readonly int _linesWanted;
    private readonly object _gate = new();
    private TaskCompletionSource<MailcastFilterMeasurement>? _pending;
    private int _linesTaken;

    /// <param name="dspRate">The channel's DSP rate, the same the mailcast modem runs at.</param>
    /// <param name="skip">True for a block of audio that must not be measured: the channel is
    /// transmitting, or a mailcast slot (retuned or on the station's own passband) is open.</param>
    internal MailcastFilterMeasurer(int dspRate, Func<bool> skip)
    {
        _skip = skip;
        _spectrum = new WaterfallSource(dspRate, OnLine, LinesPerSecond);
        _sum = new double[_spectrum.LineLength];
        _linesWanted = (int)Math.Round(TargetSeconds * LinesPerSecond);
    }

    /// <summary>Puts the measurer on <paramref name="channel"/>'s receive audio, beside whatever
    /// else taps it.</summary>
    internal void Attach(SoundModemChannel channel) => channel.AddReceiveTap(Process);

    /// <summary>One block of the channel's audio, on the receive thread. Ignored unless a
    /// measurement is running, and dropped rather than averaged in while <c>skip</c> says so.</summary>
    internal void Process(ReadOnlySpan<float> samples)
    {
        if (_pending is null || _skip())
        {
            return;
        }

        _spectrum.Process(samples);
    }

    /// <summary>
    /// Runs one measurement: clears any audio left over from before, waits for
    /// <see cref="TargetSeconds"/> of clear audio, and returns what it found. Throws
    /// <see cref="InvalidOperationException"/> if one is already running, or
    /// <see cref="TimeoutException"/> if <see cref="Timeout"/> runs out first (the channel never
    /// went quiet of both its own transmitter and a mailcast slot).
    /// </summary>
    internal async Task<MailcastFilterMeasurement> MeasureAsync(CancellationToken cancellation)
    {
        var tcs = new TaskCompletionSource<MailcastFilterMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("a filter measurement is already running on this station");
            }

            Array.Clear(_sum);
            _linesTaken = 0;
            _pending = tcs;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(Timeout);
        using CancellationTokenRegistration registration = cts.Token.Register(() =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, tcs))
                {
                    _pending = null;
                }
            }

            if (cancellation.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellation);
            }
            else
            {
                tcs.TrySetException(new TimeoutException(
                    $"no {TargetSeconds:F0} s of clear audio in {Timeout.TotalSeconds:F0} s: the channel kept "
                    + "transmitting, or stayed inside a mailcast slot. Try again when the band is quiet."));
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private void OnLine(long index, ReadOnlyMemory<byte> line)
    {
        TaskCompletionSource<MailcastFilterMeasurement>? completed = null;
        MailcastFilterMeasurement? result = null;
        lock (_gate)
        {
            if (_pending is null)
            {
                return;
            }

            ReadOnlySpan<byte> bytes = line.Span;
            for (int bin = 0; bin < bytes.Length; bin++)
            {
                _sum[bin] += WaterfallSource.ByteToLinearPower[bytes[bin]];
            }

            if (++_linesTaken < _linesWanted)
            {
                return;
            }

            var averaged = new double[_sum.Length];
            for (int bin = 0; bin < _sum.Length; bin++)
            {
                averaged[bin] = _sum[bin] / _linesTaken;
            }

            result = MailcastFilterAnalysis.Analyze(averaged, _spectrum.BinWidthHz);
            completed = _pending;
            _pending = null;
        }

        completed.TrySetResult(result!);
    }
}
