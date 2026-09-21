using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// Run the loop closed and see whether it finds the right rate, and what it gives up doing so.
/// </summary>
/// <remarks>
/// <para>Every part of negotiation has been measured on its own: the ladder, the margin measure,
/// the step policy. None of that says the loop works. A controller can be right about every burst
/// and still settle on the wrong rung, or find the right one and spend so long probing past it that
/// it delivers less than sitting still would have.</para>
/// <para>So: one direction of a real link at a fixed carrier-to-noise ratio. The transmitter sends
/// at whatever the receiver last asked for, the receiver measures what arrived and asks for
/// something else, and this counts what actually got through. <b>The comparison is against an
/// oracle</b> - every fixed rate run over the same link and the same seeds, with the best of them
/// taken afterwards. That is a station that knew the right answer in advance and never had to find
/// it, so adaptation cannot beat it and the gap to it is the price of not knowing.</para>
/// <para>Goodput here counts the air time of every burst, including the ones that failed. A probe
/// that overreaches costs the frame AND the time it spent losing it, which is the cost that makes
/// stepping up expensive and has to be in the number or the policy looks better than it is.</para>
/// </remarks>
public class NegotiationLoopProbe
{
    [Fact]
    public void Does_The_Loop_Find_The_Right_Rate()
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
        int bursts = int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_BURSTS"), out int b)
            ? b
            : 200;
        int[] cnrDb = (Environment.GetEnvironmentVariable("OFDMFM_CNR") ?? "8,12,16,20,24")
            .Split(',')
            .Select(int.Parse)
            .ToArray();
        var link = TaitTm8100.Link(TaitBandwidth.Narrow, 2500);

        var codecs = new OfdmFmBurstCodec[OfdmFmRateLadder.Rungs.Count];
        for (int r = 0; r < codecs.Length; r++)
        {
            codecs[r] = new OfdmFmBurstCodec(
                profile with { Coding = OfdmFmRateLadder.Rungs[r].Coding });
        }

        Console.WriteLine(
            $"# R1/T13 narrow 2500 Hz, {payloadBytes}-byte payload, {bursts} bursts per cell");
        Console.WriteLine("| CNR | adapting | best fixed | reached | settled on | probes lost |");

        foreach (int cnr in cnrDb)
        {
            // The oracle: every fixed rung over the same link, best taken afterwards.
            double bestFixed = 0;
            int bestRung = -1;
            for (int r = 0; r < codecs.Length; r++)
            {
                (double delivered, _) = Run(
                    codecs, profile, link, payloadBytes, bursts, cnr, fixedRung: r);
                if (delivered > bestFixed)
                {
                    bestFixed = delivered;
                    bestRung = r;
                }
            }

            (double adapting, string trace) = Run(
                codecs, profile, link, payloadBytes, bursts, cnr, fixedRung: -1);

            Console.WriteLine(
                $"| +{cnr,2} | {adapting,5:0} | {bestFixed,5:0} ({OfdmFmRateLadder.Rungs[bestRung].Constellation} "
                + $"{bestRung}) | {100 * adapting / Math.Max(bestFixed, 1),3:0} % | {trace} |");
        }
    }

    // One direction of a link. fixedRung >= 0 pins the rate; -1 lets the controller drive it.
    private static (double GoodputBitsPerSecond, string Trace) Run(
        OfdmFmBurstCodec[] codecs,
        OfdmFmParameters profile,
        FmLinkProfile link,
        int payloadBytes,
        int bursts,
        int cnrDb,
        int fixedRung)
    {
        var controller = new OfdmFmRateController();
        var visits = new int[OfdmFmRateLadder.Rungs.Count];
        int delivered = 0;
        int lostProbes = 0;
        int downOnGoodBursts = 0;
        int downOnFailures = 0;
        double airSeconds = 0;
        int lastRung = -1;

        for (int n = 0; n < bursts; n++)
        {
            int rung = fixedRung >= 0
                ? fixedRung
                : OfdmFmRateLadder.IndexOf(
                    controller.Recommendation.Constellation, controller.Recommendation.Coding);
            visits[rung]++;

            OfdmFmRate rate = OfdmFmRateLadder.Rungs[rung];
            OfdmFmBurstCodec codec = codecs[rung];
            var payload = new byte[payloadBytes];
            new Random(9000 + n).NextBytes(payload);

            float[] clean = codec.Modulate(payload, rate.Constellation);
            airSeconds += clean.Length / (double)profile.SampleRate;
            float[] heard = new FmChannel(link, profile.SampleRate, n).Apply(clean, cnrDb);

            OfdmFmBurst? burst = codec.Demodulate(heard);
            bool copied = burst?.Payload is not null
                && burst.Payload.AsSpan().SequenceEqual(payload);
            if (copied)
            {
                delivered++;
            }
            else if (rung > lastRung && lastRung >= 0)
            {
                // Failed on a burst at a rung we had just climbed to: the cost of a probe that
                // overreached, which is what the policy is trading against.
                lostProbes++;
            }

            lastRung = rung;
            if (fixedRung < 0)
            {
                controller.Heard(burst ?? Empty(rate));
                int after = OfdmFmRateLadder.IndexOf(
                    controller.Recommendation.Constellation, controller.Recommendation.Coding);
                if (after < rung)
                {
                    // Why it retreated, which is the difference between a policy that is careful
                    // and one that is twitchy. A burst that decoded is PROOF the rate works.
                    if (copied)
                    {
                        downOnGoodBursts++;
                    }
                    else
                    {
                        downOnFailures++;
                    }
                }
            }
        }

        int settled = Array.IndexOf(visits, visits.Max());
        return (
            delivered * payloadBytes * 8 / airSeconds,
            fixedRung >= 0
                ? string.Empty
                : $"{OfdmFmRateLadder.Rungs[settled].Constellation} {settled} "
                    + $"({100 * visits[settled] / bursts} %) | {lostProbes,3} | "
                    + $"down on {downOnFailures} failures, {downOnGoodBursts} good bursts");
    }

    // Nothing arrived at all: no header, so not even a rate to attribute it to. Reported to the
    // controller as an uncopied burst at the rate we believe is in use, because "silence" and "a
    // burst that failed" mean the same thing to a policy - the far end is sending something this
    // station cannot copy.
    private static OfdmFmBurst Empty(OfdmFmRate rate) =>
        new(null, rate.Constellation, 0, rate.Coding);
}
