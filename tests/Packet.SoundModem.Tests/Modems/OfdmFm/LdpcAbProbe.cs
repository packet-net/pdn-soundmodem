using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The code-family A/B the README calls for: the FreeDV datac rate-1/2 LDPC against the K=9
/// rate-1/2 convolutional, through the same link, on the same bursts.
/// </summary>
/// <remarks>
/// <para>The convolutional family was inherited from 802.11a rather than chosen, and the
/// literature puts a well-built LDPC 1 to 2 dB ahead at these block sizes. The literature has
/// also been wrong about this channel twice (MMSE, per-carrier noise weighting), which is why
/// this is a measurement and not a design note.</para>
/// <para><b>The payloads are sized so the comparison is exact.</b> A framed payload of 126 bytes
/// is 1024 bits, which fills H_1024_2048_4f to the brim; 510 bytes fills H_4096_8192_3d. Both
/// arms then produce identical coded-bit counts, identical symbol counts and identical air time,
/// so whatever separates them is the code and nothing else.</para>
/// <para>Sum-product is not scale invariant, and the scale story mattered here twice. The A/B
/// tables were taken with a fixed mean-magnitude normalisation swept to its optimum first - 2.5,
/// which is why they stand - and the rung calibration then found that same number four decibels
/// underconfident at QAM-64. The decoder now derives the scale per burst from the sync symbol's
/// free noise measurement (see <c>OfdmFmCodec.Decode</c>), which lands every constellation where
/// its own sweep would have put it.</para>
/// <para>A measurement, not a check: run with <c>OFDMFM_PROBE=1</c>, seeds via
/// <c>OFDMFM_SEEDS</c>.</para>
/// </remarks>
public class LdpcAbProbe
{
    private const double LegalPeakDeviationHz = 2500;

    private static readonly OfdmFmCoding Conv =
        new(OfdmFmFec.Convolutional, 9, 1, 2, true);

    private static readonly OfdmFmCoding Ldpc = new(OfdmFmFec.Ldpc);

    [Fact]
    public void Ldpc_Against_The_Convolutional_Code_It_Would_Replace()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null,
            "set OFDMFM_PROBE=1 for the LDPC A/B - a campaign, not a check");

        int seeds =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 32;

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        FmLinkProfile link = FmLinkProfile.DataPort(LegalPeakDeviationHz, audioHighHz: null);

        Console.WriteLine(
            $"# the A/B: 8 kHz preset, DataPort {LegalPeakDeviationHz} Hz, K=9 1/2 against LDPC 1/2, "
            + $"{seeds} seeds, identical air time by construction");
        foreach ((int payloadBytes, OfdmFmConstellation constellation, double centre) in
            (ReadOnlySpan<(int, OfdmFmConstellation, double)>)
            [
                (126, OfdmFmConstellation.Qpsk, 6.3),
                (126, OfdmFmConstellation.Qam16, 10.3),
                (510, OfdmFmConstellation.Qam16, 10.3),
            ])
        {
            Console.WriteLine(
                $"| {payloadBytes} B {constellation} | "
                + string.Join(" | ", Cnrs(centre).Select(c => $"+{c:0.0}")) + " |");
            foreach ((string label, OfdmFmCoding coding) in
                (ReadOnlySpan<(string, OfdmFmCoding)>)[("conv", Conv), ("ldpc", Ldpc)])
            {
                var cells = new List<string>();
                foreach (double cnr in Cnrs(centre))
                {
                    cells.Add(
                        $"{Run(profile, link, coding, constellation, payloadBytes, cnr, seeds)}"
                        + $"/{seeds}");
                }

                Console.WriteLine($"| {label} | " + string.Join(" | ", cells) + " |");
            }
        }
    }

    private static double[] Cnrs(double centre) =>
        [centre - 2, centre - 1, centre, centre + 1, centre + 2];

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
}
