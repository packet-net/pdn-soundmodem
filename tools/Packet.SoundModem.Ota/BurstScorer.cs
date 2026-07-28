using System.Globalization;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Ota;

/// <summary>What one burst in a capture did.</summary>
/// <param name="Index">Position in the capture, from 0 — the order bursts were detected in.</param>
/// <param name="StartSeconds">Where in the capture the burst's first data block appeared.</param>
/// <param name="EndSeconds">Where the burst ended.</param>
/// <param name="Acquired">Whether the demodulator locked at all.</param>
/// <param name="WaveformNumber">Waveform the WID announced, if it locked.</param>
/// <param name="Interleaver">Interleaver the WID announced.</param>
/// <param name="CfoHz">Carrier frequency offset at lock.</param>
/// <param name="Scheduled">Whether this burst matched a schedule entry. If not, everything
/// below is unscored — there is nothing to score it against.</param>
/// <param name="WidCorrect">Whether the WID matched what was scheduled.</param>
/// <param name="PayloadBits">Payload bits expected.</param>
/// <param name="PayloadErrors">Payload bits wrong, missing ones included.</param>
/// <param name="UncodedBits">Channel bits compared against the re-encoded reference —
/// erasures (exactly-zero LLRs, where the demodulator expressed no opinion) excluded.</param>
/// <param name="UncodedErrors">Channel bits wrong — the informative rate.</param>
/// <param name="Blocks">Blocks the demodulator decoded.</param>
/// <param name="Reason">Which D.5.4.5 exit ended the burst.</param>
/// <param name="TurboConverged">Turbo passes that converged during this burst.</param>
/// <param name="TurboReverted">Turbo passes reverted during this burst.</param>
/// <param name="TurboAborted">Turbo passes aborted during this burst.</param>
/// <param name="Snr">SNR of the burst against the quiet before it, in the rig's convention.</param>
/// <param name="Diagnostics">The demodulator's <see cref="Ms110dDemodulator.FrameDiagnostics"/>
/// lines emitted while this burst was live — the acquisition/WID trace, time-prefixed. Empty
/// unless <see cref="BurstScorerOptions.CaptureDiagnostics"/> is set; the event is
/// fire-and-forget, so collecting it cannot change what the demodulator decoded.</param>
public sealed record BurstScore(
    int Index,
    double StartSeconds,
    double EndSeconds,
    bool Acquired,
    int? WaveformNumber,
    Ms110dInterleaverKind? Interleaver,
    double CfoHz,
    bool Scheduled,
    bool WidCorrect,
    long PayloadBits,
    long PayloadErrors,
    long UncodedBits,
    long UncodedErrors,
    int Blocks,
    Ms110dBurstEndReason? Reason,
    int TurboConverged,
    int TurboReverted,
    int TurboAborted,
    SnrEstimate? Snr,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>Coded bit error rate. It saturates at 0 and at 1, so read it beside
    /// <see cref="UncodedBer"/> rather than on its own.</summary>
    public double CodedBer => PayloadBits == 0 ? double.NaN : PayloadErrors / (double)PayloadBits;

    /// <summary>Uncoded channel-bit error rate — the number that degrades smoothly and is
    /// directly comparable with the Watterson rig's own telemetry.</summary>
    public double UncodedBer => UncodedBits == 0 ? double.NaN : UncodedErrors / (double)UncodedBits;
}

/// <summary>One burst the schedule says should be in the capture.</summary>
/// <param name="Reference">The bits that went out.</param>
/// <param name="ExpectedSeconds">Where it was transmitted, for matching and for spotting a burst
/// that was missed entirely. Negative means "match by order alone".</param>
public sealed record ScheduledBurst(Ms110dReferenceBits Reference, double ExpectedSeconds = -1);

/// <summary>Everything a capture produced.</summary>
/// <param name="Bursts">One entry per burst the demodulator found, in order.</param>
/// <param name="Missed">Scheduled bursts nothing was detected for — a headline metric, not an
/// error condition.</param>
/// <param name="AudioSeconds">Length of the audio scored.</param>
public sealed record CaptureScore(
    IReadOnlyList<BurstScore> Bursts, IReadOnlyList<ScheduledBurst> Missed, double AudioSeconds);

/// <summary>Knobs for <see cref="BurstScorer"/>.</summary>
public sealed record BurstScorerOptions
{
    /// <summary>Audio rate of the capture. MS110D is native 9600 Hz.</summary>
    public int SampleRate { get; init; } = 9600;

    /// <summary>How far a detected burst may sit from its scheduled time and still be its
    /// match. Wide enough to absorb capture-start jitter, narrow enough that two bursts cannot
    /// both claim one slot.</summary>
    public double MatchWindowSeconds { get; init; } = 5.0;

    /// <summary>Quiet audio used for the noise estimate, per burst.</summary>
    public double NoiseWindowSeconds { get; init; } = 2.0;

    /// <summary>Gap left between the noise window and the burst. <b>This must exceed the
    /// preamble length</b>, or the "noise" measured will be preamble and every SNR will read
    /// high. 20 super-frames is 4.8 s; the OTA default of 3 is 0.72 s.</summary>
    public double NoiseLeadSeconds { get; init; } = 2.0;

    /// <summary>Granularity the demodulator is fed at, which sets the time resolution of
    /// <see cref="BurstScore.StartSeconds"/>. Independent of how the caller blocks its
    /// audio, so a scored capture does not change shape with the reader's buffer size.</summary>
    public double ChunkSeconds { get; init; } = 0.1;

    /// <summary>Demodulator options, so a scored pass can be run against a knob under test.</summary>
    public Ms110dDemodOptions? Demodulator { get; init; }

    /// <summary>
    /// The band the converted audio actually occupies — the receive converter's SSB passband.
    /// </summary>
    /// <remarks>Must match whatever <c>IqToAudioOptions.SsbLowHz</c>/<c>SsbHighHz</c> the capture
    /// was converted with. The noise in a converted pass lives only inside that passband, and an
    /// estimator that assumes it fills the whole audio band over-subtracts noise from the burst —
    /// harmless at high SNR, 2.6 dB out at 0 dB. See <see cref="SnrEstimator.Estimate"/>.</remarks>
    public double OccupiedLowHz { get; init; } = 150;

    /// <summary>Upper edge of the occupied band.</summary>
    public double OccupiedHighHz { get; init; } = 3450;

    /// <summary>Collect the demodulator's <see cref="Ms110dDemodulator.FrameDiagnostics"/> trace
    /// per burst into <see cref="BurstScore.Diagnostics"/>. Off by default — this is an acquisition
    /// autopsy tool, not part of scoring, and the event is fire-and-forget so subscribing to it
    /// leaves the decoded bits byte-identical.</summary>
    public bool CaptureDiagnostics { get; init; }
}

/// <summary>
/// Streams a whole capture through one demodulator and scores every burst it finds.
/// </summary>
/// <remarks>
/// <para><b>Bursts are found by acquisition, not by the clock.</b> Slicing a capture at the
/// scheduled times and demodulating each slice would conceal the failure that matters most — a
/// burst the receiver never acquired — because it hands the demodulator a window already known
/// to contain a signal. Here the demodulator sweeps the whole capture as a real receiver would,
/// and a scheduled burst with nothing detected near it comes back in
/// <see cref="CaptureScore.Missed"/>. Time is used only to match what was found against what
/// was sent, and to notice when the two disagree.</para>
/// <para>One demodulator for the whole capture, not one per burst: it returns to
/// <c>Searching</c> after each burst and re-acquires, and that is the only version of the
/// question a real receiver ever faces. Turbo counters are cumulative, so they are differenced
/// per burst.</para>
/// <para>SNR comes from a quiet stretch shortly before each burst, in the rig's own convention
/// (see <see cref="SnrEstimator"/>), so an over-the-air point can be laid against the
/// simulation's curve at a matched SNR rather than a nominal one. The gap between that stretch
/// and the burst has to clear the preamble — see
/// <see cref="BurstScorerOptions.NoiseLeadSeconds"/>.</para>
/// <para>Memory is bounded by the longest burst, not by the capture: the only audio retained is
/// the current burst and a few seconds of rolling quiet behind it.</para>
/// </remarks>
public sealed class BurstScorer
{
    private readonly BurstScorerOptions _options;
    private readonly IReadOnlyList<ScheduledBurst> _schedule;

    public BurstScorer(IReadOnlyList<ScheduledBurst> schedule, BurstScorerOptions? options = null)
    {
        _schedule = schedule;
        _options = options ?? new BurstScorerOptions();
    }

    /// <summary>Scores a capture supplied as a sequence of audio blocks. How the caller blocks
    /// its audio has no effect on the result.</summary>
    public CaptureScore Score(IEnumerable<ReadOnlyMemory<float>> audioBlocks)
    {
        int rate = _options.SampleRate;
        var demod = new Ms110dDemodulator(_options.Demodulator);
        var found = new List<BurstScore>();
        var matched = new bool[_schedule.Count];
        int scheduleCursor = 0;

        // Rolling quiet behind us: a ring covering NoiseLead + NoiseWindow seconds, of which the
        // OLDEST NoiseWindow is the part far enough back to be clear of the preamble.
        int guardSize = (int)((_options.NoiseLeadSeconds + _options.NoiseWindowSeconds) * rate);
        int noiseSamples = (int)(_options.NoiseWindowSeconds * rate);
        var guard = new float[Math.Max(guardSize, 1024)];
        int guardFill = 0;
        int guardHead = 0;

        var burstAudio = new float[rate * 30];
        int burstFill = 0;

        long consumed = 0;
        long burstStartSample = -1;
        float[]? burstNoise = null;
        Ms110dLockInfo? locked = null;
        Ms110dReferenceBits? reference = null;
        bool scheduled = false;
        long uncodedBits = 0, uncodedErrors = 0;
        int turboC0 = 0, turboR0 = 0, turboA0 = 0;

        // Acquisition/WID autopsy, opt-in. Each FrameDiagnostics line is stamped with the sample
        // it fired at, so a burst later claims the lines that fell inside its span — the
        // acquisition trace fires between carrier-detect and lock, which is inside it. The event
        // is fire-and-forget, so subscribing changes nothing the demodulator decodes.
        List<(long Sample, string Message)>? diagnostics =
            _options.CaptureDiagnostics ? new List<(long, string)>() : null;
        if (diagnostics is not null)
        {
            demod.FrameDiagnostics += message => diagnostics.Add((consumed, message));
        }

        Ms110dReferenceBits? MatchSchedule(double startSeconds)
        {
            // Order first — bursts arrive in the order they were sent. Time is the cross-check:
            // if the nth detection is nowhere near the nth remaining slot, the schedule has
            // slipped, and matching purely by order would score against the wrong reference.
            for (int k = scheduleCursor; k < _schedule.Count; k++)
            {
                if (matched[k])
                {
                    continue;
                }

                ScheduledBurst candidate = _schedule[k];
                if (candidate.ExpectedSeconds < 0
                    || Math.Abs(candidate.ExpectedSeconds - startSeconds) <= _options.MatchWindowSeconds)
                {
                    matched[k] = true;
                    scheduleCursor = k + 1;
                    return candidate.Reference;
                }

                if (candidate.ExpectedSeconds > startSeconds + _options.MatchWindowSeconds)
                {
                    break; // detected earlier than any remaining slot — this one is unscheduled
                }
            }

            return null;
        }

        // Burst start comes from the demodulator's carrier detect, polled per chunk — NOT from
        // the first block event. A block event fires only once the whole block has arrived, so
        // treating it as the start puts the burst's start at its end: measured, that made
        // StartSeconds equal EndSeconds and left no burst audio to estimate SNR from.
        void BeginBurst(long startSample)
        {
            if (burstStartSample >= 0)
            {
                return;
            }

            burstStartSample = startSample;
            burstNoise = OldestOfGuard(guard, guardFill, guardHead, noiseSamples);
            turboC0 = demod.TurboConverged;
            turboR0 = demod.TurboReverted;
            turboA0 = demod.TurboAborted;
            reference = MatchSchedule(burstStartSample / (double)rate);
            scheduled = reference is not null;
        }

        // Lock has to be read while the burst is live: EndBurst clears it, so reading
        // demod.Lock afterwards reports "no acquisition" beside a perfect decode.
        demod.FirstPassBlockLlrs += (blockIndex, llrs) =>
        {
            BeginBurst(consumed);
            locked ??= demod.Lock;

            if (reference is null || blockIndex >= reference.BlockCount)
            {
                return; // unscheduled burst, or a surplus block decoded out of post-EOM noise
            }

            ReadOnlySpan<byte> sent = reference.Block(blockIndex);
            int compare = Math.Min(llrs.Length, sent.Length);
            for (int i = 0; i < compare; i++)
            {
                // An exactly-zero LLR is an erasure, not an opinion — WN0's cold-start RAKE
                // erases its first symbol of every burst by design — so it is neither a
                // compared bit nor an error (docs/ms110d/ota-handover.md §2).
                if (llrs[i] == 0)
                {
                    continue;
                }

                uncodedBits++;
                if ((llrs[i] > 0 ? 0 : 1) != sent[i])
                {
                    uncodedErrors++;
                }
            }
        };

        demod.BlockDecoded += _ =>
        {
            BeginBurst(consumed);
            locked ??= demod.Lock;
        };

        demod.BurstCompleted += burst =>
        {
            BeginBurst(consumed);

            long payloadBits = 0, payloadErrors = 0;
            if (reference is not null)
            {
                byte[] sent = reference.PayloadBits;
                payloadBits = sent.Length;
                int compared = Math.Min(burst.PayloadBits.Length, sent.Length);
                for (int i = 0; i < compared; i++)
                {
                    if (burst.PayloadBits[i] != sent[i])
                    {
                        payloadErrors++;
                    }
                }

                payloadErrors += sent.Length - compared; // bits that never arrived are wrong
            }

            SnrEstimate? snr = null;
            if (burstNoise is { Length: >= 1024 } && burstFill >= 1024)
            {
                snr = SnrEstimator.Estimate(
                    burstAudio.AsSpan(0, burstFill), burstNoise, rate,
                    SnrEstimator.ReferenceBandwidthHz,
                    _options.OccupiedLowHz, _options.OccupiedHighHz);
            }

            IReadOnlyList<string> burstDiagnostics = Array.Empty<string>();
            if (diagnostics is not null)
            {
                var lines = new List<string>();
                foreach ((long sample, string message) in diagnostics)
                {
                    if (sample >= burstStartSample && sample <= consumed)
                    {
                        lines.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{sample / (double)rate,8:F2}s {message}"));
                    }
                }

                burstDiagnostics = lines;
            }

            found.Add(new BurstScore(
                Index: found.Count,
                StartSeconds: burstStartSample / (double)rate,
                EndSeconds: consumed / (double)rate,
                Acquired: locked is not null,
                WaveformNumber: locked?.WaveformNumber,
                Interleaver: locked?.Interleaver,
                CfoHz: locked?.CfoHz ?? double.NaN,
                Scheduled: scheduled,
                WidCorrect: locked is not null && reference is not null
                            && locked.WaveformNumber == reference.Settings.WaveformNumber
                            && locked.Interleaver == reference.Settings.Interleaver,
                PayloadBits: payloadBits,
                PayloadErrors: payloadErrors,
                UncodedBits: uncodedBits,
                UncodedErrors: uncodedErrors,
                Blocks: burst.Blocks,
                Reason: burst.Reason,
                TurboConverged: demod.TurboConverged - turboC0,
                TurboReverted: demod.TurboReverted - turboR0,
                TurboAborted: demod.TurboAborted - turboA0,
                Snr: snr,
                Diagnostics: burstDiagnostics));

            burstStartSample = -1;
            burstNoise = null;
            locked = null;
            reference = null;
            scheduled = false;
            uncodedBits = 0;
            uncodedErrors = 0;
            burstFill = 0;

            // Quiet from before this burst is no use to the next one.
            guardFill = 0;
            guardHead = 0;
        };

        int chunk = Math.Max(1, (int)(_options.ChunkSeconds * rate));

        void Consume(ReadOnlySpan<float> slice)
        {
            demod.Process(slice);

            // Carrier detect is what marks the burst boundary: it goes true partway through the
            // preamble, which is a start, whereas the first block event is an end.
            bool active = demod.CarrierDetect;
            if (active && burstStartSample < 0)
            {
                BeginBurst(consumed);
            }
            else if (!active && burstStartSample >= 0)
            {
                // Carrier came and went with no burst — a false acquisition. Drop it and go
                // back to collecting quiet, rather than leaving the guard frozen for good.
                burstStartSample = -1;
                burstNoise = null;
                locked = null;
                reference = null;
                scheduled = false;
                uncodedBits = 0;
                uncodedErrors = 0;
                burstFill = 0;
            }

            if (burstStartSample >= 0)
            {
                if (burstFill + slice.Length > burstAudio.Length)
                {
                    Array.Resize(ref burstAudio, Math.Max(burstAudio.Length * 2, burstFill + slice.Length));
                }

                slice.CopyTo(burstAudio.AsSpan(burstFill));
                burstFill += slice.Length;
            }
            else
            {
                foreach (float v in slice)
                {
                    guard[guardHead] = v;
                    guardHead = (guardHead + 1) % guard.Length;
                    guardFill = Math.Min(guardFill + 1, guard.Length);
                }
            }

            consumed += slice.Length;
        }

        // Chunk on absolute sample positions, not within each incoming block, so a capture
        // scores identically however the reader happens to have divided it up.
        var pending = new float[chunk];
        int pendingFill = 0;
        foreach (ReadOnlyMemory<float> block in audioBlocks)
        {
            ReadOnlySpan<float> span = block.Span;
            while (span.Length > 0)
            {
                if (pendingFill == 0 && span.Length >= chunk)
                {
                    Consume(span[..chunk]);
                    span = span[chunk..];
                    continue;
                }

                int take = Math.Min(chunk - pendingFill, span.Length);
                span[..take].CopyTo(pending.AsSpan(pendingFill));
                pendingFill += take;
                span = span[take..];
                if (pendingFill == chunk)
                {
                    Consume(pending);
                    pendingFill = 0;
                }
            }
        }

        if (pendingFill > 0)
        {
            Consume(pending.AsSpan(0, pendingFill));
        }

        var missed = new List<ScheduledBurst>();
        for (int k = 0; k < _schedule.Count; k++)
        {
            if (!matched[k])
            {
                missed.Add(_schedule[k]);
            }
        }

        return new CaptureScore(found, missed, consumed / (double)rate);
    }

    /// <summary>The oldest <paramref name="wanted"/> samples of the rolling guard, in order.
    /// Oldest, not newest, because the newest are the preamble of the burst about to start.</summary>
    private static float[] OldestOfGuard(float[] guard, int fill, int head, int wanted)
    {
        int take = Math.Min(wanted, fill);
        var snapshot = new float[take];
        int oldest = fill < guard.Length ? 0 : head;
        for (int k = 0; k < take; k++)
        {
            snapshot[k] = guard[(oldest + k) % guard.Length];
        }

        return snapshot;
    }
}
