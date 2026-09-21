using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// What a sample-clock difference costs through a real noisy link, now that the receiver tracks
/// the tilt it paints.
/// </summary>
/// <remarks>
/// <para>The tilt tracker was proven on a noiseless resampled loopback, which isolates the
/// mechanism and proves nothing about the fit surviving noise: the pooled regression reads pilot
/// angles, and at a cliff the pilot angles are mostly noise. This runs the practical rates near
/// their cliffs with the receiver's clock 0, 100 and 200 ppm off the transmitter's, which brackets
/// what two consumer soundcards do.</para>
/// <para>The skew comes from the link model's own <c>ReceiverClockOffsetPpm</c> (M0LTE.FmChannel
/// 0.7.0), which resamples at the end of its receive chain - where the receiving soundcard sits.
/// This probe's first runs predated that release and applied the identical composition
/// themselves by resampling the model's output. The two resamplers differ only in sub-sample
/// alignment, which re-rolls each seed's noise-versus-signal phasing: re-measured through the
/// knob, the skew cells read the same shape and a few frames kinder in aggregate, about two
/// sigma of that re-roll. The knob run is the record future re-runs compare against.</para>
/// <para>A measurement, not a check: run with <c>OFDMFM_LADDER=1</c>, seeds via
/// <c>OFDMFM_SEEDS</c>.</para>
/// </remarks>
public class ClockSkewLadderProbe
{
    private const double LegalPeakDeviationHz = 2500;

    [Fact]
    public void What_A_Clock_Difference_Costs_Through_A_Noisy_Link()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 for the clock-skew ladder - a measurement, not a check");

        int seeds =
            int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s) ? s : 32;

        // On the 8 kHz preset's layout, stated in OfdmFmTestProfiles rather than read from a
        // station's own geometry file, so this table always describes the waveform it names.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;

        FmLinkProfile link = FmLinkProfile.DataPort(LegalPeakDeviationHz, audioHighHz: null);
        var payload = new byte[256];
        new Random(1000).NextBytes(payload);

        Console.WriteLine(
            $"8 kHz preset, DataPort {LegalPeakDeviationHz} Hz, 256 B, K=9, {seeds} seeds; "
            + "frames recovered at each receiver clock offset");
        Console.WriteLine("| rate | CNR | 0 ppm | 100 ppm | 200 ppm |");

        // The cliffs each sweep is centred on were measured on a narrower span than the preset
        // above, so a window may sit a decibel or two off its cliff until they are re-measured
        // here. Read the recovered counts, not the window.
        foreach ((string label, OfdmFmConstellation constellation, int num, int den, double cliff)
            in (ReadOnlySpan<(string, OfdmFmConstellation, int, int, double)>)
            [
                ("QPSK 3/4", OfdmFmConstellation.Qpsk, 3, 4, 7.4),
                ("QAM-16 3/4", OfdmFmConstellation.Qam16, 3, 4, 13.6),
                ("QAM-64 2/3", OfdmFmConstellation.Qam64, 2, 3, 18.6),
                ("QAM-256 2/3", OfdmFmConstellation.Qam256, 2, 3, 22.9),
            ])
        {
            var coded = profile with
            {
                Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, num, den, true),
            };
            var codec = new OfdmFmBurstCodec(coded);
            float[] clean = codec.Modulate(payload, constellation, 2);

            foreach (double cnr in (ReadOnlySpan<double>)[cliff + 1, cliff + 3])
            {
                var cells = new List<string>();
                foreach (double ppm in (ReadOnlySpan<double>)[0, 100, 200])
                {
                    FmLinkProfile skewed = link with { ReceiverClockOffsetPpm = ppm };
                    int recovered = 0;
                    for (int seed = 0; seed < seeds; seed++)
                    {
                        float[] heard = new FmChannel(skewed, coded.SampleRate, seed)
                            .Apply(clean, cnr);
                        OfdmFmBurst? burst = codec.Demodulate(heard);
                        if (burst?.Payload is byte[] got && got.AsSpan().SequenceEqual(payload))
                        {
                            recovered++;
                        }
                    }

                    cells.Add($"{recovered}/{seeds}");
                }

                Console.WriteLine(
                    $"| {label,-11} | +{cnr,4:0.0} | {cells[0],5} | {cells[1],5} | {cells[2],5} |");
            }
        }
    }
}
