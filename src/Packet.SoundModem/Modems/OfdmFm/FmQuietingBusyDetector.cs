namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// Carrier sense for an FM audio path, which works the opposite way round to carrier sense
/// everywhere else: a signal makes the receiver go QUIET, and this asserts busy on the quieting.
/// </summary>
/// <remarks>
/// <para><b>Why the ordinary detector cannot do this.</b> An energy detector asserts when audio
/// rises above an adapting noise floor, which is right for a modem whose signal ADDS power to what
/// the receiver was already hearing. An FM receiver with the squelch open - which is what a data
/// station runs - does the reverse. Open-squelch noise is loud; an arriving carrier captures the
/// discriminator and replaces that noise with the modulation. The level FALLS. An adapting floor
/// makes it worse rather than better, because the band noise becomes the floor and every real
/// signal sits below it, so the detector can never fire.</para>
/// <para><b>Measured on this bench, 2026-09-18</b>, from a raw capture spanning a real keyup at the
/// far end, 5 ms blocks:</para>
/// <list type="table">
/// <item><term>idle, squelch open</term><description>-15.9 dBFS</description></item>
/// <item><term>far end modulating</term><description>-25 dBFS, so 9 dB down</description></item>
/// <item><term>far end's carrier, no modulation</term><description>-55 dBFS, so 39 dB down</description></item>
/// <item><term>our own transmission (receive gated)</term><description>below -80 dBFS</description></item>
/// </list>
/// <para><b>The unmodulated carrier is the case that matters</b>, and it is the easiest of the
/// three to see. TXDELAY is silence in front of the preamble, so for its whole length a
/// transmitting station occupies the channel while producing nothing for a sync correlator to find.
/// That is why two stations collided head-on on this bench at every turnaround: at the shipped
/// 300 ms TXDELAY there is a 300 ms window in which the channel is busy and reads clear. A carrier
/// 39 dB below the noise it replaced closes that window, because the quieting starts with the
/// carrier and not with the modulation.</para>
/// <para><b>Three guards, because the failure mode is a station that will not transmit.</b>
/// Asserting busy wrongly and never releasing is worse than not detecting at all, so: the floor
/// must be established before anything is asserted; the detector disengages entirely if the floor
/// is too quiet to be open-squelch noise, which is what a SQUELCHED receiver looks like and where
/// quieting means nothing; and audio far below any real signal is treated as a dead or gated input
/// rather than as a carrier. Each of those turns a wedged transmitter into a detector that merely
/// does nothing.</para>
/// </remarks>
public sealed class FmQuietingBusyDetector
{
    /// <summary>How far below the noise floor counts as a carrier.</summary>
    /// <remarks>The nearest real case is a modulating far end at 9 dB down, and the noise floor
    /// itself moves by well under a decibel block to block, so 6 dB sits clear of the noise and
    /// well inside the shallowest signal.</remarks>
    public const double AssertBelowFloorDb = 6.0;

    /// <summary>How far the level has to come back before busy is released.</summary>
    public const double ReleaseBelowFloorDb = 3.0;

    /// <summary>
    /// A floor quieter than this is not open-squelch noise, so the detector stands down.
    /// </summary>
    /// <remarks>A squelched receiver, a muted card or a dead input all present as a quiet floor,
    /// and on all three "the level is below the floor" means nothing. Standing down leaves the
    /// sync-based detect in charge, which is where this started.</remarks>
    public const double MinimumFloorDbfs = -45.0;

    /// <summary>Audio below this is a gated or dead input, not a carrier.</summary>
    /// <remarks>Receive is gated while the station transmits, and gated audio is digital silence
    /// at about -86 dBFS. A real quieted carrier measured -55. The gap is wide and this sits in
    /// it.</remarks>
    public const double SilenceDbfs = -72.0;

    private const double BlockSeconds = 0.005;

    /// <summary>Blocks of audio before the floor is trusted enough to assert on.</summary>
    private const int WarmUpBlocks = 200;   // one second

    /// <summary>
    /// How long busy may persist before the detector decides it has the wrong floor and starts
    /// again.
    /// </summary>
    /// <remarks>
    /// <para><b>The backstop against the one failure that matters.</b> The floor is frozen while
    /// busy, and busy is decided against the floor, so anything that lowers the receiver's own
    /// noise and KEEPS it low latches the detector on: a squelch closing, an operator turning the
    /// volume down, an AGC settling, a band that goes quiet. None of those is a carrier, all of
    /// them are "below the floor", and a station in that state stops transmitting until somebody
    /// notices. Which is a worse outcome than never detecting anything at all.</para>
    /// <para>Twenty seconds is far longer than any burst this modem sends - the longest measured
    /// on this bench is about four - and short enough that a latched detector heals itself while
    /// an operator is still looking at it. On expiry the floor is discarded rather than nudged,
    /// because the whole premise of the estimate has been shown wrong and relearning from the
    /// audio that is actually arriving is the only honest recovery.</para>
    /// </remarks>
    private const double MaxBusySeconds = 20.0;

    private readonly int _blockSamples;
    private readonly double _floorDecayPerBlock;
    private readonly int _holdBlocks;
    private readonly int _maxBusyBlocks;

    private double _sumSquares;
    private int _inBlock;
    private int _blocksSeen;
    private double _floorDb = double.NegativeInfinity;
    private int _hold;
    private int _busyBlocks;

    /// <param name="sampleRate">The rate audio arrives at.</param>
    /// <param name="holdMilliseconds">Minimum busy time after the level comes back up. Short,
    /// because the two quiet states this distinguishes are 30 dB apart and neither flickers.
    /// </param>
    public FmQuietingBusyDetector(int sampleRate, int holdMilliseconds = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _blockSamples = Math.Max(1, (int)Math.Round(sampleRate * BlockSeconds));
        // The floor only decays while the channel is idle (see Process), so this sets how fast a
        // genuinely falling noise floor is followed, not how long a transmission may last.
        _floorDecayPerBlock = 0.3 * BlockSeconds;
        _holdBlocks = Math.Max(1, (int)Math.Ceiling(holdMilliseconds / 1000.0 / BlockSeconds));
        _maxBusyBlocks = (int)Math.Ceiling(MaxBusySeconds / BlockSeconds);
    }

    /// <summary>True while the far end appears to be holding the channel.</summary>
    public bool Busy { get; private set; }

    /// <summary>The noise floor as currently estimated, for logging and for tests.</summary>
    public double FloorDbfs => _floorDb;

    /// <summary>Whether the detector considers itself able to judge at all.</summary>
    public bool Engaged => _blocksSeen >= WarmUpBlocks && _floorDb >= MinimumFloorDbfs;

    /// <summary>Consumes one sample.</summary>
    public void Process(float sample)
    {
        _sumSquares += (double)sample * sample;
        if (++_inBlock < _blockSamples)
        {
            return;
        }

        double rms = Math.Sqrt(_sumSquares / _blockSamples);
        _sumSquares = 0;
        _inBlock = 0;
        _blocksSeen = Math.Min(_blocksSeen + 1, int.MaxValue - 1);

        double db = 20 * Math.Log10(Math.Max(rms, 1e-12));

        // Before anything else: a busy that has outlasted any burst this modem can send is not a
        // burst, it is a floor that has stopped describing the channel. Throw it away and relearn.
        if (Busy && ++_busyBlocks > _maxBusyBlocks)
        {
            Busy = false;
            _hold = 0;
            _busyBlocks = 0;
            _floorDb = double.NegativeInfinity;
            _blocksSeen = 0;
        }

        // A dead or gated input is not a carrier. Nothing is asserted on it and the floor is left
        // alone, so a spell of silence cannot drag the floor down and make the noise that follows
        // it look like a signal.
        if (db < SilenceDbfs)
        {
            ReleaseAfterHold();
            return;
        }

        if (double.IsNegativeInfinity(_floorDb))
        {
            _floorDb = db;
        }
        else if (db > _floorDb)
        {
            // Rising to the floor quickly: the floor IS the loud state here, so anything louder
            // than the current estimate is better evidence of it than the estimate was.
            _floorDb += 0.25 * (db - _floorDb);
        }
        else if (!Busy)
        {
            // Only while idle. Letting the floor sag during a transmission would walk it down to
            // meet the signal and release busy part way through a long burst.
            _floorDb -= _floorDecayPerBlock;
        }

        if (!Engaged)
        {
            ReleaseAfterHold();
            return;
        }

        if (db < _floorDb - AssertBelowFloorDb)
        {
            if (!Busy)
            {
                _busyBlocks = 0;
            }

            Busy = true;
            _hold = _holdBlocks;
        }
        else if (db > _floorDb - ReleaseBelowFloorDb)
        {
            ReleaseAfterHold();
        }
    }

    private void ReleaseAfterHold()
    {
        if (!Busy)
        {
            return;
        }

        if (--_hold <= 0)
        {
            Busy = false;
            _busyBlocks = 0;
        }
    }
}
