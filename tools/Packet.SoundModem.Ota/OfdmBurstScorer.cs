using M0LTE.Ofdm;
using Packet.SoundModem.UberSdr;
using Packet.SoundModem.Iq;
using M0LTE.Dsp;

namespace Packet.SoundModem.Ota;

/// <summary>What one datac burst scored to.</summary>
/// <param name="Index">Position in the pass.</param>
/// <param name="Mode">The datac mode.</param>
/// <param name="StartSeconds">Where the active burst began in the capture.</param>
/// <param name="AskedSnrDb">SNR the rig was asked to inject - beside the measured one.</param>
/// <param name="Acquired">Whether the demodulator recovered a packet from the burst window.</param>
/// <param name="CrcOk">Whether the recovered packet's own CRC passed - FreeDV's "packet received"
/// criterion.</param>
/// <param name="CfoHz">The demodulator's carrier-frequency-offset estimate at decode.</param>
/// <param name="Snr">The delivered SNR measured from the burst against its own noise lead-in.</param>
/// <param name="PayloadBits">Payload bits compared (a lost packet counts every bit wrong).</param>
/// <param name="PayloadBitErrors">Post-LDPC payload bit errors against what was sent.</param>
/// <param name="LdpcIterations">LDPC iterations the decoder ran (a soft margin indicator).</param>
/// <param name="ParityChecks">LDPC parity checks satisfied at exit (a soft margin indicator).</param>
internal sealed record OfdmBurstScore(
    int Index,
    string Mode,
    double StartSeconds,
    double AskedSnrDb,
    bool Acquired,
    bool CrcOk,
    double CfoHz,
    SnrEstimate? Snr,
    int PayloadBits,
    int PayloadBitErrors,
    int LdpcIterations,
    int ParityChecks)
{
    /// <summary>Post-LDPC coded bit-error rate - the number FreeDV cross-checks are quoted in.</summary>
    public double CodedBer => PayloadBits == 0 ? double.NaN : (double)PayloadBitErrors / PayloadBits;
}

/// <summary>Everything a scored OFDM pass measured.</summary>
/// <param name="AudioSeconds">Length of the converted audio.</param>
/// <param name="Bursts">One row per scheduled burst, in order.</param>
internal sealed record OfdmCaptureScore(double AudioSeconds, IReadOnlyList<OfdmBurstScore> Bursts);

/// <summary>
/// Scores a captured (or rehearsed) FreeDV datac pass with the OFDM demodulator - the OFDM
/// counterpart of <see cref="BurstScorer"/>, driving the library's own <see cref="DatacReceiver"/>
/// rather than the MS110D demodulator.
/// </summary>
/// <remarks>
/// <para>The capture's IQ is converted to the datac engine's native 8&#160;kHz real audio with the
/// same <see cref="StreamingSsbDemodulator"/> the MS110D scorer uses (DAX places the datac band
/// at its native ~1500&#160;Hz audio centre, so the down-shift is the manifest's <c>OffsetHz</c>,
/// which is 0 for DAX). Each burst is then scored in its own window: a fresh
/// <see cref="DatacReceiver"/> is driven <c>Nin</c>-by-<c>Nin</c> exactly as
/// <see cref="Packet.SoundModem.Modems.FreeDvDatacModem"/> and <see cref="DatacPacketProbe"/> drive
/// it, giving an unambiguous 0/1 result per burst (a missed burst is the result that matters most at
/// the bottom of a ladder, and per-window scoring can never mis-attribute one), and the delivered
/// SNR is measured from the burst against its own transmitted noise lead-in with
/// <see cref="SnrEstimator"/> - the same convention, and the same instrument, the MS110D ladder is
/// audited on. Nominal SNR is never trusted: the injected figure is the request, this is the
/// measurement, and the two are compared.</para>
/// <para><b>Coded, not uncoded, BER.</b> The datac engine exposes post-LDPC packet bytes per packet,
/// not the pre-LDPC hard bits, so - like <see cref="DatacPacketProbe"/>, the codebase's existing
/// OFDM probe - the figure of merit here is the coded BER plus the LDPC iteration / parity-check
/// margin, which is exactly the quantity FreeDV publishes its datac operating points in.</para>
/// </remarks>
internal sealed class OfdmBurstScorer
{
    /// <summary>The datac engine's native sample rate.</summary>
    public const int NativeRate = OfdmLadderPass.NativeRate;

    // The recovered audio has been through the SSB passband, so its noise occupies that band; the
    // SNR estimator must be told so, or it over-subtracts noise at the bottom of the ladder (the
    // failure SnrEstimator's own remarks warn about). These match the MS110D scorer's occupied band.
    private const double OccupiedLowHz = 150;
    private const double OccupiedHighHz = 3450;

    // Leading noise fed to the correlator before the nominal burst start, so filter-delay slop can
    // never clip the preamble; trailing audio past the postamble for the end-of-burst window.
    private const double DecodePreRollSeconds = 0.3;
    private const double DecodePostRollSeconds = 0.5;

    // The SNR signal window is inset from the burst edges (the modulator's ramp is not stationary
    // signal power); the noise window sits before the burst with a guard off its leading edge.
    private const double SnrInsetSeconds = 0.05;
    private const double NoiseGuardSeconds = 0.3;
    private const double NoiseWindowSeconds = 2.0;

    private readonly float[] _audio;

    /// <summary>Builds a scorer over already-converted 8&#160;kHz audio (the offline/test seam).</summary>
    public OfdmBurstScorer(float[] audio8k) => _audio = audio8k;

    /// <summary>Converts a capture WAV's IQ to 8&#160;kHz audio and builds a scorer over it.</summary>
    /// <param name="capturePath">The 2-channel IQ capture.</param>
    /// <param name="dialHz">Down-shift that lands the suppressed carrier at 0&#160;Hz - the pass
    /// offset (0 for DAX).</param>
    public static OfdmBurstScorer FromCapture(string capturePath, double dialHz)
    {
        using var reader = new PcmWavReader(capturePath);
        if (reader.Channels != 2)
        {
            throw new InvalidDataException(
                $"expected a 2-channel IQ capture, found {reader.Channels}");
        }

        if (reader.SampleRate % NativeRate != 0)
        {
            throw new InvalidDataException(
                $"capture rate {reader.SampleRate} Hz is not an integer multiple of the datac "
                + $"native {NativeRate} Hz - the IQ→audio decimation needs one");
        }

        var converter = new StreamingSsbDemodulator(new SsbDemodulatorOptions
        {
            InputRate = reader.SampleRate,
            OutputRate = NativeRate,
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
        return new OfdmBurstScorer([.. audio]);
    }

    /// <summary>Scores every burst the manifest describes.</summary>
    public OfdmCaptureScore Score(OfdmCampaignManifest manifest)
    {
        var rows = new List<OfdmBurstScore>(manifest.Bursts.Count);
        for (int index = 0; index < manifest.Bursts.Count; index++)
        {
            rows.Add(ScoreBurst(index, manifest.Bursts[index]));
        }

        return new OfdmCaptureScore(_audio.Length / (double)NativeRate, rows);
    }

    private OfdmBurstScore ScoreBurst(int index, OfdmCampaignBurst burst)
    {
        OfdmMode mode = DatacEngine.Mode(burst.Mode);
        int startSample = (int)Math.Round(burst.StartSeconds * NativeRate);
        int burstSamples = (int)Math.Round(burst.BurstSeconds * NativeRate);

        byte[] payload = DatacEngine.Payload(mode, burst.Seed);

        (bool acquired, bool crcOk, double cfoHz, int bitErrors, int iterations, int parity) =
            Decode(mode, startSample, burstSamples, payload);
        SnrEstimate? snr = MeasureSnr(startSample, burstSamples);

        return new OfdmBurstScore(
            index, burst.Mode, burst.StartSeconds, burst.SnrDb, acquired, crcOk, cfoHz, snr,
            payload.Length * 8, bitErrors, iterations, parity);
    }

    /// <summary>Drives a fresh receiver over the burst window, <c>Nin</c> at a time.</summary>
    private (bool Acquired, bool CrcOk, double CfoHz, int BitErrors, int Iterations, int ParityChecks)
        Decode(OfdmMode mode, int startSample, int burstSamples, byte[] payload)
    {
        int from = Math.Max(0, startSample - (int)(DecodePreRollSeconds * NativeRate));
        int to = Math.Min(
            _audio.Length, startSample + burstSamples + (int)(DecodePostRollSeconds * NativeRate));
        if (to - from <= 0)
        {
            return (false, false, 0, payload.Length * 8, 0, 0);
        }

        short[] samples = DatacEngine.ToShort(_audio.AsSpan(from, to - from));
        DatacRxResult? decoded = DatacEngine.DecodeFirstPacket(mode, samples, out double cfoHz);
        if (decoded is not { } r)
        {
            return (false, false, cfoHz, payload.Length * 8, 0, 0);
        }

        // r.Bytes is payload followed by the 2-byte CRC; comparing against the PayloadBytes-long sent
        // payload compares exactly the payload region.
        return (true, r.CrcOk, cfoHz, DatacEngine.BitErrors(r.Bytes, payload), r.Iterations, r.ParityChecks);
    }

    /// <summary>Measures the delivered SNR from the burst against its own noise lead-in.</summary>
    private SnrEstimate? MeasureSnr(int startSample, int burstSamples)
    {
        int inset = (int)(SnrInsetSeconds * NativeRate);
        int burstFrom = startSample + inset;
        int burstTo = Math.Min(_audio.Length, startSample + burstSamples - inset);

        int noiseTo = startSample - (int)(NoiseGuardSeconds * NativeRate);
        int noiseFrom = Math.Max(0, noiseTo - (int)(NoiseWindowSeconds * NativeRate));

        if (burstTo - burstFrom < 1024 || noiseTo - noiseFrom < 1024)
        {
            return null; // not enough burst or lead-in to measure against
        }

        return SnrEstimator.Estimate(
            _audio.AsSpan(burstFrom, burstTo - burstFrom),
            _audio.AsSpan(noiseFrom, noiseTo - noiseFrom),
            NativeRate,
            occupiedLowHz: OccupiedLowHz,
            occupiedHighHz: OccupiedHighHz);
    }
}
