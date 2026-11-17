namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>What the clock-tracked read measured about a burst's sample clock.</summary>
/// <param name="RatePpm">The mean rate difference between the sender's playback clock and this
/// station's capture clock over the payload, in parts per million. Positive means the burst
/// arrived stretched: this station took more samples than the sender played.</param>
/// <param name="WanderMicroseconds">Peak-to-peak timing excursion over the payload symbols once
/// that mean rate is taken out: what a single rate figure does not describe.</param>
/// <param name="DelaySamples">The measured delay of each payload symbol against the channel
/// estimate's own epoch, in samples.</param>
public sealed record OfdmFmClockTrack(
    double RatePpm,
    double WanderMicroseconds,
    IReadOnlyList<double> DelaySamples);

public sealed partial class OfdmFmBurstCodec
{
    /// <summary>
    /// Diagnostic switch for offline tools: whether the clock-tracked read is tried at all.
    /// Process-wide and not for a station.
    /// </summary>
    public static bool ClockTracking { get; set; } = true;

    /// <summary>
    /// Diagnostic switch for offline tools: whether the clock-tracked read refines its channel
    /// estimate from the payload's own decided symbols. Process-wide and not for a station.
    /// </summary>
    public static bool DecisionDirectedChannel { get; set; } = true;

    /// <summary>
    /// Diagnostic switch for offline tools: whether the clock-tracked read weights each carrier's
    /// soft bits by the noise it measured there. Process-wide and not for a station.
    /// </summary>
    public static bool MeasuredNoiseWeighting { get; set; } = true;

    /// <summary>
    /// Diagnostic switch for offline tools: whether a carrier whose measured noise stands well
    /// clear of its neighbours' is weighted by its own figure. Process-wide and not for a station.
    /// </summary>
    public static bool MeasuredSpurWeighting { get; set; } = true;

    /// <summary>Taps either side of the interpolation point in the resampler's windowed sinc.
    /// The occupied band stops at a third of the sample rate, so sixteen is far more than the
    /// interpolation error needs to sit below everything else in the burst.</summary>
    private const int ResamplerHalfTaps = 16;

    /// <summary>
    /// Reads a burst's payload on a sample clock measured from the burst itself, symbol by
    /// symbol, and taken out in the time domain before the transforms.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Measured on the two-Tait bench in November 2026 against the
    /// samples that were actually played: the rate between one CM108B's playback clock and
    /// another's capture clock is not a constant. It sits tens of parts per million either side
    /// of its mean from one burst to the next, and bends inside a burst. A rate error does two
    /// things to an OFDM symbol. It tilts the phase across the band by an amount that grows
    /// through the burst, which a rotation per symbol can undo. And it slides the signal through
    /// the transform window WITHIN each symbol, which smears every carrier into its neighbours
    /// and which no rotation after the transform can undo. The second is what the pilot fits
    /// before this left on the table: the same recordings read with the measured clock taken out
    /// first spent a quarter of the code's correcting power that the plain read did.</para>
    /// <para><b>How.</b> A delay of d samples turns carrier k by 2 pi k d / N, through the origin:
    /// an FM audio path has no carrier offset, so there is one unknown per symbol, not two. It is
    /// measured from every carrier of every symbol, not from the pilots alone: the header's
    /// carriers are known once its CRC has passed, and a payload carrier is known as soon as it
    /// has been decided, which at any signal a dense constellation can be read at is nearly
    /// always right. Three hundred carriers measure a symbol's delay to a few nanoseconds where
    /// ten pilots could not tell it from noise. The symbols are walked in order, each predicted
    /// from the ones before, so the decisions are taken on carriers already turned to within a
    /// fraction of a decision region. The delays then become a curve through the burst, the
    /// audio is resampled along it, and the channel estimate and every symbol are read again
    /// from the resampled audio.</para>
    /// <para><b>The payload CRC still arbitrates.</b> This read is tried first and the older
    /// ones after it, so a burst the older reads would have copied is never lost to this one.
    /// </para>
    /// </remarks>
    /// <param name="audio">The audio the burst is in.</param>
    /// <param name="layout">The payload's layout.</param>
    /// <param name="channel">The payload channel estimate, as taken from the unresampled audio.
    /// </param>
    /// <param name="noise">Noise power per bin on the payload's carriers.</param>
    /// <param name="estimateStart">Where the symbol the channel estimate came from starts.</param>
    /// <param name="headerStart">Where the header symbols start.</param>
    /// <param name="headerKnown">The header's carriers as transmitted, per header symbol.</param>
    /// <param name="acquisitionChannel">The channel estimate the header is read against.</param>
    /// <param name="payloadStart">Where the payload symbols start.</param>
    /// <param name="symbols">How many payload symbols there are.</param>
    /// <param name="carrierBits">Bits on each data carrier of the payload layout.</param>
    /// <param name="tables">Constellation points by bits per carrier.</param>
    /// <param name="weight">Per-carrier soft-bit weights for the read that comes back.</param>
    /// <param name="track">What was measured about the clock.</param>
    /// <param name="state">What this read arrived at, for a second pass to start from.</param>
    private (double Re, double Im)[][]? ClockTracked(
        ReadOnlySpan<float> audio,
        Layout layout,
        (double Re, double Im)[] channel,
        double noise,
        int estimateStart,
        int headerStart,
        (double Re, double Im)[][] headerKnown,
        (double Re, double Im)[] acquisitionChannel,
        int payloadStart,
        int symbols,
        int[] carrierBits,
        Dictionary<int, (float I, float Q)[]> tables,
        out double[] weight,
        out OfdmFmClockTrack? track,
        out TrackedState? state)
    {
        weight = [];
        track = null;
        state = null;
        int period = _parameters.SymbolSamples;
        int end = payloadStart + (symbols * period);
        if (estimateStart < 0 || end > audio.Length || symbols < 1)
        {
            return null;
        }

        // The rate to start from, off the header: its carriers are known, it was sent on the
        // acquisition layout one symbol and more after the preamble that layout's channel was
        // measured from, and BPSK reads a turn of nearly a quarter cycle without ambiguity, so
        // this holds to a clock difference far beyond anything a sound card has.
        int preambleStart = headerStart - period;
        double rate = 0;
        double rateWeight = 0;
        var headerDelay = new double[headerKnown.Length];
        for (int h = 0; h < headerKnown.Length; h++)
        {
            (double Re, double Im)[]? raw = ZeroForced(
                _acquisition, audio, headerStart + (h * period), acquisitionChannel);
            if (raw is null)
            {
                return null;
            }

            headerDelay[h] = MeasureDelay(
                _acquisition, raw, acquisitionChannel, headerKnown[h], null, null,
                rate * (h + 1), iterations: 3);
            rate = ((rate * rateWeight) + headerDelay[h]) / (rateWeight + h + 1);
            rateWeight += h + 1;
        }

        // The knots of the delay curve: (sample index of a symbol's centre, delay there), on the
        // payload channel's own epoch, which is zero at the symbol it was estimated from.
        int centre = _parameters.CyclicPrefix + (_parameters.FftSize / 2);
        var knotAt = new List<double>();
        var knotDelay = new List<double>();
        bool sharedEpoch = estimateStart == preambleStart;
        knotAt.Add(estimateStart + centre);
        knotDelay.Add(0);
        if (sharedEpoch)
        {
            // The payload is on the acquisition layout and its channel is the preamble's, so the
            // header symbols lie between the epoch and the payload and their delays are knots.
            for (int h = 0; h < headerKnown.Length; h++)
            {
                knotAt.Add(headerStart + (h * period) + centre);
                knotDelay.Add(headerDelay[h]);
            }
        }

        // Walk the payload. Each symbol is predicted from the one before and the running rate,
        // decided on carriers already turned by the prediction, and measured against its own
        // decisions; the measurement then corrects the rate the next prediction uses.
        var delay = new double[symbols];
        double previousAt = knotAt[^1];
        double previousDelay = knotDelay[^1];
        for (int s = 0; s < symbols; s++)
        {
            int offset = payloadStart + (s * period);
            (double Re, double Im)[]? raw = ZeroForced(layout, audio, offset, channel);
            if (raw is null)
            {
                return null;
            }

            double at = offset + centre;
            double predicted = previousDelay + (rate * (at - previousAt) / period);
            delay[s] = MeasureDelay(
                layout, raw, channel, null, carrierBits, tables, predicted, iterations: 4, search: true);
            double measuredRate = (delay[s] - previousDelay) * period / (at - previousAt);
            rate = (0.5 * rate) + (0.5 * measuredRate);
            previousAt = at;
            previousDelay = delay[s];
            knotAt.Add(at);
            knotDelay.Add(delay[s]);
        }

        // Resample along the curve, from the estimate symbol to the end of the payload, so that
        // the estimate and every payload symbol are read on one clock. Before the first knot the
        // curve runs back at the rate the first two knots give; past the last it runs on.
        int length = end - estimateStart;
        int first = payloadStart - estimateStart;
        float[] resampled = ResampleAlong(audio, estimateStart, length, knotAt, knotDelay);
        (double Re, double Im)[]? retrained = ChannelEstimate(layout, resampled, 0, noise);
        if (retrained is null)
        {
            return null;
        }

        var read = new (double Re, double Im)[symbols][];
        if (!ReadResampled(layout, resampled, first, retrained, carrierBits, tables, read, delay, accumulate: true))
        {
            return null;
        }

        // One knot per symbol draws a straight line through each symbol, and the clock does not
        // run in straight lines that long: measured on air it changes rate by hundreds of ppm
        // at instants that fall anywhere, and a symbol with such a corner inside it is smeared by
        // the part of the bend the line cuts off. So the curve is measured again at a quarter of
        // a symbol. Every symbol's carriers have been decided by now, which makes its waveform
        // known: rebuilt from the decisions through the channel estimate, it is a reference the
        // received samples can be compared with stretch by stretch, and over a short stretch a
        // small delay is simply the part of the difference that looks like the reference's own
        // slope. A wrong decision adds a little noise to the reference and nothing else.
        if (SubSymbolTracking)
        {
            var fineAt = new List<double>();
            var fineDelay = new List<double>();
            AddFineKnots(
                layout, resampled, 0, retrained, layout.Reference, null, null, estimateStart,
                knotAt, knotDelay, fineAt, fineDelay);
            for (int s = 0; s < symbols; s++)
            {
                AddFineKnots(
                    layout, resampled, first + (s * period), retrained, null, read[s],
                    (carrierBits, tables), estimateStart, knotAt, knotDelay, fineAt, fineDelay);
            }

            if (fineAt.Count >= 2)
            {
                knotAt = fineAt;
                knotDelay = fineDelay;
                resampled = ResampleAlong(audio, estimateStart, length, knotAt, knotDelay);
                retrained = ChannelEstimate(layout, resampled, 0, noise);
                if (retrained is null)
                {
                    return null;
                }

                for (int s = 0; s < symbols; s++)
                {
                    delay[s] = CurveAt(knotAt, knotDelay, payloadStart + (s * period) + centre);
                }

                if (!ReadResampled(layout, resampled, first, retrained, carrierBits, tables, read, delay, accumulate: true))
                {
                    return null;
                }
            }
        }

        if (DecisionDirectedChannel)
        {
            retrained = RefineChannel(layout, read, retrained, carrierBits, tables);
            if (!ReadResampled(layout, resampled, first, retrained, carrierBits, tables, read, delay, accumulate: false))
            {
                return null;
            }
        }

        weight = MeasuredNoiseWeighting
            ? NoiseWeights(layout, read, retrained, carrierBits, tables)
            : PowerWeights(retrained);

        // What the clock did, for the record: the mean rate over the payload and what a single
        // rate does not describe.
        double meanRate = 0;
        double wander = 0;
        if (symbols >= 2)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int s = 0; s < symbols; s++)
            {
                sx += s;
                sy += delay[s];
                sxx += (double)s * s;
                sxy += s * delay[s];
            }

            double perSymbol = ((symbols * sxy) - (sx * sy)) / ((symbols * sxx) - (sx * sx));
            double intercept = (sy - (perSymbol * sx)) / symbols;
            meanRate = perSymbol / period * 1e6;
            double low = double.MaxValue, high = double.MinValue;
            for (int s = 0; s < symbols; s++)
            {
                double e = delay[s] - intercept - (perSymbol * s);
                low = Math.Min(low, e);
                high = Math.Max(high, e);
            }

            wander = (high - low) / _parameters.SampleRate * 1e6;
        }

        track = new OfdmFmClockTrack(meanRate, wander, delay);
        state = new TrackedState(resampled, knotAt, knotDelay, retrained, estimateStart, first, delay);
        return read;
    }

    /// <summary>
    /// Diagnostic switch for offline tools: whether the clock-tracked read measures the clock
    /// at a quarter of a symbol after measuring it per symbol. Process-wide and not for a station.
    /// </summary>
    public static bool SubSymbolTracking { get; set; } = true;

    /// <summary>Stretches each symbol's useful part is cut into when the clock is measured
    /// inside it.</summary>
    private const int FineStretches = 4;

    /// <summary>The delay curve at a sample index: straight lines between knots, run on at the
    /// end slopes outside them.</summary>
    private static double CurveAt(List<double> knotAt, List<double> knotDelay, double n)
    {
        if (knotAt.Count == 1)
        {
            return knotDelay[0];
        }

        int low = 0;
        int high = knotAt.Count - 2;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (knotAt[mid] <= n)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        double slope = (knotDelay[low + 1] - knotDelay[low]) / (knotAt[low + 1] - knotAt[low]);
        return knotDelay[low] + (slope * (n - knotAt[low]));
    }

    /// <summary>The audio from <paramref name="start"/> for <paramref name="length"/> samples,
    /// each read at its own delay along the curve.</summary>
    private static float[] ResampleAlong(
        ReadOnlySpan<float> audio, int start, int length, List<double> knotAt, List<double> knotDelay)
    {
        var resampled = new float[length];
        int knot = 0;
        for (int i = 0; i < length; i++)
        {
            double n = start + i;
            while (knot < knotAt.Count - 2 && n > knotAt[knot + 1])
            {
                knot++;
            }

            double d = knotDelay[knot];
            if (knotAt.Count > 1)
            {
                double slope = (knotDelay[knot + 1] - knotDelay[knot]) / (knotAt[knot + 1] - knotAt[knot]);
                d += slope * (n - knotAt[knot]);
            }

            resampled[i] = Interpolate(audio, n + d);
        }

        return resampled;
    }

    /// <summary>
    /// Reads every payload symbol from resampled audio: zero forced, with what the curve left of
    /// each symbol's delay measured against its own decisions and turned out. With
    /// <paramref name="accumulate"/> the residual measured here is added to each symbol's entry
    /// in <paramref name="delay"/>, so the record is of the clock and not of the curve's
    /// approximation to it.
    /// </summary>
    private bool ReadResampled(
        Layout layout,
        float[] resampled,
        int first,
        (double Re, double Im)[] channel,
        int[] carrierBits,
        Dictionary<int, (float I, float Q)[]> tables,
        (double Re, double Im)[][] read,
        double[] delay,
        bool accumulate,
        (double Re, double Im)[][]? known = null)
    {
        for (int s = 0; s < read.Length; s++)
        {
            (double Re, double Im)[]? raw = ZeroForced(
                layout, resampled, first + (s * _parameters.SymbolSamples), channel);
            if (raw is null)
            {
                return false;
            }

            double residual = MeasureDelay(
                layout, raw, channel, known?[s], carrierBits, tables, 0, iterations: 2);
            Turn(layout, raw, residual);
            read[s] = raw;
            if (accumulate)
            {
                delay[s] += residual;
            }
        }

        return true;
    }

    /// <summary>
    /// Measures the clock inside one symbol of resampled audio, a quarter of the symbol at a
    /// time, and adds a knot per quarter to the fine curve.
    /// </summary>
    /// <param name="layout">The layout the symbol is on.</param>
    /// <param name="resampled">Audio already resampled along the per-symbol curve.</param>
    /// <param name="offset">Where the symbol starts in it.</param>
    /// <param name="channel">The channel estimate taken from that audio.</param>
    /// <param name="known">The symbol's carriers when they are known outright (the estimate
    /// symbol); otherwise null and <paramref name="equalised"/> is decided.</param>
    /// <param name="equalised">The symbol's equalised carriers, to decide from.</param>
    /// <param name="deciding">Bits per data carrier and the constellation tables, to decide with.
    /// </param>
    /// <param name="origin">The sample index in the original audio that
    /// <paramref name="resampled"/> starts at.</param>
    /// <param name="knotAt">The per-symbol curve's knots, which the fine knots are offsets from.
    /// </param>
    /// <param name="knotDelay">And their delays.</param>
    /// <param name="fineAt">The fine curve's knots, appended to.</param>
    /// <param name="fineDelay">And their delays.</param>
    private void AddFineKnots(
        Layout layout,
        float[] resampled,
        int offset,
        (double Re, double Im)[] channel,
        (double Re, double Im)[]? known,
        (double Re, double Im)[]? equalised,
        (int[] CarrierBits, Dictionary<int, (float I, float Q)[]> Tables)? deciding,
        int origin,
        List<double> knotAt,
        List<double> knotDelay,
        List<double> fineAt,
        List<double> fineDelay)
    {
        int fft = _parameters.FftSize;
        int prefix = _parameters.CyclicPrefix;
        if (offset < 0 || offset + prefix + fft > resampled.Length)
        {
            return;
        }

        // The symbol as it should have arrived, and its slope: the decided carriers through the
        // channel estimate, transformed back, once as they are and once multiplied by each
        // carrier's own rate of turning.
        var binRe = new double[fft];
        var binIm = new double[fft];
        var slopeRe = new double[fft];
        var slopeIm = new double[fft];
        double perBin = 2 * Math.PI / fft;
        int data = 0;
        for (int c = 0; c < layout.TotalCarriers; c++)
        {
            double pointRe;
            double pointIm;
            if (known is not null)
            {
                (pointRe, pointIm) = known[c];
            }
            else if (layout.PilotMap[c])
            {
                (pointRe, pointIm) = (layout.Reference[c].Re, 0);
            }
            else
            {
                (float I, float Q)[] table = deciding!.Value.Tables[deciding.Value.CarrierBits[data++]];
                (float I, float Q) point = table[OfdmFmMapper.Demap(
                    table, (float)equalised![c].Re, (float)equalised[c].Im)];
                (pointRe, pointIm) = (point.I, point.Q);
            }

            int bin = layout.FirstCarrier + c;
            double re = (channel[c].Re * pointRe) - (channel[c].Im * pointIm);
            double im = (channel[c].Re * pointIm) + (channel[c].Im * pointRe);
            binRe[bin] = re;
            binIm[bin] = im;
            slopeRe[bin] = -perBin * bin * im;
            slopeIm[bin] = perBin * bin * re;
        }

        double[] reference = RealFft.ToTimeDomain(binRe, binIm, fft);
        double[] slope = RealFft.ToTimeDomain(slopeRe, slopeIm, fft);

        // The transform pair's own scale, measured rather than assumed: the reference is fitted
        // to the received samples in amplitude before the two are compared.
        double cross = 0;
        double power = 0;
        for (int n = 0; n < fft; n++)
        {
            cross += resampled[offset + prefix + n] * reference[n];
            power += reference[n] * reference[n];
        }

        if (power <= 0)
        {
            return;
        }

        double gain = cross / power;
        int stretch = fft / FineStretches;
        for (int b = 0; b < FineStretches; b++)
        {
            double numerator = 0;
            double denominator = 0;
            for (int n = b * stretch; n < (b + 1) * stretch; n++)
            {
                double expected = gain * reference[n];
                double tangent = gain * slope[n];
                numerator += (resampled[offset + prefix + n] - expected) * tangent;
                denominator += tangent * tangent;
            }

            if (denominator <= 0)
            {
                continue;
            }

            // Received = reference delayed by d is, to first order, reference less d times its
            // slope: so d is minus the part of the difference that lies along the slope.
            double fine = -numerator / denominator;
            double at = origin + offset + prefix + ((b + 0.5) * stretch);
            fineAt.Add(at);
            fineDelay.Add(CurveAt(knotAt, knotDelay, at) + fine);
        }
    }

    /// <summary>What the clock-tracked read arrived at, kept so that a second pass can start
    /// from it and not from the raw audio.</summary>
    private sealed record TrackedState(
        float[] Resampled,
        List<double> KnotAt,
        List<double> KnotDelay,
        (double Re, double Im)[] Channel,
        int EstimateStart,
        int First,
        double[] Delay);

    /// <summary>
    /// Diagnostic switch for offline tools: whether a clock-tracked read that fails its CRC is
    /// read once more against what the decoder made of it. Process-wide and not for a station.
    /// </summary>
    public static bool DecodeFeedback { get; set; } = true;

    /// <summary>
    /// The clock-tracked read, taken again against the decoder's own output.
    /// </summary>
    /// <remarks>
    /// A decode that fails its CRC is still mostly right: the code corrected most of what the
    /// demapper got wrong before it ran out. Re-encoded, its output names the point on every
    /// carrier of every symbol with far fewer mistakes than deciding each carrier alone did, and
    /// everything the tracked read measured from decisions can be measured again from that: the
    /// clock inside each symbol, the channel from every symbol and not only the estimate symbol,
    /// and the noise on each carrier without a wrong decision flattering it. The payload CRC
    /// arbitrates as it does for every other reading.
    /// </remarks>
    private (double Re, double Im)[][]? Polished(
        ReadOnlySpan<float> audio,
        Layout layout,
        double noise,
        TrackedState state,
        (double Re, double Im)[][] points,
        int[] carrierBits,
        Dictionary<int, (float I, float Q)[]> tables,
        out double[] weight)
    {
        weight = [];
        int period = _parameters.SymbolSamples;
        int symbols = points.Length;
        var read = new (double Re, double Im)[symbols][];
        var delay = new double[symbols];
        if (!ReadResampled(
            layout, state.Resampled, state.First, state.Channel, carrierBits, tables, read, delay,
            accumulate: false, points))
        {
            return null;
        }

        float[] resampled = state.Resampled;
        (double Re, double Im)[] channel = state.Channel;
        if (SubSymbolTracking)
        {
            var fineAt = new List<double>();
            var fineDelay = new List<double>();
            AddFineKnots(
                layout, resampled, 0, channel, layout.Reference, null, null, state.EstimateStart,
                state.KnotAt, state.KnotDelay, fineAt, fineDelay);
            for (int s = 0; s < symbols; s++)
            {
                AddFineKnots(
                    layout, resampled, state.First + (s * period), channel, points[s], null, null,
                    state.EstimateStart, state.KnotAt, state.KnotDelay, fineAt, fineDelay);
            }

            if (fineAt.Count >= 2)
            {
                resampled = ResampleAlong(audio, state.EstimateStart, resampled.Length, fineAt, fineDelay);
                (double Re, double Im)[]? again = ChannelEstimate(layout, resampled, 0, noise);
                if (again is null)
                {
                    return null;
                }

                channel = again;
                if (!ReadResampled(
                    layout, resampled, state.First, channel, carrierBits, tables, read, delay,
                    accumulate: false, points))
                {
                    return null;
                }
            }
        }

        channel = RefineChannel(layout, read, channel, carrierBits, tables, points);
        if (!ReadResampled(
            layout, resampled, state.First, channel, carrierBits, tables, read, delay,
            accumulate: false, points))
        {
            return null;
        }

        weight = MeasuredNoiseWeighting
            ? NoiseWeights(layout, read, channel, carrierBits, tables, points)
            : PowerWeights(channel);
        return read;
    }

    /// <summary>One symbol's occupied carriers divided by the channel estimate, and nothing else:
    /// no pilot rotation, because the clock-tracked read measures the symbol's delay from every
    /// carrier and a rotation from ten pilots would only add their noise to it.</summary>
    private (double Re, double Im)[]? ZeroForced(
        Layout layout, ReadOnlySpan<float> audio, int offset, (double Re, double Im)[] channel)
    {
        if (offset < 0 || offset + _parameters.SymbolSamples > audio.Length)
        {
            return null;
        }

        (double[] re, double[] im) = SymbolBins(audio, offset);
        var carriers = new (double Re, double Im)[layout.TotalCarriers];
        for (int c = 0; c < carriers.Length; c++)
        {
            int bin = layout.FirstCarrier + c;
            (double hRe, double hIm) = channel[c];
            double power = (hRe * hRe) + (hIm * hIm);
            carriers[c] = power < 1e-20
                ? (0, 0)
                : (((re[bin] * hRe) + (im[bin] * hIm)) / power, ((im[bin] * hRe) - (re[bin] * hIm)) / power);
        }

        return carriers;
    }

    /// <summary>
    /// The delay, in samples, that best explains how one symbol's carriers are turned against
    /// what they carried: a one-parameter fit through bin zero, weighted by how much each carrier
    /// can be trusted.
    /// </summary>
    /// <param name="layout">The layout the carriers are on.</param>
    /// <param name="raw">The symbol's zero-forced carriers, not rotated.</param>
    /// <param name="channel">The channel estimate, for the weights.</param>
    /// <param name="known">What each carrier carried, when that is known; otherwise null and the
    /// pilots are read against the reference and the data carriers against their own nearest
    /// constellation point.</param>
    /// <param name="carrierBits">Bits on each data carrier, when deciding.</param>
    /// <param name="tables">Constellation points by bits per carrier, when deciding.</param>
    /// <param name="start">The delay to start from.</param>
    /// <param name="iterations">Passes: each re-decides on carriers turned by the last answer.
    /// </param>
    /// <param name="search">Whether to search for the starting point first, for a symbol whose
    /// delay may be far from the prediction.</param>
    private double MeasureDelay(
        Layout layout,
        (double Re, double Im)[] raw,
        (double Re, double Im)[] channel,
        (double Re, double Im)[]? known,
        int[]? carrierBits,
        Dictionary<int, (float I, float Q)[]>? tables,
        double start,
        int iterations,
        bool search = false)
    {
        double perBin = 2 * Math.PI / _parameters.FftSize;
        double delay = start;

        // A decision is only worth measuring against when the carrier is already turned to within
        // its own decision region, and a clock that has just changed rate leaves the top of the
        // band a long way outside it: the carriers decide wrongly, each wrong decision reads as a
        // small turn about the wrong point, and the fit comes back saying the prediction was
        // right. Measured on air: the CM108B's clock changes rate by several hundred parts per
        // million inside a burst, half a sample and more from one symbol to the next, and a fit
        // started from the prediction alone followed a third of it. So the starting point is
        // searched for first: over a couple of samples either side of the prediction, the delay
        // whose turned carriers sit closest to constellation points, all carriers voting. Wrong
        // by a quarter of a sample the top carriers land anywhere in their cells and the distance
        // is several times what it is when right, so the minimum is not subtle.
        if (known is null && search)
        {
            double best = double.MaxValue;
            double bestDelay = start;
            for (int step = -SearchSteps; step <= SearchSteps; step++)
            {
                double candidate = start + (step * SearchStepSamples);
                double total = 0;
                int data = 0;
                for (int c = 0; c < raw.Length; c++)
                {
                    int bin = layout.FirstCarrier + c;
                    double angle = perBin * bin * candidate;
                    double cos = Math.Cos(angle);
                    double sin = Math.Sin(angle);
                    double re = (raw[c].Re * cos) - (raw[c].Im * sin);
                    double im = (raw[c].Re * sin) + (raw[c].Im * cos);
                    double pointRe;
                    double pointIm;
                    if (layout.PilotMap[c])
                    {
                        (pointRe, pointIm) = (layout.Reference[c].Re, 0);
                    }
                    else
                    {
                        (float I, float Q)[] table = tables![carrierBits![data++]];
                        (float I, float Q) point = table[OfdmFmMapper.Demap(table, (float)re, (float)im)];
                        (pointRe, pointIm) = (point.I, point.Q);
                    }

                    double w = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
                    total += w * (((re - pointRe) * (re - pointRe)) + ((im - pointIm) * (im - pointIm)));
                }

                if (total < best)
                {
                    best = total;
                    bestDelay = candidate;
                }
            }

            delay = bestDelay;
        }

        for (int pass = 0; pass < iterations; pass++)
        {
            // The fit widens as it goes: the bottom of the band turns least for a given delay, so
            // it is believed first, and each pass doubles how far up the band is trusted once the
            // pass before has turned it.
            int limit = (int)Math.Min(raw.Length, (long)raw.Length << pass >> (iterations - 1));
            limit = Math.Max(limit, Math.Min(raw.Length, 16));
            double numerator = 0;
            double denominator = 0;
            int data = 0;
            for (int c = 0; c < limit; c++)
            {
                int bin = layout.FirstCarrier + c;
                double angle = perBin * bin * delay;
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);
                double re = (raw[c].Re * cos) - (raw[c].Im * sin);
                double im = (raw[c].Re * sin) + (raw[c].Im * cos);
                double pointRe;
                double pointIm;
                if (known is not null)
                {
                    (pointRe, pointIm) = known[c];
                }
                else if (layout.PilotMap[c])
                {
                    (pointRe, pointIm) = (layout.Reference[c].Re, 0);
                }
                else
                {
                    (float I, float Q)[] table = tables![carrierBits![data++]];
                    (float I, float Q) point = table[OfdmFmMapper.Demap(table, (float)re, (float)im)];
                    (pointRe, pointIm) = (point.I, point.Q);
                }

                // The carrier against its point: the real part is how much of it agrees, the
                // imaginary part how far it is turned. A carrier that disagrees outright says
                // nothing about a small turn and is left out.
                double agree = (re * pointRe) + (im * pointIm);
                double turned = (im * pointRe) - (re * pointIm);
                if (agree <= 0)
                {
                    continue;
                }

                double theta = Math.Atan2(turned, agree);
                double pointPower = (pointRe * pointRe) + (pointIm * pointIm);
                double w = ((channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im)) * pointPower;
                numerator += w * bin * theta;
                denominator += w * (double)bin * bin;
            }

            if (denominator <= 0)
            {
                break;
            }

            delay -= numerator / denominator / perBin;
        }

        return delay;
    }

    /// <summary>Steps either side of the prediction the delay search tries.</summary>
    private const int SearchSteps = 10;

    /// <summary>The search's step, in samples: fine enough that the fit which follows starts
    /// inside every carrier's decision region.</summary>
    private const double SearchStepSamples = 0.2;

    /// <summary>Turns a symbol's carriers to take a delay of so many samples out.</summary>
    private void Turn(Layout layout, (double Re, double Im)[] carriers, double delay)
    {
        double perBin = 2 * Math.PI / _parameters.FftSize;
        for (int c = 0; c < carriers.Length; c++)
        {
            double angle = perBin * (layout.FirstCarrier + c) * delay;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            (double re, double im) = carriers[c];
            carriers[c] = ((re * cos) - (im * sin), (re * sin) + (im * cos));
        }
    }

    /// <summary>The audio at a fractional sample index, by a Hann-windowed sinc.</summary>
    private static float Interpolate(ReadOnlySpan<float> audio, double at)
    {
        int whole = (int)Math.Floor(at);
        double fraction = at - whole;
        double sum = 0;
        for (int k = -ResamplerHalfTaps + 1; k <= ResamplerHalfTaps; k++)
        {
            int index = whole + k;
            if (index < 0 || index >= audio.Length)
            {
                continue;
            }

            double x = k - fraction;
            double window = 0.5 + (0.5 * Math.Cos(Math.PI * x / ResamplerHalfTaps));
            double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            sum += audio[index] * sinc * window;
        }

        return (float)sum;
    }

    /// <summary>
    /// A channel estimate refined from the payload itself: every carrier of every payload symbol
    /// against the point it was decided to carry, averaged, and smoothed across neighbouring
    /// carriers.
    /// </summary>
    /// <remarks>
    /// The estimate symbol is one look at the channel and carries one symbol's noise, which
    /// zero forcing then adds to every payload symbol. The payload is several more looks at the
    /// same channel for nothing. A wrong decision pulls its carrier's average the wrong way, so
    /// the correction is a ratio near one that is averaged over the symbols and then over a run
    /// of carriers, where the channel is smooth and the decision errors are not.
    /// </remarks>
    private static (double Re, double Im)[] RefineChannel(
        Layout layout,
        (double Re, double Im)[][] read,
        (double Re, double Im)[] channel,
        int[] carrierBits,
        Dictionary<int, (float I, float Q)[]> tables,
        (double Re, double Im)[][]? known = null)
    {
        int n = channel.Length;
        var ratioRe = new double[n];
        var ratioIm = new double[n];
        var power = new double[n];
        for (int s = 0; s < read.Length; s++)
        {
            (double Re, double Im)[] symbol = read[s];
            int data = 0;
            for (int c = 0; c < n; c++)
            {
                double pointRe;
                double pointIm;
                if (known is not null)
                {
                    (pointRe, pointIm) = known[s][c];
                }
                else if (layout.PilotMap[c])
                {
                    (pointRe, pointIm) = (layout.Reference[c].Re, 0);
                }
                else
                {
                    (float I, float Q)[] table = tables[carrierBits[data++]];
                    (float I, float Q) point = table[OfdmFmMapper.Demap(table, (float)symbol[c].Re, (float)symbol[c].Im)];
                    (pointRe, pointIm) = (point.I, point.Q);
                }

                ratioRe[c] += (symbol[c].Re * pointRe) + (symbol[c].Im * pointIm);
                ratioIm[c] += (symbol[c].Im * pointRe) - (symbol[c].Re * pointIm);
                power[c] += (pointRe * pointRe) + (pointIm * pointIm);
            }
        }

        // The estimate symbol counts as one more look, at a ratio of exactly one, so a carrier
        // whose decisions were poor is pulled back toward what the estimate said.
        const int HalfSpan = 4;
        var refined = new (double Re, double Im)[n];
        for (int c = 0; c < n; c++)
        {
            double re = 0;
            double im = 0;
            double total = 0;
            for (int k = Math.Max(0, c - HalfSpan); k <= Math.Min(n - 1, c + HalfSpan); k++)
            {
                re += ratioRe[k] + 1;
                im += ratioIm[k];
                total += power[k] + 1;
            }

            re /= total;
            im /= total;
            refined[c] = ((channel[c].Re * re) - (channel[c].Im * im), (channel[c].Re * im) + (channel[c].Im * re));
        }

        return refined;
    }

    /// <summary>Soft-bit weights from the channel estimate's power alone, as the older reads
    /// use: a carrier's noise after zero forcing is the channel's noise over its gain.</summary>
    private static double[] PowerWeights((double Re, double Im)[] channel)
    {
        var weight = new double[channel.Length];
        double mean = 0;
        for (int c = 0; c < channel.Length; c++)
        {
            weight[c] = (channel[c].Re * channel[c].Re) + (channel[c].Im * channel[c].Im);
            mean += weight[c];
        }

        mean /= Math.Max(1, channel.Length);
        for (int c = 0; c < weight.Length; c++)
        {
            weight[c] = mean > 0 ? weight[c] / mean : 1.0;
        }

        return weight;
    }

    /// <summary>
    /// Soft-bit weights from the noise each carrier actually showed: the mean squared distance
    /// between its equalised points and the points they were decided to be, over the payload's
    /// symbols and a run of neighbouring carriers, inverted.
    /// </summary>
    /// <remarks>
    /// On an FM path the noise is not flat and neither is the distortion: both rise with audio
    /// frequency, by more than ten decibels across an 8 kHz span on the bench this was measured
    /// on, while the channel's gain falls by only a few. Weights from the gain alone therefore
    /// tell the decoder the top of the band is nearly as trustworthy as the bottom. This measures
    /// what is there instead, everything included, from the burst being read. A wrong decision
    /// understates its own error, which makes the worst carriers look a little better than they
    /// are; the run of neighbours and the cap on the ratio between best and worst keep that from
    /// mattering.
    /// </remarks>
    private static double[] NoiseWeights(
        Layout layout,
        (double Re, double Im)[][] read,
        (double Re, double Im)[] channel,
        int[] carrierBits,
        Dictionary<int, (float I, float Q)[]> tables,
        (double Re, double Im)[][]? known = null)
    {
        int n = channel.Length;
        var error = new double[n];
        for (int s = 0; s < read.Length; s++)
        {
            (double Re, double Im)[] symbol = read[s];
            int data = 0;
            for (int c = 0; c < n; c++)
            {
                double pointRe;
                double pointIm;
                if (known is not null)
                {
                    (pointRe, pointIm) = known[s][c];
                }
                else if (layout.PilotMap[c])
                {
                    (pointRe, pointIm) = (layout.Reference[c].Re, 0);
                }
                else
                {
                    (float I, float Q)[] table = tables[carrierBits[data++]];
                    (float I, float Q) point = table[OfdmFmMapper.Demap(table, (float)symbol[c].Re, (float)symbol[c].Im)];
                    (pointRe, pointIm) = (point.I, point.Q);
                }

                double dRe = symbol[c].Re - pointRe;
                double dIm = symbol[c].Im - pointIm;
                error[c] += (dRe * dRe) + (dIm * dIm);
            }
        }

        // The level across the band is the median over a run of neighbours, not the mean: a
        // receiver's own spur sits on one or two carriers (the bench's second radio has one at
        // 3.66 kHz, 15 dB over its noise), a mean smears it over the whole run so that the carrier
        // under it is trusted far too much and its neighbours too little, and a median does not
        // see it at all. The carrier that stands well clear of its neighbours' level is then
        // taken at its own figure. Few symbols make one carrier's own figure a rough one, so it
        // has to stand clear by a wide margin before it is believed over the run.
        const int HalfSpan = 8;
        const double StandsClear = 4.0;
        var weight = new double[n];
        var run = new double[(2 * HalfSpan) + 1];
        double mean = 0;
        for (int c = 0; c < n; c++)
        {
            int from = Math.Max(0, c - HalfSpan);
            int to = Math.Min(n - 1, c + HalfSpan);
            int count = to - from + 1;
            Array.Copy(error, from, run, 0, count);
            Array.Sort(run, 0, count);
            double level = run[count / 2];
            if (MeasuredSpurWeighting && error[c] > StandsClear * level)
            {
                level = error[c];
            }

            weight[c] = 1.0 / Math.Max(level, 1e-12);
            mean += weight[c];
        }

        mean /= n;
        for (int c = 0; c < n; c++)
        {
            weight[c] = Math.Clamp(weight[c] / mean, 0.01, 100);
        }

        return weight;
    }
}
