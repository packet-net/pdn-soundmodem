using Packet.SoundModem.Modems;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

namespace Packet.SoundModem.Ota;

/// <summary>What one SSB audio-carrier burst scored to.</summary>
/// <param name="Index">Position in the pass.</param>
/// <param name="Mode">The mode.</param>
/// <param name="StartSeconds">Where the active burst began in the capture.</param>
/// <param name="AskedSnrDb">SNR the rig was asked to inject - beside the measured one.</param>
/// <param name="Acquired">Whether the demodulator's carrier detect asserted over the burst window
/// (it locked onto a signal, whether or not a frame came out).</param>
/// <param name="Decoded">Whether the sent frame came back bit-exact - the coded success criterion, and
/// the deployment metric (a NinoTNC "frame received").</param>
/// <param name="FramesDecoded">Frames the receiver emitted in the window (normally 0 or 1).</param>
/// <param name="CfoHz">Carrier-frequency offset the decoding branch reported - the far-station-off-
/// frequency estimate at diversity-bank-step resolution for the multi-branch PSK modes, 0 for the
/// single-decoder modes (AFSK); <c>NaN</c> when nothing decoded.</param>
/// <param name="CorrectedBytes">Bytes the Reed-Solomon FEC repaired on the decoded frame - a floor on
/// the channel's byte-error rate, and the honest per-frame stress metric (the pre-FEC hard-bit BER is
/// not observable through the <see cref="IModem"/> seam for the RS-framed IL2P modes; see
/// <see cref="FrameQuality"/>). 0 when nothing decoded, and for framings without FEC (plain AX.25).</param>
/// <param name="Snr">The delivered SNR measured from the burst against its own noise lead-in.</param>
/// <param name="PayloadBits">Payload bits compared (a lost frame counts every bit wrong).</param>
/// <param name="PayloadBitErrors">Coded payload bit errors: 0 on a decoded frame (framing is
/// all-or-nothing behind its CRC/FCS), every bit on a lost one.</param>
internal sealed record SsbBurstScore(
    int Index,
    string Mode,
    double StartSeconds,
    double AskedSnrDb,
    bool Acquired,
    bool Decoded,
    int FramesDecoded,
    double CfoHz,
    int CorrectedBytes,
    SnrEstimate? Snr,
    int PayloadBits,
    int PayloadBitErrors)
{
    /// <summary>Coded bit-error rate. The framing is all-or-nothing behind its CRC/FCS, so this is 0 on
    /// a decoded frame and 1 on a lost one - read the decoded/lost tally, not this, at the frame layer.</summary>
    public double CodedBer => PayloadBits == 0 ? double.NaN : (double)PayloadBitErrors / PayloadBits;
}

/// <summary>Everything a scored SSB audio-carrier pass measured.</summary>
/// <param name="AudioSeconds">Length of the converted audio.</param>
/// <param name="Bursts">One row per scheduled burst, in order.</param>
internal sealed record SsbCaptureScore(double AudioSeconds, IReadOnlyList<SsbBurstScore> Bursts);

/// <summary>
/// Scores a captured (or rehearsed) SSB audio-carrier pass with the mode's own demodulator - the
/// audio-carrier counterpart of <see cref="BurstScorer"/> and <see cref="OfdmBurstScorer"/>, driving
/// the catalogue mode entirely through the <see cref="IModem"/> seam <see cref="ModemCatalog.Create"/>
/// hands back.
/// </summary>
/// <remarks>
/// <para>The capture's IQ is converted to the mode's native DSP-rate real audio with the same
/// <see cref="StreamingSsbDemodulator"/> the MS110D and OFDM scorers use (DAX places the audio
/// carrier at its native audio centre, so the down-shift is the manifest's <c>OffsetHz</c>, which is 0
/// for DAX). Each burst is then scored in its own window: a fresh <see cref="IModem"/> is driven over
/// the burst exactly as the deployment path drives it, giving an unambiguous 0/1 result per burst (a
/// missed burst is the result that matters most at the bottom of a ladder). The delivered SNR is
/// measured from the burst against its own transmitted noise lead-in with <see cref="SnrEstimator"/> -
/// the same convention, and the same instrument, the other ladders are audited on. Nominal SNR is
/// never trusted: the injected figure is the request, this is the measurement, and the two are
/// compared.</para>
/// <para><b>Coded, not uncoded, BER.</b> The IL2P modes carry Reed-Solomon FEC and only surface a
/// frame once its trailing CRC (or, for plain AX.25, its FCS) passes, so a decoded frame is correct and
/// the coded figure is all-or-nothing (the honest headline is the frame-error rate). The pre-FEC
/// hard-bit stream is not exposed through the <see cref="IModem"/> seam for these modes - the same
/// limitation the OFDM ladder documents for its post-LDPC packet bytes - so the sub-frame stress metric
/// here is the count of FEC-repaired bytes (<see cref="FrameQuality.CorrectedBytes"/>, non-null for the
/// RS-framed IL2P modes), a floor on the channel byte-error rate, alongside the acquisition
/// (carrier-detect) rate that separates "never locked" from "locked but the FEC could not close."</para>
/// </remarks>
internal sealed class SsbBurstScorer
{
    // The recovered audio has been through the SSB passband, so its noise occupies that band; the SNR
    // estimator must be told so, or it over-subtracts noise at the bottom of the ladder. These match
    // the MS110D/OFDM scorers' occupied band.
    //
    // The occupied band is the receive SSB PASSBAND - where the noise lives - and is deliberately the
    // same for every mode, because the noise floor does not depend on the mode's own signal bandwidth
    // and neither does the estimator: it measures total burst power and subtracts the noise across this
    // band, so a narrow bpsk300 carrier and a wide bpsk1200 carrier are both scored against the noise
    // in the same 3 kHz reference. That the band is mode-independent is CORRECT, not a bug.
    //
    // #121 (part 2) observed that on the rig bpsk1200's delivered SNR reads ~2.3 dB under asked across a
    // run, while afsk300 (~0.9 dB) and bpsk300 (~0.1-1.5 dB) sit far closer - a mode-dependent offset,
    // and #121 wondered whether the occupied band here was mis-set for bpsk1200. It is not. The
    // end-to-end harness self-cal (SsbLadderPassTests, which renders each mode through the sim channel,
    // the real SSB up/down-conversion, and this estimator) tracks measured−asked to within ~0.10-0.15 dB
    // for EVERY SSB mode - bpsk1200 included, and in fact bpsk1200 is the tightest - with no
    // mode-dependent bias. So the estimator and this occupied band are unbiased for these waveforms; the
    // ~2.3 dB is not introduced in the measurement.
    //
    // The offset is therefore a real, rig-side DELIVERED-signal difference, not a harness defect: the
    // Flex DIGU/DAX transmit path and the radio's own SSB/roofing filter roll off the edges of the wider
    // ~1.2 kHz bpsk1200 spectrum (a 1200-baud carrier, like qpsk2400 spanning ≈900-2100 Hz about its
    // audio centre) more than they touch the narrow 300-baud afsk300/bpsk300 carriers, and ALC on the
    // wider crest factor trims a little more. By the rig's own convention (signal power vs noise in
    // 3 kHz) the delivered SNR of the wider mode really is a couple of dB lower - a true value the scorer
    // reports faithfully, not a mis-measurement. Characterised in #121, not a defect; do not "correct"
    // it here by widening/narrowing the band per mode, which would bias the measurement to hide a real
    // transmit-chain effect.
    private const double OccupiedLowHz = 150;
    private const double OccupiedHighHz = 3450;

    // Leading audio fed to the demodulator before the nominal burst start, so it settles its carrier
    // detect on a real noise floor and no filter-delay slop clips the preamble; trailing audio past
    // the burst for the end-of-frame flush. The lead-in is noise-only and far longer than this, and
    // the inter-burst gap is longer still, so this can never reach the previous burst.
    private const double DecodePreRollSeconds = 1.0;
    private const double DecodePostRollSeconds = 0.5;

    // The SNR signal window is inset from the burst edges (the modulator ramp is not stationary signal
    // power); the noise window sits before the burst with a guard off its leading edge.
    private const double SnrInsetSeconds = 0.03;
    private const double NoiseGuardSeconds = 0.3;
    private const double NoiseWindowSeconds = 2.0;

    private readonly float[] _audio;
    private readonly int _nativeRate;

    /// <summary>Builds a scorer over already-converted native-rate audio (the offline/test seam).</summary>
    public SsbBurstScorer(float[] audio, int nativeRate)
    {
        _audio = audio;
        _nativeRate = nativeRate;
    }

    /// <summary>Converts a capture WAV's IQ to the mode's native-rate audio and builds a scorer over
    /// it.</summary>
    /// <param name="capturePath">The 2-channel IQ capture.</param>
    /// <param name="dialHz">Down-shift that lands the suppressed carrier at 0&#160;Hz - the pass
    /// offset (0 for DAX).</param>
    /// <param name="nativeRate">The mode's DSP rate (all SSB audio-carrier modes: 12&#160;kHz).</param>
    public static SsbBurstScorer FromCapture(string capturePath, double dialHz, int nativeRate)
    {
        using var reader = new PcmWavReader(capturePath);
        if (reader.Channels != 2)
        {
            throw new InvalidDataException(
                $"expected a 2-channel IQ capture, found {reader.Channels}");
        }

        if (reader.SampleRate % nativeRate != 0)
        {
            throw new InvalidDataException(
                $"capture rate {reader.SampleRate} Hz is not an integer multiple of the mode's native "
                + $"{nativeRate} Hz - the IQ→audio decimation needs one");
        }

        var converter = new StreamingSsbDemodulator(new SsbDemodulatorOptions
        {
            InputRate = reader.SampleRate,
            OutputRate = nativeRate,
            DialHz = dialHz,
            SsbLowHz = OccupiedLowHz,
            SsbHighHz = OccupiedHighHz,
            NormalisePeak = 0f,
        });

        const int blockFrames = 1 << 16;
        var input = new short[blockFrames * 2];
        var output = new float[converter.MaxOutputFor(blockFrames) + converter.MaxFlushOutput];
        var audio = new List<float>();

        int frames;
        while ((frames = reader.ReadFrames(input)) > 0)
        {
            int wrote = converter.Process(input.AsSpan(0, frames * 2), output);
            audio.AddRange(output.AsSpan(0, wrote));
        }

        audio.AddRange(output.AsSpan(0, converter.Flush(output)));
        return new SsbBurstScorer([.. audio], nativeRate);
    }

    /// <summary>Scores every burst the manifest describes.</summary>
    public SsbCaptureScore Score(SsbCampaignManifest manifest)
    {
        var rows = new List<SsbBurstScore>(manifest.Bursts.Count);
        for (int index = 0; index < manifest.Bursts.Count; index++)
        {
            rows.Add(ScoreBurst(index, manifest.Bursts[index]));
        }

        return new SsbCaptureScore(_audio.Length / (double)_nativeRate, rows);
    }

    private SsbBurstScore ScoreBurst(int index, SsbCampaignBurst burst)
    {
        int startSample = (int)Math.Round(burst.StartSeconds * _nativeRate);
        int burstSamples = (int)Math.Round(burst.BurstSeconds * _nativeRate);

        byte[] sent = SimModem.Frame(burst.FrameBytes, burst.Seed);

        (bool acquired, bool decoded, int frames, double cfoHz, int correctedBytes) =
            Decode(burst.Mode, startSample, burstSamples, sent);
        SnrEstimate? snr = MeasureSnr(startSample, burstSamples);

        int payloadBits = burst.FrameBytes * 8;
        return new SsbBurstScore(
            index, burst.Mode, burst.StartSeconds, burst.SnrDb, acquired, decoded, frames, cfoHz,
            correctedBytes, snr, payloadBits, decoded ? 0 : payloadBits);
    }

    /// <summary>Drives a fresh receiver over the burst window and reports what it recovered.</summary>
    private (bool Acquired, bool Decoded, int Frames, double CfoHz, int CorrectedBytes)
        Decode(string mode, int startSample, int burstSamples, byte[] sent)
    {
        int from = Math.Max(0, startSample - (int)(DecodePreRollSeconds * _nativeRate));
        int to = Math.Min(
            _audio.Length, startSample + burstSamples + (int)(DecodePostRollSeconds * _nativeRate));
        if (to - from <= 0)
        {
            return (false, false, 0, double.NaN, 0);
        }

        int frames = 0;
        bool decoded = false;
        double cfoHz = double.NaN;
        int correctedBytes = 0;

        IModem rx = ModemCatalog.Create(mode, _nativeRate, static _ => { });
        rx.FrameDecoded += (frame, quality) =>
        {
            frames++;
            if (!frame.AsSpan().SequenceEqual(sent))
            {
                return;
            }

            decoded = true;
            cfoHz = quality.FrequencyOffsetHz ?? 0.0;
            correctedBytes = quality.CorrectedBytes ?? 0;
        };

        // Feed the window in daemon-sized (100 ms) blocks so the streaming path is exercised, and poll
        // carrier detect after each so a burst that locks but never closes the FEC is still recorded as
        // acquired (distinct from one the receiver never saw).
        bool acquired = false;
        int block = Math.Max(1, _nativeRate / 10);
        for (int pos = from; pos < to; pos += block)
        {
            int len = Math.Min(block, to - pos);
            rx.Process(_audio.AsSpan(pos, len));
            acquired |= rx.CarrierDetect;
        }

        return (acquired || decoded, decoded, frames, cfoHz, correctedBytes);
    }

    /// <summary>Measures the delivered SNR from the burst against its own noise lead-in.</summary>
    private SnrEstimate? MeasureSnr(int startSample, int burstSamples)
    {
        int inset = (int)(SnrInsetSeconds * _nativeRate);
        int burstFrom = startSample + inset;
        int burstTo = Math.Min(_audio.Length, startSample + burstSamples - inset);

        int noiseTo = startSample - (int)(NoiseGuardSeconds * _nativeRate);
        int noiseFrom = Math.Max(0, noiseTo - (int)(NoiseWindowSeconds * _nativeRate));

        if (burstTo - burstFrom < 1024 || noiseTo - noiseFrom < 1024)
        {
            return null; // not enough burst or lead-in to measure against
        }

        return SnrEstimator.Estimate(
            _audio.AsSpan(burstFrom, burstTo - burstFrom),
            _audio.AsSpan(noiseFrom, noiseTo - noiseFrom),
            _nativeRate,
            occupiedLowHz: OccupiedLowHz,
            occupiedHighHz: OccupiedHighHz);
    }
}
