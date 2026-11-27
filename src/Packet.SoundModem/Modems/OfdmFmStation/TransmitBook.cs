namespace Packet.SoundModem.Modems.OfdmFmStation;

/// <summary>Where the book's calls end up: the calls that advance the station's clock, and the two that report a keyup.</summary>
internal interface IStationClockInput
{
    /// <summary>Audio the radio heard.</summary>
    void Push(ReadOnlySpan<float> samples);

    /// <summary>A stretch the radio did not hear (it was keyed), counted as silence.</summary>
    void Skip(int count);

    /// <summary>Request <paramref name="id"/> went on air at receive-sample <paramref name="sampleIndex"/>.</summary>
    void TransmitStarted(long id, long sampleIndex);

    /// <summary>Request <paramref name="id"/> came off the air at receive-sample <paramref name="sampleIndex"/>.</summary>
    void TransmitEnded(long id, long sampleIndex);
}

/// <summary>
/// The bookkeeping between the receive gate and the station. The station's only clock is the count of samples it has been given, so every block, heard or keyed,
/// must reach it exactly once and in order: an unkeyed block is a <c>Push</c> of its samples, a keyed block of n samples is a <c>Skip(n)</c>.
/// The transmit half works out the receive-sample index a keyup started and ended at from the same blocks: the run of keyed blocks IS the keyup.
/// <para>
/// No thread of its own and no lock: the caller serialises every call (the host does it under its one lock), and everything that happens on another thread
/// (the transmitter saying a request is on air or finished) is handed in by the caller on the receive thread, between blocks.
/// </para>
/// <para>
/// <b>One request at a time.</b> The station hands over one request and asks for the next only when the last is over. The book keeps the one outstanding request
/// (<see cref="Requested"/>, then <see cref="TakeDue"/> once the count has reached its earliest sample) and the one on air (<see cref="OnAir"/>).
/// </para>
/// <para>
/// <b>The indices.</b> A run starts at its first keyed block and ends at the first unkeyed block after it. If a request is on air the start is reported at the run's first block
/// (or, if the on-air mark comes later, at once, with the run's first block as the index) and the end at the first unkeyed block, always before that block is pushed.
/// An index is never ahead of <see cref="SampleCount"/>, and never before the request's earliest sample (a keyup that was already running when the request was handed over
/// is clamped to it). A keyup that left no keyed block behind it (<see cref="Finished"/> with the run never seen) is reported as starting and ending at the count.
/// A keyed run with no request on air is somebody else's (the Morse ident, a keyup that failed): only <c>Skip</c>.
/// </para>
/// </summary>
internal sealed class TransmitBook(IStationClockInput station)
{
    private readonly IStationClockInput _station = station ?? throw new ArgumentNullException(nameof(station));

    private long? _pendingId;
    private long _pendingEarliest;
    private bool _pendingHandedOver;

    private long? _onAirId;
    private long _onAirEarliest;
    private bool _startReported;

    private bool _inRun;
    private long _runStart;

    /// <summary>The samples given to the station so far, heard and skipped together. Equals the station's own <c>SampleCount</c> while this is the only thing feeding it.</summary>
    public long SampleCount { get; private set; }

    /// <summary>True from the first keyed block of a run until the first unkeyed block after it.</summary>
    public bool InRun => _inRun;

    /// <summary>The request on air, or null.</summary>
    public long? OnAirId => _onAirId;

    /// <summary>An unkeyed block: pushed whole, after the end of a keyed run before it is reported. An empty block is no sample and no call.</summary>
    public void Heard(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        if (_inRun)
        {
            EndRun();
        }

        _station.Push(samples);
        SampleCount += samples.Length;
    }

    /// <summary>A keyed block of <paramref name="count"/> samples: skipped. Zero is no sample and no call.</summary>
    public void Keyed(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }

        if (!_inRun)
        {
            _inRun = true;
            _runStart = SampleCount;
            if (_onAirId is { } id && !_startReported)
            {
                ReportStart(id);
            }
        }

        _station.Skip(count);
        SampleCount += count;
    }

    /// <summary>The station asked for a transmission. It replaces any request not yet on air.</summary>
    public void Requested(long id, long earliestSample)
    {
        _pendingId = id;
        _pendingEarliest = earliestSample;
        _pendingHandedOver = false;
    }

    /// <summary>
    /// The outstanding request, once the count has reached its earliest sample and it has not been handed over: true once for each request.
    /// </summary>
    public bool TakeDue(out long id)
    {
        if (_pendingId is { } pending && !_pendingHandedOver && SampleCount >= _pendingEarliest)
        {
            _pendingHandedOver = true;
            id = pending;
            return true;
        }

        id = 0;
        return false;
    }

    /// <summary>The station withdrew a request. One not handed over is forgotten. One that has been is the caller's to cancel; if the transmitter has it already it goes on air and <see cref="OnAir"/> still follows.</summary>
    public void Withdrawn(long id)
    {
        if (_pendingId == id)
        {
            _pendingId = null;
        }
    }

    /// <summary>
    /// The channel will not transmit request <paramref name="id"/>. True: the station is to be told. False: it is on air, so there is nothing to refuse. The request is forgotten.
    /// </summary>
    public bool Refused(long id)
    {
        if (_onAirId == id)
        {
            return false;
        }

        if (_pendingId == id)
        {
            _pendingId = null;
        }

        return true;
    }

    /// <summary>
    /// The transmitter took request <paramref name="id"/> and is keying it: its keyup is a run of keyed blocks, the one in progress or the next. A second mark while one is on air is ignored (the second request
    /// rides in the first's keyup and the station was told to withdraw it).
    /// </summary>
    /// <param name="id">The request.</param>
    /// <param name="earliestSample">Its earliest sample, so that a start index is never before it.</param>
    public void OnAir(long id, long earliestSample)
    {
        if (_onAirId is not null)
        {
            return;
        }

        if (_pendingId == id)
        {
            _pendingId = null;
        }

        _onAirId = id;
        _onAirEarliest = earliestSample;
        _startReported = false;
        if (_inRun)
        {
            ReportStart(id);
        }
    }

    /// <summary>
    /// The transmitter has finished with request <paramref name="id"/> (its task completed, whichever way). If it was on air and no keyed run was ever seen for it, the keyup left no block behind
    /// and is reported as starting and ending now. Otherwise nothing: the run ends at its first unkeyed block.
    /// </summary>
    public void Finished(long id)
    {
        if (_onAirId == id && !_inRun && !_startReported)
        {
            _station.TransmitStarted(id, SampleCount);
            _station.TransmitEnded(id, SampleCount);
            _onAirId = null;
        }
    }

    private void ReportStart(long id)
    {
        _station.TransmitStarted(id, Math.Max(_runStart, _onAirEarliest));
        _startReported = true;
    }

    private void EndRun()
    {
        _inRun = false;
        if (_onAirId is { } id)
        {
            if (!_startReported)
            {
                ReportStart(id);
            }

            _station.TransmitEnded(id, SampleCount);
            _onAirId = null;
            _startReported = false;
        }
    }
}
