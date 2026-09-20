using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// Does the pre-FEC bit error rate say how much link is left, and does it say it in the same
/// language on every rate?
/// </summary>
/// <remarks>
/// <para>A rate decision needs to know how close to the edge the link is, and "the CRC passed" does
/// not answer that: it is one bit, it is the same bit at 3 dB of margin as at 0.2, and by the time
/// it changes the decision is already too late. <see cref="OfdmFmBurst.PreFecBitErrorRate"/> is the
/// candidate replacement - how much of the code's correcting power a burst spent.</para>
/// <para>Two separate claims, and the second is the one that matters:</para>
/// <list type="number">
/// <item>It rises as the signal falls, smoothly, on every rate. Without that it is not a measure of
/// anything.</item>
/// <item><b>Its value at the cliff is roughly the same on every rate.</b> If it is, one number is a
/// universal "you are at the edge" and a step policy can be written once. If instead each rate has
/// its own danger value, every rung needs its own calibration and the whole scheme gets a table
/// that has to be re-measured whenever the waveform changes.</item>
/// </list>
/// <para>Measured only on bursts that decoded. The figure comes from re-encoding what the decoder
/// produced, so on a failed burst the reference is wrong too; including those would mix a
/// measurement with a guess. Median rather than mean, because the distribution has a tail of nearly
/// failed bursts and a mean would track the tail rather than the typical burst.</para>
/// </remarks>
public class MarginMeasureProbe
{
    private static readonly (OfdmFmConstellation C, string Label, int Num, int Den)[] Ladder =
    [
        (OfdmFmConstellation.Bpsk, "3/4", 3, 4),
        (OfdmFmConstellation.Qpsk, "2/3", 2, 3),
        (OfdmFmConstellation.Qpsk, "3/4", 3, 4),
        (OfdmFmConstellation.Qam16, "1/2", 1, 2),
        (OfdmFmConstellation.Qam16, "3/4", 3, 4),
        (OfdmFmConstellation.Qam64, "2/3", 2, 3),
        (OfdmFmConstellation.Qam256, "2/3", 2, 3),
    ];

    [Fact]
    public void Does_The_Pre_Fec_Error_Rate_Say_How_Much_Link_Is_Left()
    {
        // OFDMFM_PROBE, not OFDMFM_LADDER. These are campaign instruments that sweep a grid at 64
        // seeds a cell and run for several minutes each; the ladder gate is meant to stay something
        // a person will actually wait for.
        if (Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null)
        {
            return;
        }

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        int payloadBytes =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_PAYLOAD"), out int p) ? p : 256;
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s)
            ? s
            : 64;
        var link = TaitTm8100.Link(TaitBandwidth.Narrow, 2500);

        // Each rung is walked relative to its OWN cliff rather than on one absolute grid, because
        // the rungs are 17 dB apart and an absolute grid would spend most of its cells on rates that
        // are either perfect or dead. The cliffs are RateLadderGridProbe's, rounded, and were taken
        // on a narrower span than this probe now runs on: expect the offsets to need re-centring
        // once the grid has been re-measured here.
        var cliffs = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Bpsk 3/4"] = 6,
            ["Qpsk 2/3"] = 7,
            ["Qpsk 3/4"] = 7,
            ["Qam16 1/2"] = 10,
            ["Qam16 3/4"] = 14,
            ["Qam64 2/3"] = 19,
            ["Qam256 2/3"] = 23,
        };

        int[] offsets = [8, 6, 4, 3, 2, 1, 0, -1];

        Console.WriteLine(
            $"# R1/T13 narrow 2500 Hz, K=9, {payloadBytes}-byte payload, {seeds} seeds");
        Console.WriteLine(
            "# median pre-FEC bit error rate of the bursts that decoded, and frames/"
            + seeds + " beneath");
        Console.WriteLine(
            "| rate | " + string.Join(" | ", offsets.Select(o => $"{o:+0;-0;0} dB")) + " |");

        foreach ((OfdmFmConstellation constellation, string label, int num, int den) in Ladder)
        {
            var coded = profile with
            {
                Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, num, den, true),
            };
            var codec = new OfdmFmBurstCodec(coded);
            int cliff = cliffs[$"{constellation} {label}"];

            var bers = new List<string>();
            var counts = new List<string>();
            foreach (int offset in offsets)
            {
                var seen = new List<double>();
                int ok = 0;
                for (int seed = 0; seed < seeds; seed++)
                {
                    var payload = new byte[payloadBytes];
                    new Random(7000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(payload, constellation);
                    float[] heard = new FmChannel(link, coded.SampleRate, seed)
                        .Apply(clean, cliff + offset);
                    OfdmFmBurst? burst = codec.Demodulate(heard);
                    if (burst?.Payload is not null
                        && burst.Payload.AsSpan().SequenceEqual(payload))
                    {
                        ok++;
                        if (burst.PreFecBitErrorRate is double ber)
                        {
                            seen.Add(ber);
                        }
                    }
                }

                seen.Sort();
                bers.Add(seen.Count == 0 ? "    -" : $"{seen[seen.Count / 2]:0.0000}");
                counts.Add($"{ok,5}");
            }

            Console.WriteLine($"| {constellation,-6} {label} | {string.Join(" | ", bers)} |");
            Console.WriteLine($"| {"frames",-10} | {string.Join(" | ", counts)} |");
        }
    }
}
