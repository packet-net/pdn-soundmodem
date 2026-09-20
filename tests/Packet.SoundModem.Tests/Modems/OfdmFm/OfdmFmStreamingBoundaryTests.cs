using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The seams inside the streaming receiver: where one <c>Process</c> call ends and the next
/// begins, and where the window is compacted underneath the incremental sync metric.
/// </summary>
/// <remarks>
/// Separate from <see cref="OfdmFmModemTests"/> because these are not about decoding a burst. The
/// sliding correlation sums are carried across calls and across a memmove, and every one of them
/// reads a sample just before the window it describes. That is the kind of arithmetic that is
/// right for every profile anybody happens to test with and wrong for one nobody did.
/// </remarks>
public class OfdmFmStreamingBoundaryTests
{
    private static float[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        var samples = new float[count];
        for (int n = 0; n < count; n++)
        {
            samples[n] = (float)((random.NextDouble() * 2) - 1);
        }

        return samples;
    }

    [Theory]
    [InlineData(0)]   // no guard at all: the metric window starts at sample 0 of the symbol
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    public void A_Profile_Of_Any_Cyclic_Prefix_Survives_Compaction(int cyclicPrefix)
    {
        // The compaction keeps everything from the search position onward and drops the rest, and
        // the sliding update then reads one sample BEFORE that position. With no cyclic prefix the
        // metric window starts exactly at the search position, so a compaction that kept only from
        // there would leave the next update reading off the front of the buffer.
        var profile = OfdmFmParameters.Synthetic with { CyclicPrefix = cyclicPrefix };
        var delivered = new List<byte[]>();
        var modem = new OfdmFmModem("ofdm-fm:test", profile, delivered.Add);

        Action stream = () =>
        {
            for (int block = 0; block < 40; block++)
            {
                modem.Process(Noise(1200, block));
            }
        };

        stream.Should().NotThrow("cyclic prefix {0}", cyclicPrefix);
        delivered.Should().BeEmpty();
    }

    [Fact]
    public void The_Incremental_Metric_Agrees_With_A_Direct_One_At_Every_Position()
    {
        // The instrument for the sliding sums themselves. Streaming a burst in one-sample calls
        // forces a compaction between almost every pair of positions, so if the update were wrong
        // by a term the search would drift and this would stop finding the burst where the
        // whole-buffer search does.
        var delivered = new List<byte[]>();
        var modem = new OfdmFmModem("ofdm-fm:test", OfdmFmParameters.Synthetic, delivered.Add);
        var payload = new byte[24];
        new Random(5).NextBytes(payload);

        float[] audio = modem.Modulate(payload, txDelayMilliseconds: 0);
        int wholeBuffer = new OfdmFmBurstCodec(OfdmFmParameters.Synthetic)
            .Demodulate(audio)!.StartSample;

        foreach (float sample in audio)
        {
            modem.Process([sample]);
        }

        delivered.Should().ContainSingle().Which.Should().Equal(payload);
        modem.LastSyncAtSample.Should().Be(
            wholeBuffer, "a wrong sliding term would move where the search peaks");
    }

    [Fact]
    public void A_Burst_Split_Across_A_Compaction_Still_Decodes()
    {
        // A block boundary landing inside the burst, after the sync has been committed but before
        // the header is complete, is the case where a compaction has to keep the burst rather than
        // the search window.
        var delivered = new List<byte[]>();
        var modem = new OfdmFmModem("ofdm-fm:test", OfdmFmParameters.Synthetic, delivered.Add);
        var payload = new byte[80];
        new Random(6).NextBytes(payload);

        float[] burst = modem.Modulate(payload, txDelayMilliseconds: 0);
        var stream = new List<float>();
        stream.AddRange(Noise(5000, 1));
        stream.AddRange(burst);
        stream.AddRange(Noise(5000, 2));
        float[] all = [.. stream];

        for (int at = 0; at < all.Length; at += 137)   // a block size no symbol boundary divides
        {
            modem.Process(all.AsSpan(at, Math.Min(137, all.Length - at)));
        }

        delivered.Should().ContainSingle().Which.Should().Equal(payload);
    }

    [Fact]
    public void An_Unmodulated_Carrier_Does_Not_Cost_A_Transform_Per_Sample()
    {
        // A steady tone correlates with itself perfectly and forever, so every sample looks like
        // the top of a sync plateau. Committing at each one means a header read - several
        // transforms - per sample arriving, which does not fall behind gracefully: it falls behind
        // for as long as somebody holds a PTT down on frequency. Measured against real silence
        // rather than against a wall-clock budget, so it says something about the work done rather
        // than about how busy this machine happens to be.
        var profile = OfdmFmParameters.Synthetic;

        // The tone has to repeat over half a transform, or it does not correlate with itself at
        // the lag the search uses and proves nothing. Half a transform is the period, so a tone at
        // SampleRate divided by any factor of it does: this one fits eight cycles into the window
        // and reads as a perfect self-correlation at every single sample.
        double toneHz = profile.SampleRate / 8.0;
        double phase = 2 * Math.PI * toneHz / profile.SampleRate;

        var tone = new float[profile.SampleRate * 2];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = (float)(0.5 * Math.Sin(n * phase));
        }

        long Cost(float[] audio)
        {
            var delivered = new List<byte[]>();
            var modem = new OfdmFmModem("ofdm-fm:test", profile, delivered.Add);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int at = 0; at < audio.Length; at += 1200)
            {
                modem.Process(audio.AsSpan(at, Math.Min(1200, audio.Length - at)));
            }

            delivered.Should().BeEmpty("a tone is not a frame");
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long silent = Cost(new float[tone.Length]);
        long carrier = Cost(tone);
        long twiceTheCarrier = Cost([.. tone, .. tone]);

        // The invariant is that the cost is bounded by the number of header reads, and the budget
        // resets only when the correlation breaks - so a tone that never breaks costs the same
        // whether it runs for two seconds or four. That is the thing worth pinning: it holds no
        // matter what a single header read costs, which a fixed multiple of the silent cost does
        // not. An earlier version of this test asserted such a multiple and had to move the moment
        // a header read legitimately got more expensive, which told us nothing about the defect it
        // was there to catch.
        twiceTheCarrier.Should().BeLessThan(
            carrier + Math.Max(silent, 1024),
            "twice the tone must not cost twice the work - the attempts are spent on the carrier "
            + "appearing, not on its samples going by (two seconds cost {0} bytes, four {1})",
            carrier,
            twiceTheCarrier);

        // And it is still a bounded multiple of doing nothing at all, just a looser one: a header
        // read allocates transforms, tables and an estimate, and a handful of those per carrier-on
        // event is the design. One per sample would be thousands of times this.
        carrier.Should().BeLessThan(
            Math.Max(silent, 1024) * 200,
            "a permanently correlated signal must cost a bounded number of header reads, not one "
            + "per sample (silence cost {0} bytes, the carrier {1})",
            silent,
            carrier);
    }

    [Fact]
    public void A_Burst_Still_Decodes_When_A_Carrier_Has_Already_Spent_The_Attempts()
    {
        // The budget resets when the correlation breaks, so a burst arriving after a carrier drops
        // is not penalised by it.
        var profile = OfdmFmParameters.Synthetic;
        var delivered = new List<byte[]>();
        var modem = new OfdmFmModem("ofdm-fm:test", profile, delivered.Add);
        var payload = new byte[32];
        new Random(7).NextBytes(payload);

        double phase = 2 * Math.PI * (profile.SampleRate / 8.0) / profile.SampleRate;
        var tone = new float[profile.SampleRate / 2];
        for (int n = 0; n < tone.Length; n++)
        {
            tone[n] = (float)(0.5 * Math.Sin(n * phase));
        }

        var stream = new List<float>();
        stream.AddRange(tone);
        stream.AddRange(new float[profile.SymbolSamples * 4]);
        stream.AddRange(modem.Modulate(payload, txDelayMilliseconds: 0));
        float[] all = [.. stream];

        for (int at = 0; at < all.Length; at += 1200)
        {
            modem.Process(all.AsSpan(at, Math.Min(1200, all.Length - at)));
        }

        delivered.Should().ContainSingle().Which.Should().Equal(payload);
    }

    [Fact]
    public void An_Empty_Block_Is_Not_An_Event()
    {
        var delivered = new List<byte[]>();
        var modem = new OfdmFmModem("ofdm-fm:test", OfdmFmParameters.Synthetic, delivered.Add);

        Action empty = () => modem.Process([]);

        empty.Should().NotThrow();
        modem.CarrierDetect.Should().BeFalse();
        delivered.Should().BeEmpty();
    }
}
