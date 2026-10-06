using System.Globalization;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Ms110d;

namespace Packet.SoundModem.Tests.Ms110d;

/// <summary>
/// The channel-sounding probe goes out on the mailcast frequency between MS110D bursts, so every
/// MS110D receiver listening there hears it. These pin that none of them mistakes it for a
/// preamble: the sync search never comes near its accept threshold over the probe, and a whole
/// receiver fed the whole keyup locks on nothing and decodes nothing.
/// </summary>
/// <remarks>
/// <para><b>Exhaustive over what the search tries.</b> The receiver runs its search at every
/// half-symbol position (4800 a second) and, at each, the seven frequency bins it searches
/// (-75 to +75 Hz in 25 Hz steps); a receiver still searching has evaluated all of them, so its
/// <see cref="Ms110dDemodulator.PeakSearchMetric"/> after the probe is the largest metric over
/// every lag and every bin. What the search cannot try for itself is where between its samples
/// the probe lands and how far off frequency it arrives, so those are swept here: ten timing
/// offsets of a 48 kHz sample each (one half-symbol in all, which covers both decimator phases)
/// and carrier offsets on the bins, between them and past the outer ones. Over a wider sweep
/// (2026-10-06, ten offsets from -87.5 to +87.5 Hz) the maximum barely moves, 0.2379 to 0.2391:
/// the probe is periodic and the metric sums segment magnitudes, so an offset inside the bins'
/// reach changes little. For comparison, 7 s of white noise reaches 0.29 to 0.30 on the same
/// search, so the probe is quieter to it than an empty band.</para>
/// <para><b>Deterministic.</b> No noise and no clock: the probe is the worst case clean, since
/// noise only adds energy to the metric's denominator.</para>
/// </remarks>
public class Ms110dProbeFalseSyncTests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    /// <summary>The margin asked for: the accept threshold is 0.32.</summary>
    private const double Target = 0.24;

    [Fact]
    public void The_Sync_Search_Stays_Well_Below_Its_Threshold_At_Every_Lag_Bin_And_Offset()
    {
        double threshold = new Ms110dDemodOptions().SyncThreshold;
        threshold.Should().Be(0.32, "the margin below is stated against the receiver's own threshold");

        double[] carrierOffsets = [-87.5, -37.5, 0, 12.5, 62.5];
        var cases = new List<(double Offset, int Lead)>();
        foreach (double offset in carrierOffsets)
        {
            for (int lead = 0; lead < 10; lead++)
            {
                cases.Add((offset, lead));
            }
        }

        Dictionary<double, float[]> rendered = carrierOffsets.ToDictionary(
            offset => offset, offset => ProbeSignal.Render(ProbeSignal.Zc255, 1800 + offset, 0.8, Rate));
        var results = new (double Metric, bool Left)[cases.Count];
        Parallel.For(0, cases.Count, i =>
        {
            (double offset, int lead) = cases[i];
            float[] probe = rendered[offset];
            var modem = new Ms110dModem(Rate, _ => { });
            bool left = false;
            modem.Receiver.FrameDiagnostics += line => left |= line.StartsWith("accept@", StringComparison.Ordinal);
            modem.Process(new float[lead]);
            for (int at = 0; at < probe.Length; at += 4800)
            {
                modem.Process(probe.AsSpan(at, Math.Min(4800, probe.Length - at)));
                left |= modem.CarrierDetect;
            }

            modem.Process(new float[Rate / 2]);
            results[i] = (modem.Receiver.PeakSearchMetric, left || modem.CarrierDetect);
        });

        int worst = 0;
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].Metric > results[worst].Metric)
            {
                worst = i;
            }
        }

        foreach (double offset in carrierOffsets)
        {
            double max = cases.Select((c, i) => (c, i)).Where(x => x.c.Offset == offset).Max(x => results[x.i].Metric);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"carrier {offset,6:+0.0;-0.0} Hz: peak metric {max:F4}"));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"maximum {results[worst].Metric:F4} at {cases[worst].Offset:+0.0;-0.0} Hz, lead {cases[worst].Lead} samples "
            + $"(threshold {threshold:F2}, target {Target:F2})"));

        results.Should().OnlyContain(r => !r.Left, "the search never accepts a peak, so it searched every lag");
        results[worst].Metric.Should().BeLessThan(Target);
        results[worst].Metric.Should().BeGreaterThan(0, "the probe was heard at all");
    }

    [Fact]
    public void A_Whole_Receiver_Fed_The_Whole_Keyup_Locks_On_Nothing_And_Decodes_Nothing()
    {
        // What a mailcast listener hears in a slot: the 10 s calibration tone, the gap, the probe,
        // and quiet after - straight through the station's own 48 kHz receive path.
        float[] tone = new TestTone([1800], 0.8, Rate, 10).Render();
        float[] probe = ProbeSignal.Render(ProbeSignal.Zc255, 1800, 0.8, Rate);
        var keyup = new float[Rate + tone.Length + (int)(1.5 * Rate) + probe.Length + (2 * Rate)];
        tone.CopyTo(keyup, Rate);
        probe.CopyTo(keyup, Rate + tone.Length + (int)(1.5 * Rate));

        int frames = 0, decoded = 0;
        var accepted = new List<string>();
        var modem = new Ms110dModem(Rate, _ => frames++);
        modem.FrameDecoded += (_, _) => decoded++;
        modem.Receiver.FrameDiagnostics += line =>
        {
            if (line.StartsWith("accept@", StringComparison.Ordinal))
            {
                accepted.Add(line);
            }
        };

        bool locked = false;
        for (int at = 0; at < keyup.Length; at += 4800)
        {
            modem.Process(keyup.AsSpan(at, Math.Min(4800, keyup.Length - at)));
            locked |= modem.CarrierDetect;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"peak search metric over the keyup {modem.Receiver.PeakSearchMetric:F4}"));
        accepted.Should().BeEmpty("no sync peak is ever accepted");
        locked.Should().BeFalse();
        frames.Should().Be(0);
        decoded.Should().Be(0);
        modem.LocksReleased.Should().Be(0);
        modem.LockedWaveformNumber.Should().BeNull();
    }
}
