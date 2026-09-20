using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// What is actually limiting this modem: acquisition, or decoding?
/// </summary>
/// <remarks>
/// The ladder tables have zeros in them, and a zero on a bench is a claim about physics that
/// wants checking, because real hardware carries data over an FM voice path at these rates. So:
/// the same bursts, the same link, the same seeds, decoded two ways. Once through the sync search
/// the modem really uses, and once with the burst's position handed to it - a genie that cannot
/// exist on air, whose only job is to say how much of the failure belongs to finding the burst
/// rather than to reading it.
///
/// The true position is taken once from a noiseless run of the same link, which is legitimate
/// because the link's filters and resamplers are deterministic: the seed fixes the fade and the
/// noise realisation, and the burst lands at the same sample whatever the carrier-to-noise ratio.
/// </remarks>
public class AcquisitionOracleTests
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    private static readonly int[] CnrDb = [28, 24, 20, 16, 14, 12, 10, 8];
    private static readonly int Seeds =
        int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 8;

    [Fact]
    public void How_Much_Of_The_Failure_Is_Finding_The_Burst_Rather_Than_Reading_It()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 - a measurement, not a check");

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        var coded = profile with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        };
        var codec = new OfdmFmBurstCodec(coded);
        // R1/T13 on a Tait TM8100 is a tap at the discriminator and a tap straight into the
        // modulator, so nothing in the radio's audio chain is in circuit: no 300 Hz high pass,
        // no 3 kHz low pass, no emphasis pair, no limiter. That is the data-port model, not the
        // microphone one, and it is what this waveform is actually deployed through.
        FmLinkProfile link = Environment.GetEnvironmentVariable("OFDMFM_LINK") == "mic"
            ? FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz)
            : FmLinkProfile.DataPort(
                LegalPeakDeviationHz,
                audioHighHz: double.TryParse(
                    Environment.GetEnvironmentVariable("OFDMFM_AUDIOHIGH"), out double h)
                    ? h
                    : null);

        Console.WriteLine($"--- 8 kHz preset ({coded.SampleRate} Hz) ---");
        Console.WriteLine(
            "| CNR | searched | genie | never acquired | payload failed |");

        foreach (int cnr in CnrDb)
        {
            int searched = 0;
            int genie = 0;
            int neverAcquired = 0;
            int payloadFailed = 0;

            for (int seed = 0; seed < Seeds; seed++)
            {
                var payload = new byte[64];
                new Random(1000 + seed).NextBytes(payload);
                float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);

                // Where the burst lands after this link, taken from a noiseless pass of the
                // very same link and seed. Deterministic filters, so it is the same sample at
                // every carrier-to-noise ratio below.
                float[] quiet = new FmChannel(link, coded.SampleRate, seed)
                    .Apply(clean, double.PositiveInfinity);
                OfdmFmBurst? truth = codec.Demodulate(quiet);
                if (truth?.Payload is null)
                {
                    continue;   // the link cannot carry it even noiselessly; not a fair rung
                }

                // FindSync's own noiseless answer is NOT ground truth: it is the argmax of a
                // plateau, and it can sit at the edge of the window that decodes. Handing that
                // to the genie measures the search twice and calls the second one an oracle.
                // So: sweep the noiseless burst for every offset that decodes and take the
                // middle of that range, which is the position with the most margin either way.
                int best = Centre(codec, quiet, truth.StartSample, coded, payload);

                float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, cnr);

                OfdmFmBurst? found = codec.Demodulate(heard);
                if (found is null)
                {
                    neverAcquired++;
                }
                else if (found.Payload is null
                    || !found.Payload.AsSpan().SequenceEqual(payload))
                {
                    payloadFailed++;
                }
                else
                {
                    searched++;
                }

                byte[]? given = codec.DecodeAt(heard, best)?.Payload;
                if (given is not null && given.AsSpan().SequenceEqual(payload))
                {
                    genie++;
                }
            }

            Console.WriteLine(
                $"| +{cnr} | {searched} | {genie} | {neverAcquired} | {payloadFailed} |");
        }
    }

    /// <summary>
    /// The best-centred sync position for a noiseless burst: every offset near
    /// <paramref name="found"/> that decodes, and the middle of that run. A genie handed the edge
    /// of the decodable window is not a genie, it is the search wearing a hat.
    /// </summary>
    private static int Centre(
        OfdmFmBurstCodec codec,
        float[] quiet,
        int found,
        OfdmFmParameters geometry,
        byte[] payload)
    {
        int reach = geometry.CyclicPrefix * 2;
        int first = int.MaxValue;
        int last = int.MinValue;
        for (int at = Math.Max(0, found - reach); at <= found + reach; at++)
        {
            byte[]? got = codec.DecodeAt(quiet, at)?.Payload;
            if (got is not null && got.AsSpan().SequenceEqual(payload))
            {
                first = Math.Min(first, at);
                last = Math.Max(last, at);
            }
        }

        return first == int.MaxValue ? found : (first + last) / 2;
    }
}
