using System.Text;
using Packet.SoundModem.Modems;

using M0LTE.Fm;
using Packet.SoundModem.Modems.OfdmFm;

namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// The coded ladder through the FM link model: the measurement that says what the convolutional
/// code is worth on the path this waveform is for.
/// </summary>
/// <remarks>
/// <para>Gated behind <c>OFDMFM_LADDER=1</c>, because it is a measurement rather than a check and
/// it takes long enough to be annoying on every build.</para>
/// <para>It runs on the 8 kHz preset's layout (<see cref="OfdmFmTestProfiles"/>) and prints the
/// table rather than asserting a threshold, because the numbers move with the layout. What it does
/// assert is the ordering, which cannot: at a carrier-to-noise ratio where the uncoded burst has
/// started to fail, rate 1/2 is doing better, and the puncturing ladder falls in the expected
/// order.</para>
/// <para><b>This ladder no longer separates anything and is kept as a floor, not as an
/// instrument.</b> Over +40 to +16 dB every coded rung now copies every frame and uncoded only
/// starts to fail at the bottom of it, which is the receive-path work since - the coded header,
/// the band-limited search, the estimate denoiser and the crest-factor fixes - showing up as about
/// 8 dB. Use <see cref="CodeRateProbe"/> for anything that needs to tell two codings apart; it runs
/// where they differ.</para>
/// </remarks>
public class OfdmFmFmLadderTests
{
    /// <summary>100 % modulation on a 12.5 kHz channel, which is the channel this
    /// profile belongs on. Tait define it at MMA-00072-03 p.6. Measurements used to run
    /// at 3000 Hz, which is 120 % of that: over-deviation, and it flattered every number
    /// taken through it.</summary>
    private const double LegalPeakDeviationHz = 2500;

    private static readonly int[] CnrDb = [40, 34, 28, 24, 20, 16];
    private const int Seeds = 8;

    [Fact]
    public void The_Coded_Ladder_Still_Holds_Through_The_Fm_Link()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 for the FM coded ladder - a measurement, not a check");

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz);

        (string Label, OfdmFmCoding? Coding)[] schemes =
        [
            ("none", null),
            ("conv 1/2", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true)),
            ("conv 2/3", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 2, 3, true)),
            ("conv 3/4", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 3, 4, true)),
            ("conv 5/6", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 5, 6, true)),
            ("conv 7/8", new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 7, 8, true)),
        ];

        var table = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var report = new StringBuilder();
        report.Append("| coding |");
        foreach (int cnr in CnrDb)
        {
            report.Append($" +{cnr} |");
        }

        report.AppendLine();

        foreach ((string label, OfdmFmCoding? coding) in schemes)
        {
            var codec = new OfdmFmBurstCodec(profile with { Coding = coding });
            var row = new int[CnrDb.Length];
            for (int c = 0; c < CnrDb.Length; c++)
            {
                for (int seed = 0; seed < Seeds; seed++)
                {
                    var payload = new byte[64];
                    new Random(1000 + seed).NextBytes(payload);
                    float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk);
                    float[] heard = new FmChannel(link, profile.SampleRate, seed)
                        .Apply(clean, CnrDb[c]);

                    byte[]? got = codec.Demodulate(heard)?.Payload;
                    if (got is not null && got.AsSpan().SequenceEqual(payload))
                    {
                        row[c]++;
                    }
                }
            }

            table[label] = row;
            report.Append($"| {label} |");
            foreach (int ok in row)
            {
                report.Append($" {ok} |");
            }

            report.AppendLine();
        }

        Console.WriteLine(report.ToString());

        // The ordering, which is what a regression would break and what does not depend on the
        // geometry: somewhere on this ladder the code has to be worth something, and the punctured
        // rates have to fall between uncoded and rate 1/2.
        int[] none = table["none"];
        int[] half = table["conv 1/2"];
        half.Sum().Should().BeGreaterThan(
            none.Sum(), "the rate-1/2 code has to buy frames the uncoded burst loses");
        table["conv 2/3"].Sum().Should().BeLessThanOrEqualTo(half.Sum());
        table["conv 3/4"].Sum().Should().BeLessThanOrEqualTo(table["conv 2/3"].Sum());
        table["conv 5/6"].Sum().Should().BeLessThanOrEqualTo(table["conv 3/4"].Sum());
        table["conv 7/8"].Sum().Should().BeLessThanOrEqualTo(table["conv 5/6"].Sum());

        // And the top of the ladder must be clean for every scheme, or the instrument is broken
        // rather than the modem.
        foreach ((string label, int[] row) in table)
        {
            row[0].Should().Be(Seeds, "{0} must copy every frame at +{1} dB", label, CnrDb[0]);
        }
    }

    [Fact]
    public void The_Streaming_Modem_Holds_The_Same_Ladder_As_The_Codec_It_Wraps()
    {
        // The gap this closes: the ladder above measures OfdmFmBurstCodec, handed a whole burst.
        // A station runs OfdmFmModem, fed 100 ms at a time - a different sync search, a bounded
        // window, an attempt budget and a retry after a failed payload, none of which the codec's
        // own ladder exercises. Quoting the codec's numbers for the modem would be quoting an
        // instrument that is not the thing deployed, which is the failure this project keeps
        // relearning. So: the same link, the same seeds, the same profile, through the modem.
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 for the FM coded ladder - a measurement, not a check");

        // The preset is written at the rate a channel runs it at, so there is one waveform here
        // and no rescale in the path. What rescaling costs the receiver has its own test below.
        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        var link = FmLinkProfile.MicAndSpeaker(LegalPeakDeviationHz);
        var coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true);

        var report = new StringBuilder();
        report.Append("| path |");
        foreach (int cnr in CnrDb)
        {
            report.Append($" +{cnr} |");
        }

        report.AppendLine();

        var totals = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (string path in (ReadOnlySpan<string>)["codec", "streaming"])
        {
            var row = new int[CnrDb.Length];
            for (int c = 0; c < CnrDb.Length; c++)
            {
                for (int seed = 0; seed < Seeds; seed++)
                {
                    var payload = new byte[64];
                    new Random(1000 + seed).NextBytes(payload);

                    var coded = profile with { Coding = coding };
                    var codec = new OfdmFmBurstCodec(coded);
                    float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);
                    float[] heard = new FmChannel(link, coded.SampleRate, seed).Apply(clean, CnrDb[c]);

                    bool copied;
                    if (path != "streaming")
                    {
                        byte[]? got = codec.Demodulate(heard)?.Payload;
                        copied = got is not null && got.AsSpan().SequenceEqual(payload);
                    }
                    else
                    {
                        var delivered = new List<byte[]>();
                        var modem = new OfdmFmModem("ofdm-fm:ladder", coded, delivered.Add);

                        // 100 ms at a time, which is what the daemon hands a modem.
                        int block = coded.SampleRate / 10;
                        for (int at = 0; at < heard.Length; at += block)
                        {
                            modem.Process(heard.AsSpan(at, Math.Min(block, heard.Length - at)));
                        }

                        copied = delivered.Count == 1 && delivered[0].AsSpan().SequenceEqual(payload);
                    }

                    if (copied)
                    {
                        row[c]++;
                    }
                }
            }

            totals[path] = row;
            report.Append($"| {path} |");
            foreach (int ok in row)
            {
                report.Append($" {ok} |");
            }

            report.AppendLine();
        }

        Console.WriteLine(report.ToString());

        // The modem must not be worse than the codec it wraps. It is allowed to be better - the
        // retry after a failed payload finds sync positions the codec's global argmax does not -
        // but a streaming adapter that loses frames the whole-buffer path copies is an adapter
        // with a bug, and this is the only place that would show up on a real channel.
        int[] codecRow = totals["codec"];
        int[] streamingRow = totals["streaming"];
        streamingRow.Sum().Should().BeGreaterThanOrEqualTo(
            codecRow.Sum(),
            "the streaming modem is what a station runs, and it must copy at least what the "
            + "whole-buffer codec does on the same audio (streaming {0}, codec {1})",
            streamingRow.Sum(),
            codecRow.Sum());

        for (int c = 0; c < CnrDb.Length; c++)
        {
            streamingRow[c].Should().BeGreaterThanOrEqualTo(
                codecRow[c] - 1,
                "no single rung may collapse (+{0} dB: streaming {1}, codec {2})",
                CnrDb[c],
                streamingRow[c],
                codecRow[c]);
        }
    }

    [Fact]
    public void Rescaling_Preserves_The_Waveform_And_No_Longer_Costs_The_Sync_Correlator()
    {
        // A measured finding, pinned as the mechanism rather than as a number.
        //
        // The FM ladder used to show a rescaled profile doing worse than the same profile at its
        // own rate. Rescaled() really does preserve the waveform: same bins, same spacing, same
        // symbol duration, and the transmitted peak and RMS come out identical. What it does not
        // preserve is what the RECEIVER's acquisition sees.
        //
        // The payload path is an FFT, which rejects everything outside the occupied bins, so
        // doubling the sample rate costs it nothing. The sync search is a time-domain
        // self-correlation with no filter in front of it, so it sees the whole band: at twice the
        // rate it takes in twice the noise power for the same in-band noise, and the normalised
        // correlation coefficient it thresholds at 0.8 drops by the oversampling ratio. Acquisition
        // therefore loses about 3 dB per octave of oversampling while the payload gains.
        //
        // Measured, on a narrow profile at sigma 0.11, 16 seeds: native fails to acquire 0 times;
        // rescaled with the noise held at the same PER-BIN level fails to acquire 16 of 16; and
        // rescaled with the noise held at the same TIME-DOMAIN level fails 0 and copies more than
        // native, which is the payload's 3 dB showing up. That third row is the control: it is the
        // same rescaled waveform, so a waveform defect could not switch off with the noise
        // convention.
        //
        // FIXED, and this test now defends the fix rather than pinning the defect. The band-limited
        // front end the paragraph above asks for landed as SearchBandLimit, which filters the sync
        // correlator's input alone and leaves the decode path untouched. Re-measured on the same
        // profile at the same sigma: native fails to acquire 1, rescaled per-bin-matched fails 0,
        // rescaled time-domain-matched fails 0. The 16-of-16 collapse is gone entirely, and the
        // rescaled profile now acquires at least as well as its native-rate parent, which is what
        // it should always have done since it is the same waveform.
        //
        // The narrative above is kept because it is the reasoning that found the fix, and because
        // anyone widening the correlator's input again needs to know what that costs.
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("OFDMFM_LADDER") != "1",
            "set OFDMFM_LADDER=1 - a measurement, not a check");

        OfdmFmParameters profile = OfdmFmTestProfiles.EightKhz;
        OfdmFmParameters rescaled = profile.Rescaled(profile.SampleRate * 2)!;

        // The transmitted signal is the same signal, which is the half of the claim that holds.
        (double NativePeak, double NativeRms) sent = Level(profile);
        (double Peak, double Rms) resent = Level(rescaled);
        resent.Peak.Should().BeApproximately(sent.NativePeak, sent.NativePeak * 0.02);
        resent.Rms.Should().BeApproximately(sent.NativeRms, sent.NativeRms * 0.02);

        const double Sigma = 0.11;
        (int NoSync, int Ok) nativeResult = Copy(profile, Sigma);
        (int NoSync, int Ok) perBin = Copy(rescaled, Sigma * Math.Sqrt(2));
        (int NoSync, int Ok) timeDomain = Copy(rescaled, Sigma);

        Console.WriteLine(
            $"rescale at sigma {Sigma}: native no-sync {nativeResult.NoSync} ok {nativeResult.Ok}"
            + $" | rescaled per-bin-matched no-sync {perBin.NoSync} ok {perBin.Ok}"
            + $" | rescaled time-domain-matched no-sync {timeDomain.NoSync} ok {timeDomain.Ok}");

        // The claim under test is per-bin, which is the honest comparison: the same noise density
        // in the band the signal occupies, whatever rate the receiver happens to run at. Before the
        // band limit this was the row that collapsed 16 of 16.
        perBin.NoSync.Should().BeLessThanOrEqualTo(
            nativeResult.NoSync,
            "the correlator is band-limited now, so oversampling no longer takes in noise the "
            + "signal never occupied");
        timeDomain.NoSync.Should().BeLessThanOrEqualTo(
            nativeResult.NoSync,
            "and the easier noise convention cannot be the harder one");
    }

    private static (double Peak, double Rms) Level(OfdmFmParameters geometry)
    {
        float[] tx = new OfdmFmBurstCodec(geometry)
            .Modulate(new byte[64], OfdmFmConstellation.Qpsk, 2);
        double peak = 0;
        double power = 0;
        foreach (float v in tx)
        {
            peak = Math.Max(peak, Math.Abs(v));
            power += (double)v * v;
        }

        return (peak, Math.Sqrt(power / tx.Length));
    }

    /// <summary>How a profile fares at one noise level: bursts that never acquired, and bursts
    /// that came back whole.</summary>
    private static (int NoSync, int Ok) Copy(OfdmFmParameters geometry, double sigma)
    {
        var codec = new OfdmFmBurstCodec(geometry);
        int noSync = 0;
        int ok = 0;
        for (int seed = 0; seed < 16; seed++)
        {
            var payload = new byte[64];
            new Random(2000 + seed).NextBytes(payload);
            float[] clean = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);

            var random = new Random(seed);
            var noisy = new float[clean.Length];
            for (int n = 0; n < clean.Length; n++)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                noisy[n] = (float)(clean[n] + (sigma * gauss));
            }

            OfdmFmBurst? burst = codec.Demodulate(noisy);
            if (burst is null)
            {
                noSync++;
            }
            else if (burst.Payload is not null && burst.Payload.AsSpan().SequenceEqual(payload))
            {
                ok++;
            }
        }

        return (noSync, ok);
    }
}
