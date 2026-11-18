namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// What a receiver carries from one burst to the next inside a keyup, so that a follow-on frame
/// can be read without a sync symbol, a preamble or an estimate symbol of its own.
/// </summary>
/// <remarks>
/// <para>A full burst acquires everything from scratch: timing from its sync symbol, the
/// acquisition layout's channel from its preamble, its payload layout's channel from its estimate
/// symbol. Inside one keyup the channel does not change and the timing drifts by a hundredth of a
/// sample a symbol, so the second and later frames can spend none of that and carry only their
/// header and payload (see <see cref="OfdmFmParameters.FollowOnFrames"/>). This is the state that
/// makes that possible, and it is refreshed from every frame that decodes: the acquisition
/// layout's channel from each header once its CRC has made the header's bits known, and the
/// payload layout's channel from the last few payload symbols of each frame whose payload CRC
/// passed, every carrier of which is then a known reference.</para>
/// <para>Handed out by <see cref="OfdmFmBurstCodec.DecodeAt(ReadOnlySpan{float}, int, out OfdmFmKeyupState?)"/>
/// and consumed and updated by the follow-on calls. Opaque outside the codec: what it holds is a
/// channel estimate, which is the codec's business.</para>
/// </remarks>
public sealed class OfdmFmKeyupState
{
    internal OfdmFmKeyupState((double Re, double Im)[] acquisitionChannel, double acquisitionNoise)
    {
        AcquisitionChannel = acquisitionChannel;
        AcquisitionNoise = acquisitionNoise;
    }

    internal (double Re, double Im)[] AcquisitionChannel { get; set; }

    internal double AcquisitionNoise { get; }

    internal Dictionary<int, (double Re, double Im)[]> PayloadChannels { get; } = [];

    internal Dictionary<int, double> PayloadNoise { get; } = [];

    /// <summary>The table entry the last decoded burst's payload was on.</summary>
    public int Geometry { get; internal set; }

    /// <summary>The sample-clock rate the last clock-tracked read of this keyup ended on, in
    /// samples per symbol: where the next frame's read starts its prediction from. Zero until a
    /// tracked read has been taken.</summary>
    internal double ClockRate { get; set; }

    /// <summary>How many whole samples later (or, negative, sooner) than its announced length
    /// the last burst really ended, by the clock its tracked read measured: where the next
    /// frame's header is looked for first. The payload channel handed on is on the grid that
    /// starts there. Zero when the last burst was not read that way.</summary>
    internal int ClockShift { get; set; }

    /// <summary>How far, in samples, the grid has moved beyond what the signal itself moved
    /// since the keyup's last full burst: the sum of every <see cref="ClockShift"/> less the
    /// sum of the drift measured. Carried so that the shifts cannot add up to a creep of the
    /// read window through a long keyup of follow-on frames.</summary>
    internal double ClockLead { get; set; }

    /// <summary>
    /// Samples from a symbol boundary as the symbols' own cyclic prefixes place it to the position
    /// this keyup's estimates are aligned to: negative when the sync search committed early, which
    /// within a prefix it may. A burst read at its found position decodes because its channel
    /// estimate was made at the same position; a follow-on burst found by its prefixes has to be
    /// read this far from where they put it, or it is read against estimates made a few samples
    /// away, which the payload does not survive.
    /// </summary>
    internal int Alignment { get; set; }

    /// <summary>Whether a follow-on frame on this entry could be read: a payload layout's
    /// channel is known only once a burst on it has been decoded in this keyup.</summary>
    public bool CanRead(int geometry) => PayloadChannels.ContainsKey(geometry);
}
