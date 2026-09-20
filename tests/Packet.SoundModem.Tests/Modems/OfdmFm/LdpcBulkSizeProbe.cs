using M0LTE.Fec;
using M0LTE.FecLdpc;
using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// Whether the LDPC 1/2 gain <see cref="LdpcAbProbe"/> measured at 126 and 510 bytes survives at
/// the frame sizes bulk transfers actually use, and what the codeword tax
/// <see cref="LdpcLadderProbe"/> found at 256 bytes costs as a burst grows into several
/// codewords. packet-net/pdn-ofdm-fm#10, step 3.
/// </summary>
/// <remarks>
/// <para>Same link model, same noise sweep shape and the same pass criterion as the two probes
/// above - frames recovered out of N seeds per CNR point, on the synthetic profile only, so
/// these numbers sit next to the recorded ones without a units argument. What is new here is the
/// payload sizes (1024, 1900, 3000 bytes - the range long transfers actually use, per
/// docs/dev/ofdm-fm/receiver-findings.md's "How fast is it" section) and a third code, K=7 rate
/// 2/3, as the reference point for what the 8 kHz preset actually runs.</para>
/// <para>Neither the A/B nor the ladder probe knew the cliff in advance either, but both could
/// centre a fixed window on numbers already measured elsewhere. Nothing here has been measured
/// on the synthetic profile before, so this probe finds its own centre first: a cheap bisection
/// at a handful of seeds, then the real sweep - 1 dB steps, the full seed count, widened outward
/// if the cheap centre missed - around the point that search lands on. The reported 50 % point
/// only ever comes from the full-seed points.</para>
/// <para>A measurement, not a check: run with <c>OFDMFM_PROBE=1</c>, seeds via
/// <c>OFDMFM_SEEDS</c> (default 32).</para>
/// </remarks>
public class LdpcBulkSizeProbe
{
    private const double LegalPeakDeviationHz = 2500;

    private static readonly OfdmFmCoding LdpcCoding = new(OfdmFmFec.Ldpc);
    private static readonly OfdmFmCoding K9Half = new(OfdmFmFec.Convolutional, 9, 1, 2, true);
    private static readonly OfdmFmCoding K7TwoThirds = new(OfdmFmFec.Convolutional, 7, 2, 3, true);

    // The five FreeDV/codec2 mother codes, smallest first - the same list OfdmFmCodec.LdpcFrames
    // packs a payload into. Read via the public LdpcCodes table rather than retyped by hand, so
    // the codeword count this probe reports can never drift from what the codec actually does;
    // that method itself is private, so the packing below is re-derived, not called.
    private static readonly LdpcCode[] MotherCodesBySize =
    [
        LdpcCodes.HRA_56_56, LdpcCodes.H_128_256_5, LdpcCodes.H_256_512_4,
        LdpcCodes.H_1024_2048_4f, LdpcCodes.H_4096_8192_3d,
    ];

    [Fact]
    public void Ldpc_Against_Convolutional_At_Bulk_Payload_Sizes()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null,
            "set OFDMFM_PROBE=1 for the bulk-size LDPC probe - a campaign, not a check");

        int seeds =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 32;

        // The committed synthetic profile only - no local overrides, no geometry numbers.
        OfdmFmParameters profile = OfdmFmParameters.Synthetic;
        FmLinkProfile link = FmLinkProfile.DataPort(LegalPeakDeviationHz, audioHighHz: null);

        Console.WriteLine(
            $"# LDPC 1/2 against K=9 1/2 and K=7 2/3 at bulk payload sizes, synthetic profile, "
            + $"DataPort {LegalPeakDeviationHz} Hz, {seeds} seeds per point, 50% point "
            + "interpolated across the 1 dB step that crosses it");

        foreach (int payloadBytes in new[] { 1024, 1900, 3000 })
        {
            int codewords = CountLdpcCodewords(payloadBytes);
            foreach (OfdmFmConstellation constellation in
                new[] { OfdmFmConstellation.Qam16, OfdmFmConstellation.Qam64 })
            {
                Console.WriteLine(
                    $"## {payloadBytes} B, {constellation}, LDPC splits into {codewords} codeword"
                    + (codewords == 1 ? "" : "s"));

                foreach ((string label, OfdmFmCoding coding) in new (string, OfdmFmCoding)[]
                {
                    ("ldpc 1/2", LdpcCoding),
                    ("k9 1/2", K9Half),
                    ("k7 2/3", K7TwoThirds),
                })
                {
                    (double half, List<(double Cnr, int Recovered)> points) = SweepAndFindHalf(
                        profile, link, coding, constellation, payloadBytes, seeds);

                    Console.WriteLine(
                        $"| {label} | " + string.Join(" | ", points.Select(p => $"+{p.Cnr:0.0}"))
                        + " |");
                    Console.WriteLine(
                        "| recovered | "
                        + string.Join(" | ", points.Select(p => $"{p.Recovered}/{seeds}")) + " |");
                    Console.WriteLine($"| 50% point | +{half:0.0} dB |");
                }

                Console.WriteLine();
            }
        }
    }

    /// <summary>How a framed payload (payload bytes plus the burst codec's CRC-16) of this many
    /// bytes splits across the LDPC mother codes: full frames of the largest that still fits,
    /// then the next size down, ending in one shortened tail frame once the remainder drops below
    /// the smallest mother code.</summary>
    private static int CountLdpcCodewords(int payloadBytes)
    {
        int remaining = (payloadBytes + 2) * 8;
        int frames = 0;
        while (remaining > 0)
        {
            LdpcCode? full = null;
            for (int i = MotherCodesBySize.Length - 1; i >= 0; i--)
            {
                if (MotherCodesBySize[i].NumberRowsHcols <= remaining)
                {
                    full = MotherCodesBySize[i];
                    break;
                }
            }

            frames++;
            remaining -= full is LdpcCode code ? code.NumberRowsHcols : remaining;
        }

        return frames;
    }

    private static int Run(
        OfdmFmParameters profile,
        FmLinkProfile link,
        OfdmFmCoding coding,
        OfdmFmConstellation constellation,
        int payloadBytes,
        double cnrDb,
        int seeds)
    {
        var coded = profile with { Coding = coding };
        var codec = new OfdmFmBurstCodec(coded);
        var payload = new byte[payloadBytes];
        new Random(1000).NextBytes(payload);
        float[] clean = codec.Modulate(payload, constellation, 2);

        int recovered = 0;
        for (int seed = 0; seed < seeds; seed++)
        {
            float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, cnrDb);
            OfdmFmBurst? burst = codec.Demodulate(heard);
            if (burst?.Payload is byte[] got && got.AsSpan().SequenceEqual(payload))
            {
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>Locates the cliff cheaply (few seeds, bisection), then sweeps the full seed count
    /// at 1 dB steps around that centre, widening outward if the cheap centre missed, and
    /// interpolates the 50 % point across the step that crosses it - the same interpolation
    /// <see cref="LdpcLadderProbe"/> uses.</summary>
    private static (double Half, List<(double Cnr, int Recovered)> Points) SweepAndFindHalf(
        OfdmFmParameters profile,
        FmLinkProfile link,
        OfdmFmCoding coding,
        OfdmFmConstellation constellation,
        int payloadBytes,
        int seeds)
    {
        const int CoarseSeeds = 4;
        double half = seeds / 2.0;

        double lo = 0;
        double hi = 36;
        for (int i = 0; i < 6; i++)
        {
            double mid = Math.Round((lo + hi) / 2);
            int r = Run(profile, link, coding, constellation, payloadBytes, mid, CoarseSeeds);
            if (r >= CoarseSeeds / 2.0)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        double centre = Math.Round((lo + hi) / 2);

        var points = new List<(double Cnr, int Recovered)>();
        for (double cnr = centre - 2; cnr <= centre + 2.01; cnr += 1)
        {
            points.Add((cnr, Run(profile, link, coding, constellation, payloadBytes, cnr, seeds)));
        }

        int guard = 0;
        while (points[0].Recovered > half && guard++ < 8)
        {
            double cnr = points[0].Cnr - 1;
            points.Insert(
                0, (cnr, Run(profile, link, coding, constellation, payloadBytes, cnr, seeds)));
        }

        guard = 0;
        while (points[^1].Recovered < half && guard++ < 8)
        {
            double cnr = points[^1].Cnr + 1;
            points.Add((cnr, Run(profile, link, coding, constellation, payloadBytes, cnr, seeds)));
        }

        double result = double.NaN;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i - 1].Recovered < half && points[i].Recovered >= half)
            {
                result = points[i - 1].Cnr
                    + ((half - points[i - 1].Recovered)
                        / (points[i].Recovered - points[i - 1].Recovered));
                break;
            }
        }

        return (result, points);
    }
}
