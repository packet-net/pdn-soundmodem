using M0LTE.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Is the ARDOP slot in use by somebody else? Band-limits the channel's receive audio to the
/// ARDOP modem's own slot and meters it with the same <see cref="EnergyBusyDetector"/> the
/// packet modems use, so the TNC can be told the channel is busy through
/// <c>ArdopArqConfig.ChannelBusy</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> M0LTE.Ardop implements no busy detector: it has no audio device
/// and no spectrum, so it takes a seam and leaves the decision to whoever owns the audio. That is
/// this daemon. Until this, nothing here answered, the seam stayed unwired, and a station
/// transmitted regardless of what else was on the slot. ARDOP bursts also bypass the channel's
/// own p-persistence deliberately (PR #171: at a shifted centre a packet modem's detector asserts
/// on our own ARDOP signal, so the station waits for itself), which leaves the operator as the
/// only channel-access mechanism there is.</para>
/// <para><b>Why per-slot and not the channel's aggregate.</b>
/// <see cref="Channel.SoundModemChannel.ChannelBusy"/> is the OR across every modem plus our own
/// transmit, so feeding ARDOP that would defer to packet traffic in slots ARDOP does not occupy.
/// The question ARDOP has to answer is narrower and it is the one the band plan coordinates: is
/// <em>this</em> slot in use. Measured on the GB7RDG plan (2026-09-12), the separation is ample:
/// afsk300-il2pc at 850 Hz puts -84 dB into the 500-class ARDOP band and bpsk300 at 2150 Hz puts
/// -50 dB, so they would need 86 dB and 56 dB of SNR respectively to raise it by the 6 dB the
/// detector needs, against 6 dB for ARDOP itself.</para>
/// <para><b>Why the band is wider than ARDOP emits.</b> ardopcf watches 1160 to 1840 Hz at the
/// 500 class (BusyDetect.c), padding its window by the 100 Hz TuningRange each side, because a
/// busy detector does not choose its stations: a caller up to a tuning range off frequency is
/// still one we will work, so a detector that only watched our own emission would transmit over
/// them. Same reasoning, same padding.</para>
/// <para><b>Transmit is not our problem here.</b> The channel gates its receive tap while
/// transmitting, so no samples arrive during our own bursts and the detector cannot hear
/// itself.</para>
/// </remarks>
internal sealed class ArdopBusyDetector
{
    /// <summary>
    /// ardopcf's capture range either side of the nominal centre (<c>TuningRange</c>, 100 Hz),
    /// and so how far off frequency a station we would still work can sit.
    /// </summary>
    internal const double TuningRangeHz = 100.0;

    /// <summary>
    /// Half the audio width each ARDOP bandwidth class actually emits, measured at 99 % occupied
    /// bandwidth over the widest frame each class can send (2026-09-12, see
    /// <c>/home/tf/ardop-busy-detector-measurement.md</c>).
    /// </summary>
    /// <remarks>
    /// Measured rather than taken from the class name, because the two disagree: a frame's width
    /// is a property of the frame and not of the session, so every ConReq measures 190 to 199 Hz
    /// whether it announces 200 or 2000. Every class is narrower than its label except 200.
    /// </remarks>
    private static double EmittedHalfWidthHz(double bandwidthHz) => bandwidthHz switch
    {
        <= 200 => 102.5,    // measured 1397.5-1602.5 Hz about 1500
        <= 500 => 189.0,    // measured 1309.6-1687.5
        <= 1000 => 391.0,   // measured 1110.4-1892.6
        _ => 930.0,         // measured  577.1-2437.5
    };

    /// <summary>
    /// Taps for the detection bandpass. The receive bandpass in
    /// <see cref="ArdopChannelBridge"/> uses 639 for a filter the demodulator reads through; this
    /// one only has to meter power, so a shorter filter is enough and costs less per sample.
    /// </summary>
    private const int Taps = 257;

    private readonly FirFilter _bandpass;
    private readonly EnergyBusyDetector _energy;

    /// <summary>The low and high edges of the band being watched, for the start-up line.</summary>
    internal double LowHz { get; }

    /// <summary>The high edge of the band being watched.</summary>
    internal double HighHz { get; }

    /// <summary>
    /// Watches <paramref name="bandwidthHz"/> of ARDOP centred on <paramref name="centreHz"/>,
    /// padded by the tuning range each side, in audio sampled at <paramref name="sampleRate"/>.
    /// </summary>
    internal ArdopBusyDetector(double centreHz, double bandwidthHz, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        double half = EmittedHalfWidthHz(bandwidthHz) + TuningRangeHz;
        LowHz = Math.Max(50, centreHz - half);
        HighHz = Math.Min((sampleRate / 2.0) - 50, centreHz + half);
        _bandpass = new FirFilter(FilterDesign.BandPass(LowHz, HighHz, sampleRate, Taps));

        // Everything else left at the meter's own defaults on purpose: block length,
        // assert and release thresholds and hold time are its business, and restating them
        // here would pin this to today's values and quietly diverge when they are tuned.
        _energy = new EnergyBusyDetector(sampleRate);
    }

    /// <summary>True while the ARDOP slot carries energy well above its own noise floor.</summary>
    internal bool Busy => _energy.Busy;

    /// <summary>Feeds one block of channel receive audio through the band and the meter.</summary>
    internal void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            _energy.Process(_bandpass.Next(sample));
        }
    }
}
