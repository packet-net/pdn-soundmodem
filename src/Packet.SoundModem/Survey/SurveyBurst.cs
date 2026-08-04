namespace Packet.SoundModem.Survey;

/// <summary>
/// One burst of energy seen on the channel: where it sat, how long it lasted and how far it
/// stood above the noise. Produced by <see cref="SpectralBurstDetector"/> once the burst has
/// <em>ended</em>, which is what separates a transmission from a carrier.
/// </summary>
/// <remarks>
/// Frequencies are audio, not RF: the detector works on the same spectrum the waterfall draws,
/// and the dial that turns one into the other belongs to whoever is displaying or logging it.
/// </remarks>
/// <param name="StartLine">Spectrum line the burst began on.</param>
/// <param name="EndLine">Line after the last one it was present on.</param>
/// <param name="LowHz">Mean low edge over the burst's lines.</param>
/// <param name="HighHz">Mean high edge.</param>
/// <param name="PeakSnrDb">Best single-line SNR over the floor.</param>
/// <param name="MeanSnrDb">Mean SNR over the burst.</param>
/// <param name="EndedOnTimeout">
/// True when the burst was closed because it ran past the detector's duration limit rather than
/// because it stopped - a carrier, a long SSB over, or a data mode that is simply not packet.
/// Triage drops these; the flag is what stops that reading as "a packet we could not classify".
/// </param>
public readonly record struct SurveyBurst(
    long StartLine,
    long EndLine,
    double LowHz,
    double HighHz,
    double PeakSnrDb,
    double MeanSnrDb,
    bool EndedOnTimeout)
{
    /// <summary>Midpoint of the measured edges.</summary>
    public double CentreHz => (LowHz + HighHz) / 2;

    /// <summary>Measured occupied width.</summary>
    public double WidthHz => HighHz - LowHz;

    /// <summary>How many spectrum lines it lasted.</summary>
    public long Lines => EndLine - StartLine;
}
