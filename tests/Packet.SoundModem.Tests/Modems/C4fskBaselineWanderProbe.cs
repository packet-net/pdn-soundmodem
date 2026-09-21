using M0LTE.Dsp;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// One first-order high-pass section, bilinear-transformed with its corner pre-warped so the
/// digital -3 dB point lands on the analogue one. A coupling capacitor is a first-order high
/// pass and nothing else, so this is the right shape for the thing under test rather than a
/// convenience.
/// </summary>
internal sealed class OnePole(double cornerHz, int sampleRate)
{
    private readonly float _a0 = (float)(1.0 / (1.0 + Math.Tan(Math.PI * cornerHz / sampleRate)));

    private readonly float _b1 = (float)((1.0 - Math.Tan(Math.PI * cornerHz / sampleRate))
        / (1.0 + Math.Tan(Math.PI * cornerHz / sampleRate)));

    private float _x;
    private float _y;

    internal float Next(float x)
    {
        float y = (_a0 * (x - _x)) + (_b1 * _y);
        _x = x;
        _y = y;
        return y;
    }
}

/// <summary>
/// An audio path between the modulator and the demodulator: a cascade of identical first-order
/// high-pass sections, an optional linear-phase FIR carrying the top end, and a flat gain.
/// Every <see cref="Apply"/> builds its own filter state, so runs do not contaminate each other.
/// </summary>
internal sealed class AudioPath
{
    private readonly double _cornerHz;
    private readonly int _sections;
    private readonly float[]? _topEnd;
    private readonly float _gain;

    private AudioPath(string label, double cornerHz, int sections, float[]? topEnd, double gainDb)
    {
        Label = label;
        _cornerHz = cornerHz;
        _sections = sections;
        _topEnd = topEnd;
        _gain = (float)Math.Pow(10, gainDb / 20);
    }

    internal string Label { get; }

    /// <summary>No impairment at all: the loopback both modes already pass.</summary>
    internal static AudioPath Flat { get; } = new("none", 0, 0, null, 0);

    /// <summary>
    /// A high-pass network whose overall -3 dB point is <paramref name="minusThreeDbHz"/>, built
    /// from <paramref name="sections"/> identical first-order sections. The section count is a
    /// parameter because it matters: measured points from 50 Hz up cannot tell one section from
    /// four, and the two leave very different amounts of low-frequency content behind. Well above
    /// the corner a cascade of N sections passes N times the residue a single section does, and
    /// that residue is the baseline wander.
    /// </summary>
    internal static AudioPath HighPass(double minusThreeDbHz, int sections = 1)
    {
        if (minusThreeDbHz <= 0 || sections <= 0)
        {
            return Flat;
        }

        double corner = minusThreeDbHz * Math.Sqrt(Math.Pow(2, 1.0 / sections) - 1);
        return new($"{minusThreeDbHz:0} Hz x{sections}", corner, sections, null, 0);
    }

    /// <summary>The measured rig response, or either half of it; see
    /// <see cref="MeasuredRigPath"/>.</summary>
    internal static AudioPath Measured(string label, bool low, bool high) => new(
        label,
        low ? MeasuredRigPath.HighPassCornerHz : 0,
        low ? MeasuredRigPath.HighPassSections : 0,
        high ? MeasuredRigPath.TopEndTaps : null,
        low ? MeasuredRigPath.GainDb : 0);

    /// <summary>The single-section reading of the same measurement, so the answer does not rest
    /// on how many sections the low end was fitted with.</summary>
    internal static AudioPath MeasuredSinglePole() =>
        new("measured (1-section fit)", MeasuredRigPath.SinglePoleCornerHz, 1, MeasuredRigPath.TopEndTaps, 0);

    /// <summary>The rig's own control: a FIR that is flat everywhere, so all it does is delay.
    /// If a path that only delays the burst stops the frame then the rig is broken and the ladder
    /// means nothing, which is what it was until <see cref="Apply"/> ran past the end of its
    /// input.</summary>
    internal static AudioPath PureDelay() => new("delay only", 0, 0, MeasuredRigPath.FlatTaps, 0);

    /// <summary>
    /// Puts the audio through the path. The output runs past the input by the path's own impulse
    /// response plus 20 ms, because an analogue path does not stop when the transmitter does, and
    /// a fixed-length output chops the path's delay off the end of the burst. The end of the
    /// burst is the IL2P trailer, so that costs the frame for reasons which have nothing to do
    /// with the filter's shape.
    /// </summary>
    internal float[] Apply(ReadOnlySpan<float> audio, int sampleRate)
    {
        var sections = new OnePole[_sections];
        for (int s = 0; s < sections.Length; s++)
        {
            sections[s] = new OnePole(_cornerHz, sampleRate);
        }

        var fir = _topEnd is null ? null : new FirFilter(_topEnd);
        int tail = (_topEnd?.Length ?? 0) + (sampleRate / 50);
        var output = new float[audio.Length + tail];
        for (int i = 0; i < output.Length; i++)
        {
            float value = i < audio.Length ? audio[i] * _gain : 0f;
            foreach (OnePole section in sections)
            {
                value = section.Next(value);
            }

            output[i] = fir is null ? value : fir.Next(value);
        }

        return output;
    }

    /// <summary>
    /// This path's response at one frequency, measured the way the rig was measured: put a tone
    /// through it, let it settle, read the level back. Not computed from the coefficients, so a
    /// mistake in the filter itself shows up in the table rather than hiding behind the algebra
    /// it was designed from.
    /// </summary>
    internal double ResponseDb(double toneHz, int sampleRate)
    {
        int settle = sampleRate;
        var tone = new float[settle + sampleRate];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = (float)Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
        }

        float[] through = Apply(tone, sampleRate);
        double outPower = 0;
        double inPower = 0;
        for (int i = settle; i < tone.Length; i++)
        {
            outPower += through[i] * (double)through[i];
            inPower += tone[i] * (double)tone[i];
        }

        return 10 * Math.Log10(outPower / inPower);
    }
}

/// <summary>
/// The measured radio1/radio2 bench response, and the filter fitted to it.
/// </summary>
/// <remarks>
/// <para><b>The measurement</b> (2026-09-21, stepped tone out of radio2's T12 tap, through RF,
/// read off radio1's raw 48 kHz capture) is <see cref="Frequencies"/> against
/// <see cref="ResponseDb"/>: flat from 280 Hz to 4.5 kHz, about -3 dB at 70 Hz, -6.9 dB at
/// 50 Hz, -3.7 dB at 6.4 kHz. The repo's own wiring guide (docs/hardware/tait-tm8100-cm108.md)
/// independently puts the transmit coupling capacitor's corner at 43 to 64 Hz, so the low end is
/// a coupling network and a cascade of first-order high-pass sections is the right shape to fit
/// it with.</para>
/// <para><b>The fit.</b> Low end: a grid search over section count and corner against the ten
/// points from 50 Hz to 1130 Hz, with a flat gain, picks <see cref="HighPassSections"/> sections
/// at <see cref="HighPassCornerHz"/> with <see cref="GainDb"/> (rms 0.17 dB, worst point 0.43 dB
/// at 70 Hz, where the model is the more lossy of the two); that network is -3 dB at 79.5 Hz.
/// One section alone fits the same ten points at <see cref="SinglePoleCornerHz"/> with rms
/// 0.44 dB and 0.98 dB worst. Both are carried, because a measurement that starts at 50 Hz
/// cannot tell them apart while the wander they leave differs several-fold. Top end: the
/// measured curve above 1130 Hz is carried by a 257-tap linear-phase FIR designed from the
/// magnitude itself, because the knee the measurement shows (flat to 4.5 kHz, then -3.7 dB by
/// 6.4 kHz) is steeper than any first-order cascade fits - a low-pass cascade good at 6.4 kHz is
/// 0.7 to 0.8 dB too lossy at 4.5 kHz. Above 6.4 kHz the measurement stops and the FIR continues
/// the last segment's slope in log frequency. That is an extrapolation and is flagged as one: it
/// covers the top of c4fsk19200's band and none of c4fsk9600's.</para>
/// <para>The fitted path's own response is measured back with tones and printed beside the rig's,
/// so the fit error is in the report rather than in this comment.</para>
/// </remarks>
internal static class MeasuredRigPath
{
    /// <summary>The tone frequencies the bench response was stepped over.</summary>
    internal static readonly double[] Frequencies =
        [50, 70, 100, 140, 200, 280, 400, 560, 800, 1130, 1600, 2260, 3200, 4500, 6400];

    /// <summary>What came back at each of <see cref="Frequencies"/>, in dB.</summary>
    internal static readonly double[] ResponseDb =
        [-6.9, -3.2, -1.7, -0.9, -0.4, -0.1, 0.1, 0.1, 0.1, 0.0, -0.1, -0.3, -0.8, -0.9, -3.7];

    /// <summary>Sections in the fitted low end.</summary>
    internal const int HighPassSections = 4;

    /// <summary>Each fitted section's own corner; four of them are -3 dB at 79.5 Hz.</summary>
    internal const double HighPassCornerHz = 34.6;

    /// <summary>The one-section reading of the same ten points.</summary>
    internal const double SinglePoleCornerHz = 89.55;

    /// <summary>The flat gain the low-end fit wants.</summary>
    internal const double GainDb = 0.171;

    /// <summary>Where the FIR takes over from the flat gain.</summary>
    private const double TopEndFromHz = 1130;

    private const int Taps = 257;
    private const int SampleRate = 48000;
    private static readonly Lazy<float[]> LazyTopEnd = new(DesignTopEnd);
    private static readonly Lazy<float[]> LazyFlat = new(DesignFlat);

    /// <summary>The top-end FIR, designed once.</summary>
    internal static float[] TopEndTaps => LazyTopEnd.Value;

    /// <summary>The same length of FIR at unity everywhere: the delay-only control.</summary>
    internal static float[] FlatTaps => LazyFlat.Value;

    /// <summary>The measured curve at an arbitrary frequency: linear in dB against log frequency
    /// between the measured points, the last segment's slope continued above 6400 Hz and the
    /// first segment's continued below 50 Hz.</summary>
    internal static double InterpolateDb(double hz)
    {
        if (hz <= Frequencies[0])
        {
            return ResponseDb[0] + (SlopeDbPerOctave(0) * Math.Log2(Math.Max(hz, 1e-3) / Frequencies[0]));
        }

        for (int i = 1; i < Frequencies.Length; i++)
        {
            if (hz <= Frequencies[i])
            {
                double t = Math.Log2(hz / Frequencies[i - 1]) / Math.Log2(Frequencies[i] / Frequencies[i - 1]);
                return ResponseDb[i - 1] + (t * (ResponseDb[i] - ResponseDb[i - 1]));
            }
        }

        int last = Frequencies.Length - 1;
        return ResponseDb[last] + (SlopeDbPerOctave(last - 1) * Math.Log2(hz / Frequencies[last]));
    }

    private static double SlopeDbPerOctave(int segment) =>
        (ResponseDb[segment + 1] - ResponseDb[segment])
        / Math.Log2(Frequencies[segment + 1] / Frequencies[segment]);

    /// <summary>Frequency-sampled linear-phase FIR: unity below <see cref="TopEndFromHz"/>, the
    /// measured curve less the cascade's flat gain above it, Hamming-windowed.</summary>
    private static float[] DesignTopEnd()
    {
        const int bins = 4096;
        var magnitude = new double[bins + 1];
        for (int k = 0; k <= bins; k++)
        {
            double hz = (double)k * SampleRate / (2.0 * bins);
            double db = hz <= TopEndFromHz ? 0 : InterpolateDb(hz) - GainDb;
            magnitude[k] = Math.Pow(10, Math.Max(db, -60) / 20);
        }

        var taps = new float[Taps];
        int centre = (Taps - 1) / 2;
        int length = 2 * bins;
        for (int n = 0; n < Taps; n++)
        {
            double sum = magnitude[0];
            for (int k = 1; k < bins; k++)
            {
                sum += 2 * magnitude[k] * Math.Cos(2 * Math.PI * k * (n - centre) / length);
            }

            sum += magnitude[bins] * Math.Cos(Math.PI * (n - centre));
            double window = 0.54 - (0.46 * Math.Cos(2 * Math.PI * n / (Taps - 1)));
            taps[n] = (float)(window * sum / length);
        }

        return taps;
    }

    private static float[] DesignFlat()
    {
        var taps = new float[Taps];
        taps[(Taps - 1) / 2] = 1;
        return taps;
    }
}

/// <summary>
/// The rig behind this file: a known frame, modulated by the real modem, put through an
/// <see cref="AudioPath"/>, given calibrated AWGN, and fed to a receiver. One call is one burst
/// and one yes-or-no, so every figure in the report is a count out of a stated number of seeds.
/// </summary>
internal static class WanderRig
{
    internal const int Rate = 48000;

    /// <summary>The bench's own TXDELAY (<c>SoundModemChannel</c>'s default), so the preamble in
    /// front of the sync word is the one the radios actually sent.</summary>
    internal const int TxDelayMs = 300;

    internal static IModem Make(string mode, Action<byte[]> received) => mode switch
    {
        "c4fsk9600" => C4fskModem.C4fsk9600(Rate, received),
        "c4fsk19200" => C4fskModem.C4fsk19200(Rate, received),
        "fsk9600" => FskModem.Fsk9600(Rate, received, FskFraming.ClassicHdlc),
        "fsk9600-il2p" => FskModem.Fsk9600(Rate, received, FskFraming.Il2pCrc),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown mode"),
    };

    /// <summary>The sim rig's frame: an AX.25-looking UI header then a seeded random body, as
    /// <see cref="C4fskTxDelayRig"/> builds it.</summary>
    internal static byte[] Frame(int bytes, int seed)
    {
        byte[] frame = new byte[bytes];
        ReadOnlySpan<byte> header =
        [
            0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4,
            0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0,
        ];
        header[..Math.Min(header.Length, bytes)].CopyTo(frame);
        if (bytes > header.Length)
        {
            new Random(seed).NextBytes(frame.AsSpan(header.Length));
        }

        return frame;
    }

    /// <summary>The receive audio one seed's burst arrives as, through one path.</summary>
    internal static float[] Burst(string mode, int frameBytes, int seed, AudioPath path, double snrDb)
    {
        float[] clean = Trim(Make(mode, _ => { }).Modulate(Frame(frameBytes, seed), TxDelayMs));
        float[] shaped = path.Apply(clean, Rate);
        var channel = new WattersonChannel(Rate, seed + 3_000_000);
        return channel.Apply(
            shaped, snrDb, noiseBandwidthHz: 3000, leadInSamples: Rate / 2, leadOutSamples: Rate / 4);
    }

    /// <summary>Whether the frame came back through the production modem for that mode.</summary>
    internal static bool Delivers(string mode, int frameBytes, int seed, AudioPath path, double snrDb)
    {
        byte[] frame = Frame(frameBytes, seed);
        float[] rx = Burst(mode, frameBytes, seed, path, snrDb);
        bool decoded = false;
        IModem modem = Make(mode, f => decoded |= f.AsSpan().SequenceEqual(frame));
        Feed(rx, modem.Process);
        return decoded;
    }

    /// <summary>How many of <paramref name="seeds"/> bursts came back.</summary>
    internal static int Delivered(string mode, int frameBytes, AudioPath path, double snrDb, int seeds)
    {
        int delivered = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            delivered += Delivers(mode, frameBytes, seed, path, snrDb) ? 1 : 0;
        }

        return delivered;
    }

    /// <summary>The same count through the local copy of the C4FSK decision logic, with its DC
    /// restoration loop at <paramref name="dcGain"/>; gain 0 is the production behaviour.</summary>
    internal static int DeliveredRestored(
        string mode, int frameBytes, AudioPath path, double snrDb, int seeds, float dcGain,
        float errorClamp = float.PositiveInfinity)
    {
        int symbolRate = mode == "c4fsk19200" ? 9600 : 4800;
        int delivered = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            byte[] frame = Frame(frameBytes, seed);
            float[] rx = Burst(mode, frameBytes, seed, path, snrDb);
            bool decoded = false;
            var receiver = new DcRestoringC4fskReceiver(
                Rate, symbolRate, f => decoded |= f.AsSpan().SequenceEqual(frame), dcGain, errorClamp);
            Feed(rx, receiver.Process);
            delivered += decoded ? 1 : 0;
        }

        return delivered;
    }

    internal static void Feed(float[] audio, Action<ReadOnlySpan<float>> process)
    {
        int block = Rate / 10;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
        }
    }

    private static float[] Trim(float[] audio)
    {
        int start = 0;
        while (start < audio.Length && audio[start] == 0f)
        {
            start++;
        }

        int end = audio.Length;
        while (end > start && audio[end - 1] == 0f)
        {
            end--;
        }

        return audio[start..end];
    }
}

/// <summary>
/// A local copy of <see cref="C4fskModem"/>'s decision logic with one addition: a fast,
/// decision-directed DC estimate per timing phase, subtracted from the slicer input.
/// </summary>
/// <remarks>
/// <para>The production modem's only defence against a moving baseline is its envelope tracker,
/// which moves each peak 5 % of the way toward the reading on every <em>outer</em> decision at
/// the clock instant. Outer levels are half the symbols, so the midpoint the slicer's thresholds
/// hang off has a time constant near 40 symbols, about 8 ms at 4800 sym/s. The time constant of
/// a 70 Hz pole is 2.3 ms, so the tracker is roughly four times too slow to follow what an audio
/// coupling network does to this waveform, and the 5-tap symbol-spaced equalizer cannot reach it
/// either: it spans 1 ms.</para>
/// <para>The addition is a first-order loop on the slicer's own residual. At every decision take
/// the difference between the equalized value and the ideal level for the symbol decided, and
/// accumulate it at <c>dcGain</c> into a per-phase estimate that is subtracted from the next
/// symbol's slicer input. Gain 0 leaves the chain exactly as the production modem runs it, which
/// is how the copy is checked against the real thing. The estimate is clamped to the outer
/// level's own size so a wrong lock cannot run away.</para>
/// <para>This is a prototype for measuring the size of the win, not a proposed patch. It shares
/// its error signal with the equalizer's NLMS update, and what that interaction costs on the
/// AWGN and fm-data ladders is not measured here.</para>
/// </remarks>
internal sealed class DcRestoringC4fskReceiver
{
    private const int FfeLength = 5;
    private const float FfeMu = 0.05f;
    private const int AcquireSymbols = 32;
    private const float EnvelopeRate = 0.05f;
    private const int DedupeWindowSymbols = 32;
    private const double PhaseStep = 0.05;
    private const int PhasePairs = 3;
    private const double AcquireInertia = 0.74;
    private const double HoldInertia = 0.995;
    private const int SyncWord = 0x57DF7F;

    private static readonly int[] DibitToLevel = [2, 3, 1, 0];
    private static readonly int[] LevelToDibit = BuildInverse();
    private static readonly double[] PhaseFractions = TimingDiversity.Build(PhaseStep, PhasePairs);

    private readonly FirFilter _rxFilter;
    private readonly EnergyBusyDetector _energyBusy;
    private readonly PacketDcd _packetDcd = new();
    private readonly Il2pReceiver[] _deframers;
    private readonly FrameDeduper _deduper;
    private readonly int _upsample;
    private readonly double _clockIncrement;
    private readonly double _pointsPerSymbol;
    private readonly float[] _slicerRing;
    private readonly int _ringLead;
    private readonly float[] _ffeTaps;
    private readonly float[] _ffeHistory;
    private readonly int[] _ffeCount;
    private readonly int[] _alternatingOuterRun;
    private readonly int[] _nonAlternatingRun;
    private readonly bool[] _ffeFrozen;
    private readonly int[] _previousLevel;
    private readonly float[] _dc;
    private readonly float _dcGain;
    private readonly float _errorClamp;

    private long _pointIndex;
    private long _pendingInstant = -1;
    private long _symbolsSeen;
    private int _symbolsSinceGate;
    private double _clockPhase;
    private int _lastSign;
    private bool _previousEnergyBusy;
    private float _peakHigh;
    private float _peakLow;
    private float _previousFiltered;

    internal DcRestoringC4fskReceiver(
        int sampleRate, int symbolRate, Action<byte[]> frameReceived, float dcGain,
        float errorClamp = float.PositiveInfinity)
    {
        _dcGain = dcGain;
        _errorClamp = errorClamp;
        _rxFilter = new FirFilter(FilterDesign.LowPass(1.5 * symbolRate, sampleRate, 48 * sampleRate / 48000));
        _energyBusy = new EnergyBusyDetector(sampleRate, blockMilliseconds: 20);
        _deduper = new FrameDeduper(DedupeWindowSymbols);
        _deframers = new Il2pReceiver[PhaseFractions.Length];
        for (int phase = 0; phase < _deframers.Length; phase++)
        {
            _deframers[phase] = new Il2pReceiver(
                (frame, _, delivery) =>
                {
                    if (!_deduper.ShouldEmit(frame, _symbolsSeen, !delivery.MonitorOnly))
                    {
                        return;
                    }

                    if (!delivery.MonitorOnly)
                    {
                        frameReceived(frame);
                    }
                },
                crcMode: true, acceptPlainIl2p: false, syncWord: SyncWord);
        }

        _ffeTaps = new float[PhaseFractions.Length * FfeLength];
        _ffeHistory = new float[PhaseFractions.Length * FfeLength];
        _ffeCount = new int[PhaseFractions.Length];
        _alternatingOuterRun = new int[PhaseFractions.Length];
        _nonAlternatingRun = new int[PhaseFractions.Length];
        _ffeFrozen = new bool[PhaseFractions.Length];
        _previousLevel = new int[PhaseFractions.Length];
        _dc = new float[PhaseFractions.Length];

        _upsample = sampleRate / symbolRate < 8 ? 2 : 1;
        _clockIncrement = (double)symbolRate / (sampleRate * _upsample);
        _pointsPerSymbol = 1.0 / _clockIncrement;
        _ringLead = (int)Math.Ceiling(PhaseStep * PhasePairs * _pointsPerSymbol) + 1;
        _slicerRing = new float[(2 * _ringLead) + 4];
        ResetFfe();
    }

    internal void Process(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            float filtered = _rxFilter.Next(sample);
            _energyBusy.Process(filtered);
            if (!_energyBusy.Busy)
            {
                if (_previousEnergyBusy)
                {
                    foreach (Il2pReceiver deframer in _deframers)
                    {
                        deframer.Reset();
                    }

                    _pendingInstant = -1;
                    ResetFfe();
                }

                _previousEnergyBusy = false;
                continue;
            }

            if (!_previousEnergyBusy)
            {
                _symbolsSinceGate = 0;
            }

            _previousEnergyBusy = true;

            for (int point = 1; point <= _upsample; point++)
            {
                float value = _previousFiltered + ((filtered - _previousFiltered) * point / _upsample);
                if (_symbolsSinceGate < AcquireSymbols)
                {
                    _peakHigh += (value - _peakHigh) * (value > _peakHigh ? 0.08f : 0.00001f);
                    _peakLow += (value - _peakLow) * (value < _peakLow ? 0.08f : 0.00001f);
                }

                float mid = (_peakHigh + _peakLow) * 0.5f;
                float half = Math.Max((_peakHigh - _peakLow) * 0.5f, 1e-6f);
                float normalised = (value - mid) / half;
                _slicerRing[(int)(_pointIndex % _slicerRing.Length)] = normalised;

                _clockPhase += _clockIncrement;
                if (_clockPhase >= 0.5)
                {
                    _clockPhase -= 1.0;
                    if (_pendingInstant >= 0)
                    {
                        DecidePending();
                    }

                    _pendingInstant = _pointIndex;
                }

                if (_pendingInstant >= 0 && _pointIndex >= _pendingInstant + _ringLead)
                {
                    DecidePending();
                }

                int sign = normalised > 0 ? 1 : 0;
                if (sign != _lastSign)
                {
                    _lastSign = sign;
                    _packetDcd.OnTransition(_clockPhase);
                    _clockPhase *= _packetDcd.Asserted ? HoldInertia : AcquireInertia;
                }

                _pointIndex++;
            }

            _previousFiltered = filtered;
        }
    }

    private static int[] BuildInverse()
    {
        var inverse = new int[4];
        for (int dibit = 0; dibit < 4; dibit++)
        {
            inverse[DibitToLevel[dibit]] = dibit;
        }

        return inverse;
    }

    private void DecidePending()
    {
        long instant = _pendingInstant;
        _pendingInstant = -1;
        int ring = _slicerRing.Length;
        for (int phase = 0; phase < PhaseFractions.Length; phase++)
        {
            double position = instant + (PhaseFractions[phase] * _pointsPerSymbol);
            long lower = (long)Math.Floor(position);
            float fraction = (float)(position - lower);
            int a = (int)(((lower % ring) + ring) % ring);
            int b = (a + 1) % ring;
            Decide(phase, _slicerRing[a] + (fraction * (_slicerRing[b] - _slicerRing[a])));
        }
    }

    private void Decide(int phase, float normalised)
    {
        int taps = phase * FfeLength;
        normalised -= _dc[phase];
        for (int t = 0; t < FfeLength - 1; t++)
        {
            _ffeHistory[taps + t] = _ffeHistory[taps + t + 1];
        }

        _ffeHistory[taps + FfeLength - 1] = normalised;
        if (++_ffeCount[phase] < (FfeLength / 2) + 1)
        {
            return;
        }

        float equalized = 0;
        float power = 1e-6f;
        for (int t = 0; t < FfeLength; t++)
        {
            equalized += _ffeTaps[taps + t] * _ffeHistory[taps + t];
            power += _ffeHistory[taps + t] * _ffeHistory[taps + t];
        }

        int level = equalized switch
        {
            < -2f / 3f => 0,
            < 0f => 1,
            < 2f / 3f => 2,
            _ => 3,
        };

        if (level is 0 or 3 && level == 3 - _previousLevel[phase])
        {
            _nonAlternatingRun[phase] = 0;
            if (++_alternatingOuterRun[phase] >= 8)
            {
                _ffeFrozen[phase] = true;
            }
        }
        else
        {
            _alternatingOuterRun[phase] = 0;
            if (++_nonAlternatingRun[phase] >= 4)
            {
                _ffeFrozen[phase] = false;
            }
        }

        _previousLevel[phase] = level;
        float target = level switch { 0 => -1f, 1 => -1f / 3f, 2 => 1f / 3f, _ => 1f };
        if (!_ffeFrozen[phase])
        {
            float step = FfeMu / power * (target - equalized);
            for (int t = 0; t < FfeLength; t++)
            {
                _ffeTaps[taps + t] += step * _ffeHistory[taps + t];
            }
        }

        // The addition. The error can optionally be clamped before it is accumulated, because a
        // decision that reads one level for its neighbour hands the loop an error a whole level
        // spacing wide and drags it further off; clamping to half a spacing keeps a wrong
        // decision from doing more damage than a right one can undo. The estimate itself is
        // clamped to the outer level so a wrong lock cannot run away.
        if (_dcGain > 0)
        {
            float error = Math.Clamp(equalized - target, -_errorClamp, _errorClamp);
            _dc[phase] = Math.Clamp(_dc[phase] + (_dcGain * error), -1f, 1f);
        }

        int dibit = LevelToDibit[level];
        _deframers[phase].PushBit((dibit >> 1) & 1);
        _deframers[phase].PushBit(dibit & 1);
        if (phase == 0)
        {
            _symbolsSeen++;
            _packetDcd.OnSymbol(Math.Abs(_ffeHistory[taps + (FfeLength / 2)]));
            TrackEnvelope(level, _ffeHistory[taps + (FfeLength / 2)]);
        }
    }

    /// <summary>As the production tracker, except that the reading is put back into the slicer's
    /// units with the DC estimate added in again: the envelope is a property of the signal, not
    /// of the correction applied to it.</summary>
    private void TrackEnvelope(int level, float normalisedCentre)
    {
        if (++_symbolsSinceGate <= AcquireSymbols || level is not (0 or 3))
        {
            return;
        }

        float mid = (_peakHigh + _peakLow) * 0.5f;
        float half = Math.Max((_peakHigh - _peakLow) * 0.5f, 1e-6f);
        float reading = ((normalisedCentre + _dc[0]) * half) + mid;
        if (level == 3)
        {
            _peakHigh += (reading - _peakHigh) * EnvelopeRate;
        }
        else
        {
            _peakLow += (reading - _peakLow) * EnvelopeRate;
        }
    }

    private void ResetFfe()
    {
        Array.Clear(_ffeTaps);
        Array.Clear(_ffeHistory);
        Array.Clear(_ffeCount);
        Array.Clear(_alternatingOuterRun);
        Array.Clear(_nonAlternatingRun);
        Array.Clear(_ffeFrozen);
        Array.Clear(_previousLevel);
        Array.Clear(_dc);
        for (int phase = 0; phase < PhaseFractions.Length; phase++)
        {
            _ffeTaps[(phase * FfeLength) + (FfeLength / 2)] = 1;
        }
    }
}

/// <summary>
/// Bench probe (set <c>C4FSK_WANDER_PROBE=1</c>) for issue #518: does the measured
/// radio1/radio2 audio response, which high-passes at about 70 Hz and is otherwise flat across
/// both modes' bands, close a 4-level eye through baseline wander?
/// </summary>
/// <remarks>
/// Part 1 is a ladder: a high-pass network at a run of corner frequencies in front of the real
/// receiver, several seeds a rung, at two frame sizes, for both 4-PAM modes and the binary G3RUH
/// control at the same baseband. Part 2 replaces the textbook pole with the network fitted to the
/// 15 measured points (<see cref="MeasuredRigPath"/>) and splits it into its low and high halves,
/// so the two candidate mechanisms are separated rather than confounded. Part 3 is
/// <see cref="C4fskDcRestorationProbe"/>. Seeds default to 40 and can be overridden with
/// <c>C4FSK_WANDER_SEEDS</c>.
/// </remarks>
public class C4fskBaselineWanderProbe(ITestOutputHelper output)
{
    internal const string Gate = "C4FSK_WANDER_PROBE";

    /// <summary>Essentially noiseless: the sim's own clean row, so the only impairment is the
    /// path.</summary>
    internal const double CleanSnrDb = 60;

    /// <summary>Overall -3 dB points of the Part 1 ladder, in Hz; 0 is no filter at all.</summary>
    internal static readonly double[] Ladder = [0, 10, 20, 30, 50, 70, 100, 150, 200, 300, 500];

    /// <summary>Frame sizes: a long frame holds longer runs of one symbol, so more low-frequency
    /// content, than a short one.</summary>
    internal static readonly int[] FrameSizes = [60, 500];

    /// <summary>The modes under test: both 4-PAM modes, the binary G3RUH control at the same
    /// baseband, and the same binary baseband under the C4FSK modes' own IL2P+CRC coding, so that
    /// "two levels instead of four" is separated from "a different amount of FEC".</summary>
    internal static readonly string[] Modes = ["c4fsk9600", "c4fsk19200", "fsk9600", "fsk9600-il2p"];

    /// <summary>Seeds per rung. Every figure printed is a count out of this.</summary>
    internal static int Seeds =>
        int.TryParse(Environment.GetEnvironmentVariable("C4FSK_WANDER_SEEDS"), out int n) ? n : 40;

    [Fact]
    public void Delivery_Against_A_High_Pass_Corner_Ladder()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Gate) is null, "bench probe only");

        output.WriteLine(
            $"Part 1: a high-pass network in front of the real receiver, {CleanSnrDb:0} dB AWGN, "
            + $"TXDELAY {WanderRig.TxDelayMs} ms, seeds 1..{Seeds}, delivered out of {Seeds}.");
        output.WriteLine("Columns are the network's overall -3 dB point in Hz.");

        foreach (int sections in new[] { 1, 4 })
        {
            output.WriteLine(string.Empty);
            output.WriteLine($"{sections} first-order section(s):");
            output.WriteLine(
                "mode           bytes | "
                + string.Join(" ", Ladder.Select(c => $"{(c == 0 ? "none" : $"{c:0}"),5}")));
            foreach (string mode in Modes)
            {
                foreach (int bytes in FrameSizes)
                {
                    IEnumerable<string> row = Ladder.Select(corner =>
                        $"{WanderRig.Delivered(mode, bytes, AudioPath.HighPass(corner, sections), CleanSnrDb, Seeds),5}");
                    output.WriteLine($"{mode,-14} {bytes,5} | {string.Join(" ", row)}");
                }
            }
        }
    }

    [Fact]
    public void Delivery_Through_The_Measured_Bench_Response()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Gate) is null, "bench probe only");

        AudioPath full = AudioPath.Measured("measured (full)", low: true, high: true);
        AudioPath low = AudioPath.Measured("measured (low end only)", low: true, high: false);
        AudioPath high = AudioPath.Measured("measured (top end only)", low: false, high: true);
        AudioPath single = AudioPath.MeasuredSinglePole();

        output.WriteLine("Part 2a: the fitted path's own response, measured back with tones.");
        output.WriteLine(string.Empty);
        output.WriteLine("    Hz |   rig | fitted | error | low only | top only");
        for (int i = 0; i < MeasuredRigPath.Frequencies.Length; i++)
        {
            double hz = MeasuredRigPath.Frequencies[i];
            double fitted = full.ResponseDb(hz, WanderRig.Rate);
            output.WriteLine(
                $"{hz,6:0} | {MeasuredRigPath.ResponseDb[i],5:0.0} | {fitted,6:0.00} | "
                + $"{fitted - MeasuredRigPath.ResponseDb[i],5:0.00} | "
                + $"{low.ResponseDb(hz, WanderRig.Rate),8:0.00} | "
                + $"{high.ResponseDb(hz, WanderRig.Rate),8:0.00}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"Part 2b: delivery through it. TXDELAY {WanderRig.TxDelayMs} ms, seeds 1..{Seeds}, "
            + $"delivered out of {Seeds}. 'delay only' is the rig's control: a path that only "
            + "delays the burst must not cost a frame.");
        output.WriteLine(string.Empty);
        output.WriteLine(
            "mode           bytes | none | delay only | full | low only | top only "
            + "| 1-section | full+25dB | none+25dB");

        foreach (string mode in Modes)
        {
            foreach (int bytes in FrameSizes)
            {
                output.WriteLine(
                    $"{mode,-14} {bytes,5} | "
                    + $"{WanderRig.Delivered(mode, bytes, AudioPath.Flat, CleanSnrDb, Seeds),4} | "
                    + $"{WanderRig.Delivered(mode, bytes, AudioPath.PureDelay(), CleanSnrDb, Seeds),10} | "
                    + $"{WanderRig.Delivered(mode, bytes, full, CleanSnrDb, Seeds),4} | "
                    + $"{WanderRig.Delivered(mode, bytes, low, CleanSnrDb, Seeds),8} | "
                    + $"{WanderRig.Delivered(mode, bytes, high, CleanSnrDb, Seeds),8} | "
                    + $"{WanderRig.Delivered(mode, bytes, single, CleanSnrDb, Seeds),9} | "
                    + $"{WanderRig.Delivered(mode, bytes, full, 25, Seeds),9} | "
                    + $"{WanderRig.Delivered(mode, bytes, AudioPath.Flat, 25, Seeds),9}");
            }
        }
    }
}

/// <summary>
/// Bench probe (same gate): how much of the burst each path actually changes. A path that still
/// matches the transmitted waveform almost perfectly and yet stops the frame is doing its damage
/// through a slow baseline shift rather than through gross distortion; the residual column is
/// that shift, as a fraction of the waveform's own rms.
/// </summary>
public class C4fskPathShapeProbe(ITestOutputHelper output)
{
    [Fact]
    public void What_Each_Path_Does_To_One_Burst()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(C4fskBaselineWanderProbe.Gate) is null, "bench probe only");

        (string Name, AudioPath Path)[] paths =
        [
            ("none", AudioPath.Flat),
            ("delay only", AudioPath.PureDelay()),
            ("hp 30 Hz", AudioPath.HighPass(30)),
            ("hp 50 Hz", AudioPath.HighPass(50)),
            ("hp 70 Hz", AudioPath.HighPass(70)),
            ("hp 150 Hz", AudioPath.HighPass(150)),
            ("measured full", AudioPath.Measured("full", low: true, high: true)),
            ("measured low", AudioPath.Measured("low", low: true, high: false)),
            ("measured top", AudioPath.Measured("top", low: false, high: true)),
            ("measured 1-section", AudioPath.MeasuredSinglePole()),
        ];

        output.WriteLine(
            "One 60-byte burst, TXDELAY 50 ms. 'residual' is the rms of what is left once the "
            + "best-matching delay is taken out, as a fraction of the burst's own rms; 'delivered' "
            + "is over seeds 1..3 at 60 dB AWGN.");
        output.WriteLine(string.Empty);
        output.WriteLine("mode        path                 rms   residual   lag   delivered");

        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200", "fsk9600" })
        {
            float[] clean = WanderRig.Make(mode, _ => { }).Modulate(WanderRig.Frame(60, 1), 50);
            double cleanRms = Math.Sqrt(clean.Sum(s => (double)s * s) / clean.Length);
            foreach ((string name, AudioPath path) in paths)
            {
                float[] through = path.Apply(clean, WanderRig.Rate);
                double rms = Math.Sqrt(through.Sum(s => (double)s * s) / clean.Length);
                (int lag, double correlation) = BestMatch(clean, through);
                double residual = Math.Sqrt(Math.Max(0, 1 - (correlation * correlation)));
                output.WriteLine(
                    $"{mode,-11} {name,-18} {rms / cleanRms,5:0.000}  {residual,8:0.000}  {lag,4}   "
                    + $"{WanderRig.Delivered(mode, 60, path, 60, 3)}/3");
            }
        }
    }

    /// <summary>The highest normalised correlation between the two, over integer lags.</summary>
    private static (int Lag, double Correlation) BestMatch(float[] a, float[] b)
    {
        int best = 0;
        double bestValue = -1;
        for (int lag = 0; lag <= 300; lag++)
        {
            double dot = 0;
            double aa = 0;
            double bb = 0;
            for (int i = 0; i + lag < a.Length; i++)
            {
                dot += a[i] * (double)b[i + lag];
                aa += a[i] * (double)a[i];
                bb += b[i + lag] * (double)b[i + lag];
            }

            double value = dot / Math.Sqrt(aa * bb);
            if (value > bestValue)
            {
                bestValue = value;
                best = lag;
            }
        }

        return (best, bestValue);
    }
}

/// <summary>
/// Bench probe (same gate), Part 3: does decision-directed DC restoration recover delivery? The
/// prototype is <see cref="DcRestoringC4fskReceiver"/>, a local copy of the production decision
/// logic; gain 0 is the production behaviour and is printed beside the production modem's own
/// count over the same bursts, which is the copy's check.
/// </summary>
public class C4fskDcRestorationProbe(ITestOutputHelper output)
{
    private static readonly float[] Gains =
        [0f, 0.02f, 0.05f, 0.1f, 0.15f, 0.2f, 0.3f, 0.4f, 0.5f];

    /// <summary>The two loop variants: the plain residual, and the residual clamped to half a
    /// level spacing before it is accumulated.</summary>
    private static readonly (string Name, float Clamp)[] Variants =
        [("plain", float.PositiveInfinity), ("error clamped to 1/3", 1f / 3f)];

    [Fact]
    public void Loop_Gain_Sweep_At_The_Decisive_Corners()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(C4fskBaselineWanderProbe.Gate) is null, "bench probe only");

        double clean = C4fskBaselineWanderProbe.CleanSnrDb;
        AudioPath measured = AudioPath.Measured("measured", low: true, high: true);
        (string Name, AudioPath Path, double Snr)[] cases =
        [
            ("none", AudioPath.Flat, clean),
            ("hp 50 Hz", AudioPath.HighPass(50), clean),
            ("hp 70 Hz", AudioPath.HighPass(70), clean),
            ("hp 100 Hz", AudioPath.HighPass(100), clean),
            ("hp 200 Hz", AudioPath.HighPass(200), clean),
            ("measured", measured, clean),
            ("measured+25dB", measured, 25),
            ("none+25dB", AudioPath.Flat, 25),
        ];

        int seeds = C4fskBaselineWanderProbe.Seeds;
        output.WriteLine(
            $"Part 3a: decision-directed DC restoration, loop gain swept. TXDELAY "
            + $"{WanderRig.TxDelayMs} ms, seeds 1..{seeds}, delivered out of {seeds}. "
            + "Gain 0.00 is the production decision logic.");

        foreach ((string variant, float clamp) in Variants)
        {
            foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
            {
                foreach (int bytes in C4fskBaselineWanderProbe.FrameSizes)
                {
                    output.WriteLine(string.Empty);
                    output.WriteLine($"{mode} {bytes} bytes, loop {variant}");
                    output.WriteLine(
                        "path            production | "
                        + string.Join(" ", Gains.Select(g => $"{g,5:0.00}")));
                    foreach ((string name, AudioPath path, double snr) in cases)
                    {
                        IEnumerable<string> row = Gains.Select(gain =>
                            $"{WanderRig.DeliveredRestored(mode, bytes, path, snr, seeds, gain, clamp),5}");
                        output.WriteLine(
                            $"{name,-14} {WanderRig.Delivered(mode, bytes, path, snr, seeds),10} | "
                            + string.Join(" ", row));
                    }
                }
            }
        }
    }

    [Fact]
    public void The_Whole_Ladder_With_The_Loop_In()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(C4fskBaselineWanderProbe.Gate) is null, "bench probe only");

        (string Name, float Gain, float Clamp)[] chosen =
        [
            ("plain  0.00", 0f, float.PositiveInfinity),
            ("plain  0.20", 0.2f, float.PositiveInfinity),
            ("clamp  0.20", 0.2f, 1f / 3f),
            ("clamp  0.30", 0.3f, 1f / 3f),
            ("clamp  0.40", 0.4f, 1f / 3f),
        ];
        double clean = C4fskBaselineWanderProbe.CleanSnrDb;
        int seeds = C4fskBaselineWanderProbe.Seeds;
        output.WriteLine(
            $"Part 3b: the Part 1 ladder again, through the local copy. Seeds 1..{seeds}, "
            + $"delivered out of {seeds}. Columns are the single-section high pass's -3 dB point "
            + "in Hz; the 'production' row is the real modem over the same bursts.");

        foreach (string mode in new[] { "c4fsk9600", "c4fsk19200" })
        {
            foreach (int bytes in C4fskBaselineWanderProbe.FrameSizes)
            {
                output.WriteLine(string.Empty);
                output.WriteLine($"{mode} {bytes} bytes");
                output.WriteLine(
                    "loop gain    | " + string.Join(" ", C4fskBaselineWanderProbe.Ladder.Select(
                        c => $"{(c == 0 ? "none" : $"{c:0}"),5}")));
                IEnumerable<string> production = C4fskBaselineWanderProbe.Ladder.Select(corner =>
                    $"{WanderRig.Delivered(mode, bytes, AudioPath.HighPass(corner), clean, seeds),5}");
                output.WriteLine($"production   | {string.Join(" ", production)}");
                foreach ((string name, float gain, float clampAt) in chosen)
                {
                    IEnumerable<string> row = C4fskBaselineWanderProbe.Ladder.Select(corner =>
                        $"{WanderRig.DeliveredRestored(mode, bytes, AudioPath.HighPass(corner), clean, seeds, gain, clampAt),5}");
                    output.WriteLine($"{name,-12} | {string.Join(" ", row)}");
                }
            }
        }
    }
}
