namespace Packet.SoundModem.Modems.OfdmFmStation;

/// <summary>Where the book's calls end up: the two calls that advance the station's clock.</summary>
internal interface IStationClockInput
{
    /// <summary>Audio the radio heard.</summary>
    void Push(ReadOnlySpan<float> samples);

    /// <summary>A stretch the radio did not hear (it was keyed), counted as silence.</summary>
    void Skip(int count);
}

/// <summary>
/// The bookkeeping that turns "a block of n samples, keyed or not" into the station's clock calls. The station's only clock is the count
/// of samples it has been given, so every block, heard or keyed, must reach it exactly once and in order: an unkeyed block is a
/// <c>Push</c> of its samples, a keyed block of n samples is a <c>Skip(n)</c>.
/// <para>
/// No thread of its own and no lock: the caller serialises every call (the host does it under its one lock). In this slice it has only the
/// receive half; the transmit half (the receive-sample index a keyup started and ended at, which is <see cref="SampleCount"/> at that moment) is added with transmitting.
/// </para>
/// </summary>
internal sealed class TransmitBook(IStationClockInput station)
{
    private readonly IStationClockInput _station = station ?? throw new ArgumentNullException(nameof(station));

    /// <summary>The samples given to the station so far, heard and skipped together. Equals the station's own <c>SampleCount</c> while this is the only thing feeding it.</summary>
    public long SampleCount { get; private set; }

    /// <summary>An unkeyed block: pushed whole. An empty block is no sample and no call.</summary>
    public void Heard(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
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

        _station.Skip(count);
        SampleCount += count;
    }
}
