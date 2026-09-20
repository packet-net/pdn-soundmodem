using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using M0LTE.Fm;

/// <summary>
/// What each code rate costs and what it buys, on the link this waveform actually runs on.
/// </summary>
/// <remarks>
/// <para>The question a station has to answer once rate is signalled: given a link, which of the
/// seven codings should the transmitter be set to? The coded ladder in
/// <see cref="OfdmFmFmLadderTests"/> cannot answer it - it runs from +40 down to +16 dB, and every
/// coding now copies every frame across that whole span, so it separates nothing. This runs low
/// enough to separate them, and reports the two numbers that matter together.</para>
/// <para><b>Frames copied is the wrong headline on its own.</b> A rate 3/4 burst carries the same
/// payload in fewer symbols, so it is shorter and off the air sooner. Comparing codings on
/// robustness alone always picks the strongest code, which is only right if air time is free.
/// The goodput column is payload bits actually delivered per second of transmission, which is what
/// a channel shared with other stations cares about.</para>
/// <para>Legs are NOT paired across codings and must not be read as if they were. A different code
/// rate means a different number of coded bits, so a different burst length, and
/// <c>FmChannel.FadingGains()</c> draws its channel from a generator whose length depends on that.
/// Two legs "at the same seeds" see different channels. That is why the seed count is high: these
/// are independent samples, not a matched-pairs comparison.</para>
/// </remarks>
public class CodeRateProbe
{
    /// <summary>
    /// Payload bytes per burst. <b>Not a detail:</b> a burst is a whole number of symbols, so a
    /// short payload rounds up and several rungs of the ladder come out the same length as each
    /// other. Measure at the size the link will actually carry, or half the ladder is an artefact
    /// of the rounding.
    /// </summary>
    private static int PayloadBytes =>
        int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_PAYLOAD"), out int p) ? p : 64;

    private static readonly (string Label, OfdmFmCoding Coding)[] Codings =
    [
        ("none    ", new OfdmFmCoding()),
        ("K7 1/2  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true)),
        ("K7 2/3  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 2, 3, true)),
        ("K7 3/4  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 3, 4, true)),
        ("K7 5/6  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 5, 6, true)),
        ("K7 7/8  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 7, 8, true)),
        ("K9 1/2  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 1, 2, true)),
        ("K9 2/3  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true)),
        ("K9 3/4  ", new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 3, 4, true)),
    ];

    /// <summary>
    /// The other axis a header can name, measured the same way so the two can be compared.
    /// </summary>
    /// <remarks>
    /// Which matters for the same reason the code-rate table does: once rate is signalled, a
    /// station has to decide what to move and by how much. If one axis is worth several decibels a
    /// step and the other is worth a fraction of one, an adaptation scheme that spends its time on
    /// the second is solving the wrong problem. Same link, same payload, same seeds, one coding
    /// held fixed at the deployed one.
    /// </remarks>
    [Fact]
    public void And_What_Is_The_Constellation_Worth_By_Comparison()
    {
        // OFDMFM_PROBE, not OFDMFM_LADDER. These are campaign instruments that sweep a grid at 64
        // seeds a cell and run for several minutes each; the ladder gate is meant to stay something
        // a person will actually wait for.
        if (Environment.GetEnvironmentVariable("OFDMFM_PROBE") is null)
        {
            return;
        }

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s)
            ? s
            : 64;
        int[] cnrDb = (Environment.GetEnvironmentVariable("OFDMFM_CCNR") ?? "30,26,22,18,14,10,6,4")
            .Split(',')
            .Select(int.Parse)
            .ToArray();

        var coded = profile with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        };
        var codec = new OfdmFmBurstCodec(coded);
        var link = TaitTm8100.Link(TaitBandwidth.Narrow, 2500);

        Console.WriteLine(
            $"--- R1/T13, narrow, 2500 Hz, K7 1/2, {PayloadBytes}-byte payload, {seeds} seeds ---");
        Console.WriteLine(
            "| constellation | ms | "
            + string.Join(" | ", cnrDb.Select(c => $"+{c}")) + " |   goodput bit/s at each |");

        foreach (OfdmFmConstellation constellation in (ReadOnlySpan<OfdmFmConstellation>)
            [
                OfdmFmConstellation.Bpsk,
                OfdmFmConstellation.Qpsk,
                OfdmFmConstellation.Psk8,
                OfdmFmConstellation.Qam16,
                OfdmFmConstellation.Qam32,
                OfdmFmConstellation.Qam64,
                OfdmFmConstellation.Qam128,
                OfdmFmConstellation.Qam256,
            ])
        {
            var frames = new List<int>();
            var goodput = new List<string>();
            double burstMs = 0;

            foreach (int cnr in cnrDb)
            {
                int ok = 0;
                for (int seed = 0; seed < seeds; seed++)
                {
                    var payload = new byte[PayloadBytes];
                    new Random(7000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(payload, constellation);
                    burstMs = clean.Length * 1000.0 / coded.SampleRate;
                    float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, cnr);
                    byte[]? got = codec.Demodulate(heard)?.Payload;
                    if (got is not null && got.AsSpan().SequenceEqual(payload))
                    {
                        ok++;
                    }
                }

                frames.Add(ok);
                goodput.Add($"{PayloadBytes * 8 * ((double)ok / seeds) / (burstMs / 1000):0}");
            }

            Console.WriteLine(
                $"| {constellation,-13} | {burstMs,4:0} | "
                + string.Join(" | ", frames.Select(f => $"{f,3}"))
                + " | " + string.Join(" ", goodput.Select(g => $"{g,5}")) + " |");
        }

        Console.WriteLine();
    }

    [Fact]
    public void Which_Code_Rate_Should_A_Station_Transmit_At()
    {
        if (Environment.GetEnvironmentVariable("OFDMFM_LADDER") is null)
        {
            return;
        }

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("OFDMFM_SEEDS"), out int s)
            ? s
            : 64;
        int[] cnrDb = (Environment.GetEnvironmentVariable("OFDMFM_CNR") ?? "16,14,12,10,8,6,4")
            .Split(',')
            .Select(int.Parse)
            .ToArray();

        // The taps we actually use, both offered because the answer could depend on the path and
        // saying which one it was measured on is half the result. R1/T13 is flat and unlimited and
        // is what an internal accessory board reaches; the microphone path is emphasised and
        // limited and is what a dongle on the front panel reaches.
        (string Label, FmLinkProfile Link)[] links =
        [
            ("R1/T13, narrow, 2500 Hz", TaitTm8100.Link(TaitBandwidth.Narrow, 2500)),
            ("mic/speaker, 2500 Hz", FmLinkProfile.MicAndSpeaker(2500)),
        ];

        foreach ((string linkLabel, FmLinkProfile link) in links)
        {
            Console.WriteLine($"--- {linkLabel}, QPSK, {PayloadBytes}-byte payload, {seeds} seeds ---");
            Console.WriteLine(
                "| coding | ms | "
                + string.Join(" | ", cnrDb.Select(c => $"+{c}")) + " |   goodput bit/s at each |");

            foreach ((string label, OfdmFmCoding coding) in Codings)
            {
                var coded = profile with { Coding = coding };
                var codec = new OfdmFmBurstCodec(coded);
                var frames = new List<int>();
                var goodput = new List<string>();
                double burstMs = 0;

                foreach (int cnr in cnrDb)
                {
                    int ok = 0;
                    for (int seed = 0; seed < seeds; seed++)
                    {
                        var payload = new byte[PayloadBytes];
                        new Random(7000 + seed).NextBytes(payload);
                        float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk);
                        burstMs = clean.Length * 1000.0 / coded.SampleRate;
                        float[] heard = new FmChannel(link, coded.SampleRate, seed)
                            .Apply(clean, cnr);
                        byte[]? got = codec.Demodulate(heard)?.Payload;
                        if (got is not null && got.AsSpan().SequenceEqual(payload))
                        {
                            ok++;
                        }
                    }

                    frames.Add(ok);

                    // Payload bits delivered per second of transmission: the whole burst is paid
                    // for whether it decodes or not, so a failed one is air time spent for nothing.
                    goodput.Add($"{PayloadBytes * 8 * ((double)ok / seeds) / (burstMs / 1000):0}");
                }

                Console.WriteLine(
                    $"| {label} | {burstMs,4:0} | "
                    + string.Join(" | ", frames.Select(f => $"{f,3}"))
                    + " | " + string.Join(" ", goodput.Select(g => $"{g,5}")) + " |");
            }

            Console.WriteLine();
        }
    }
}
