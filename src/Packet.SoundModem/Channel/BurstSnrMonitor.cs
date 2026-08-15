using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Channel;

/// <summary>
/// The channel's own per-modem burst-SNR instrument: a <see cref="WaterfallSource"/> over
/// the receive audio feeding one <see cref="BandActivityTracker"/> per modem band, so that
/// every decoded frame can carry the strength of the burst it arrived on
/// (<see cref="FrameQuality.SnrDb"/>) - into the frame log, the journal line and the KISS
/// quality frame - whether or not a waterfall is configured. The daemon measured this
/// number for years and threw it away unless a browser was watching
/// (rx-roadmap.md workstream 9); answering "how strong was it" about a frame heard last
/// Tuesday used to mean standing up a parallel IQ capture.
/// </summary>
/// <remarks>
/// <para><b>What the number is, and is not.</b> Mean in-band power over the burst against
/// a rolling minimum noise floor (see <see cref="BandActivityTracker"/>), in dB - the same
/// convention the waterfall panel has always shown. It is NOT the 3 kHz-referenced SNR the
/// sim ladder and the Watterson masks use: the reference bandwidth is the modem's own band
/// and the floor is the band's quietest recent half second, so the two scales differ by a
/// bandwidth ratio and a floor-estimation bias. Comparing a frame-log <c>snr_db</c> against
/// a mask row's SNR without converting is exactly the mistake this remark exists to
/// prevent.</para>
/// <para>Bands come from <see cref="ModemBandProbe"/> - the same measurement the waterfall
/// overlay and the RF band planner use, so all three can never disagree about where a
/// modem's band is. A modem whose band cannot be probed (nothing modulates) simply gets no
/// tracker and its frames carry a null SNR.</para>
/// <para>Single-threaded with the receive path, like the modems it serves; the caller
/// feeds only non-transmit audio (our own transmissions are not signals we heard).</para>
/// </remarks>
internal sealed class BurstSnrMonitor
{
    private readonly WaterfallSource _source;
    private readonly Dictionary<int, BandActivityTracker> _trackers = [];
    private readonly int _sampleRate;

    public BurstSnrMonitor(int sampleRate)
    {
        _sampleRate = sampleRate;
        _source = new WaterfallSource(sampleRate, OnLine, LinesPerSecondFor(sampleRate));
    }

    /// <summary>The display default where it divides the rate, else the largest familiar
    /// line rate that does - the tracker only needs the rate it is told, but
    /// <see cref="WaterfallSource"/> insists the hop be whole samples (8 kHz engine-rate
    /// channels exist in the sims).</summary>
    private static int LinesPerSecondFor(int sampleRate)
    {
        foreach (int rate in (int[])[30, 25, 20, 16, 10, 8, 5, 4, 2])
        {
            if (sampleRate % rate == 0)
            {
                return rate;
            }
        }

        return 1;
    }

    /// <summary>Measures the modem's band and starts tracking it. Call once per modem,
    /// before audio flows.</summary>
    public void AddModem(int subChannel, IModem modem)
    {
        if (ModemBandProbe.TryMeasure(modem, _sampleRate, out double lowHz, out double highHz))
        {
            _trackers[subChannel] = new BandActivityTracker(
                _source.BinWidthHz, _source.LinesPerSecond, _source.LineLength, lowHz, highHz);
        }
    }

    /// <summary>Feeds received (never transmitted) audio.</summary>
    public void Process(ReadOnlySpan<float> samples) => _source.Process(samples);

    /// <summary>The burst behind a just-decoded frame on this sub-channel, or null when the
    /// band has been quiet (or the modem's band was never measurable) - see
    /// <see cref="BandActivityTracker.TryMeasureBurst"/>.</summary>
    public double? MeasureBurst(int subChannel) =>
        _trackers.TryGetValue(subChannel, out BandActivityTracker? tracker)
            && tracker.TryMeasureBurst(out double snrDb, out _)
            ? Math.Round(snrDb, 1)
            : null;

    private void OnLine(long index, ReadOnlyMemory<byte> line)
    {
        foreach (BandActivityTracker tracker in _trackers.Values)
        {
            tracker.AddLine(line.Span);
        }
    }
}
