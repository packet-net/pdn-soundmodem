using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// What the link does to a burst when there is no noise at all.
/// </summary>
/// <remarks>
/// Every measurement so far computed noise as (heard - quiet) against a noiseless pass, which
/// cancels any DETERMINISTIC distortion exactly. So a rate-dependent distortion - in the channel
/// model's filters, or in our own chain - has been invisible by construction. This looks straight
/// at it: how far from the ideal constellation does a NOISELESS burst land?
/// </remarks>
public class NoiselessErrorProbe
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void How_Much_Error_Does_A_Noiseless_Link_Leave_At_Each_Rate()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1", "probe");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        Console.WriteLine("| rate | link | EVM | implied SNR | worst carrier |");

        foreach (OfdmFmParameters geometry in (ReadOnlySpan<OfdmFmParameters>)
            [profile, profile.Rescaled(profile.SampleRate * 2)!])
        {
            var codec = new OfdmFmBurstCodec(geometry);
            var payload = new byte[64];
            new Random(1000).NextBytes(payload);
            float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);

            foreach (string kind in (ReadOnlySpan<string>)
                ["none", "fm-mic", "fm-mic +28", "fm-mic +24", "fm-mic +20"])
            {
                double cnr = kind switch
                {
                    "fm-mic +28" => 28,
                    "fm-mic +24" => 24,
                    "fm-mic +20" => 20,
                    _ => double.PositiveInfinity,
                };

                float[] heard = kind == "none"
                    ? clean
                    : new FmChannel(FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz), geometry.SampleRate, 0)
                        .Apply(clean, cnr);

                int sync = codec.Demodulate(heard) is { Payload: not null } b ? b.StartSample : -1;
                if (sync < 0)
                {
                    Console.WriteLine($"| {geometry.SampleRate} | {kind} | did not decode |");
                    continue;
                }

                (double Evm, double Worst) e = Evm(codec, geometry, heard, sync);
                Console.WriteLine(
                    $"| {geometry.SampleRate} | {kind} | {e.Evm * 100,6:F2} % | "
                    + $"{-20 * Math.Log10(e.Evm),6:F1} dB | {-20 * Math.Log10(e.Worst),6:F1} dB |");
            }
        }
    }

    /// <summary>Error vector magnitude of the equalised payload carriers, and the worst single
    /// carrier, against the nearest ideal QPSK point.</summary>
    private static (double Evm, double Worst) Evm(
        OfdmFmBurstCodec codec, OfdmFmParameters geometry, float[] audio, int sync)
    {
        (float I, float Q)[] points = OfdmFmMapper.Points(OfdmFmConstellation.Qpsk);
        bool[] pilots = geometry.PilotMap();
        int symbolSamples = geometry.SymbolSamples;

        double total = 0;
        int counted = 0;
        var perCarrier = new double[geometry.TotalCarriers];
        var perCarrierCount = new int[geometry.TotalCarriers];

        // Bounded by the burst's ACTUAL payload symbol count. Running past it reads silence and
        // reports a colossal error at every rate equally, which is a measurement of nothing.
        int[] carrierBits = geometry.BitsPerDataCarrier(OfdmFmConstellation.Qpsk);
        int perSymbol = carrierBits.Sum();
        int codedBits = new OfdmFmCodec(geometry.Codes).CodedBits((64 + 2) * 8);
        int symbols = ((codedBits + perSymbol) - 1) / perSymbol;

        // The payload symbols start after sync, preamble and the header symbols.
        for (int s = 0; s < symbols; s++)
        {
            int offset = sync + codec.HeaderEndOffset + (s * symbolSamples);
            if (offset + symbolSamples > audio.Length)
            {
                break;
            }

            (double Re, double Im)[]? symbol = codec.EqualisedAt(audio, offset, sync);
            if (symbol is null)
            {
                break;
            }

            for (int c = 0; c < symbol.Length; c++)
            {
                if (pilots[c])
                {
                    continue;
                }

                int nearest = OfdmFmMapper.Demap(points, (float)symbol[c].Re, (float)symbol[c].Im);
                double di = symbol[c].Re - points[nearest].I;
                double dq = symbol[c].Im - points[nearest].Q;
                double err = (di * di) + (dq * dq);
                total += err;
                counted++;
                perCarrier[c] += err;
                perCarrierCount[c]++;
            }
        }

        double worst = 0;
        for (int c = 0; c < perCarrier.Length; c++)
        {
            if (perCarrierCount[c] > 0)
            {
                worst = Math.Max(worst, Math.Sqrt(perCarrier[c] / perCarrierCount[c]));
            }
        }

        return (Math.Sqrt(total / Math.Max(1, counted)), worst);
    }
}
