using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio.Audio;

namespace Packet.SoundModem.Tests.Channel;

/// <summary>
/// A fake clock with a sound card on the end of it: the crank that moves the clock, and an output
/// that costs the clock what the audio costs the air.
/// </summary>
/// <remarks>
/// <para>The no-op sink the other channel tests use makes a keyup instantaneous, which is fine
/// while nothing under test cares how long a transmission takes. It stops being fine the moment
/// the question is where a frame's wait went, because most of a queued frame's wait can be this
/// station's own airtime and a free write has none.</para>
/// <para>Nothing here decides anything by the wall clock. The wall clock turns the crank, and
/// every figure any test reads comes off the fake one.</para>
/// </remarks>
internal static class VirtualAir
{
    /// <summary>How far the clock moves per turn of the crank.</summary>
    /// <remarks>
    /// One millisecond, which makes the fake clock run at roughly real speed, and that is the
    /// point. The crank turns on the thread pool, so on a loaded box the transmitter can be held
    /// off a thread while the crank keeps getting one, and every millisecond of that stall lands
    /// on the figures under test as if the channel had been waiting. At ten milliseconds a step
    /// this harness ran the clock about eight times real speed, which multiplied every scheduling
    /// hiccup by eight and made a 60 ms tolerance fail under a full test run while passing alone.
    /// </remarks>
    internal static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// What everything measured through this harness is quantised to: a CSMA slot, the crank's own
    /// step, and room for the thread pool to be slow about handing the transmitter a thread. The
    /// effects these tests are about are hundreds of milliseconds and seconds.
    /// </summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The one place the fake clock is moved, so that two crankers cannot lose an advance to each
    /// other - <see cref="FakeTimeProvider.Advance"/> reads the current time and sets it back, and
    /// interleaving that with another thread either drops a step or throws.
    /// </summary>
    /// <remarks>
    /// The lock is re-entrant on purpose, and it has to be. A fake clock runs its timer callbacks
    /// on whichever thread advanced it, so the transmitter's continuation after a CSMA delay can
    /// run INSIDE the pump's advance, and a paced write can then be cranking the clock from the
    /// pump's own thread. Anything that waited for another thread instead would deadlock there,
    /// which is what the first version of this harness did.
    /// </remarks>
    private static readonly object Crank = new();

    internal static void Tick(FakeTimeProvider time) => Tick(time, Step);

    internal static void Tick(FakeTimeProvider time, TimeSpan step)
    {
        lock (Crank)
        {
            time.Advance(step);
        }
    }

    /// <summary>Turns the crank until the fake clock reaches a point.</summary>
    internal static async Task AdvanceToAsync(FakeTimeProvider time, DateTimeOffset until)
    {
        while (time.GetUtcNow() < until)
        {
            await Task.Delay(1, CancellationToken.None);
        }
    }

    /// <summary>Runs the crank until cancelled, so that delays on the fake clock complete.</summary>
    internal static void Pump(FakeTimeProvider time, CancellationToken cancellation) =>
        _ = Task.Run(
            async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    Tick(time);
                    await Task.Delay(1, CancellationToken.None);
                }
            },
            CancellationToken.None);

    /// <summary>
    /// A sound card that costs the clock what the audio costs the air: the write does not return
    /// until the fake clock has moved on by the burst's own duration.
    /// </summary>
    internal sealed class PacedSink(int sampleRate, FakeTimeProvider time) : IAudioOutput
    {
        private readonly List<DateTimeOffset> _written = [];

        public int SampleRate { get; } = sampleRate;

        /// <summary>
        /// When each burst finished going to the device, on the fake clock and in order.
        /// </summary>
        /// <remarks>
        /// The instant a station stamps as a transmitted frame's <c>heard_at</c>: the frame log
        /// writes it from the handler for FrameTransmittedWithReport, which is raised once the
        /// enqueue task completes, and the enqueue task completes when this write returns. A test
        /// that stamps it in that handler instead is stamping when the thread pool got round to
        /// the continuation, which on a loaded box is a second or more later and is the pool's
        /// business rather than the channel's. Taken here it is the same instant, on the
        /// transmitter's own thread.
        /// </remarks>
        public IReadOnlyList<DateTimeOffset> Written
        {
            get
            {
                lock (_written)
                {
                    return [.. _written];
                }
            }
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            DateTimeOffset until = time.GetUtcNow()
                + TimeSpan.FromSeconds(samples.Length / (double)SampleRate);
            while (time.GetUtcNow() < until)
            {
                Tick(time);
            }

            lock (_written)
            {
                _written.Add(time.GetUtcNow());
            }
        }

        public void Drain()
        {
        }
    }
}
