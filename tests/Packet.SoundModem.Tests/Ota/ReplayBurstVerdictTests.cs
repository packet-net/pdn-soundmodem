using System.Globalization;
using M0LTE.Il2p;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.Ota;

namespace Packet.SoundModem.Tests.Ota;

/// <summary>
/// The burst-verdict instrument end to end: a synthetic raw-capture chunk with one decodable
/// burst and one damaged-beyond-Reed-Solomon burst goes through <c>sm-ota replay --bursts</c>
/// and comes out as exactly two verdict rows - the decoded one and the DAMAGED-BPSK one, with
/// the sync-found-but-RS-failed delta on the latter. This is the deterministic version of what
/// the 40 m full-day run does to real air.
/// </summary>
public class ReplayBurstVerdictTests
{
    private const int SampleRate = 12000;

    [Fact]
    public void A_Replayed_Chunk_Yields_One_Verdict_Row_Per_Dcd_Burst()
    {
        byte[] frame = Modems.Il2pReceiverTests.Gb7bpqBeacon();
        var audio = new List<float>();
        void Silence(double seconds) => audio.AddRange(new float[(int)(seconds * SampleRate)]);

        Silence(2);
        double goodStart = audio.Count / (double)SampleRate;
        audio.AddRange(BpskModem.Bpsk300(SampleRate, static _ => { })
            .Modulate(frame, txDelayMilliseconds: 300));
        double goodEnd = audio.Count / (double)SampleRate;
        Silence(5);
        double damagedStart = audio.Count / (double)SampleRate;
        audio.AddRange(DamagedBurst(frame));
        Silence(3);

        DirectoryInfo dir = Directory.CreateTempSubdirectory("burst-verdicts-");
        try
        {
            WavFile.WriteMono(
                Path.Combine(dir.FullName, "raw-20260101T000000Z.wav"),
                audio.ToArray(), SampleRate);
            string csv = Path.Combine(dir.FullName, "bursts.csv");

            int code = ReplayCommand.Run(
            [
                "--raw", dir.FullName, "--mode", "bpsk300@1500",
                "--bursts", csv, "--workers", "1", "--quiet",
            ]);

            code.Should().Be(0);
            string[] rows = File.ReadAllLines(csv);
            rows[0].Should().Be(
                "utc_start,utc_end,dcd_seconds,decoded,frames,offset_hz,rs_failures,crc_failures");
            rows.Should().HaveCount(3, "two transmissions were on the air, so two verdict rows");

            var chunkStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            string[] good = rows[1].Split(',');
            SecondsInto(good[0], chunkStart).Should().BeInRange(goodStart, goodStart + 1,
                "the first row opens where the first burst's preamble asserted DCD");
            SecondsInto(good[1], chunkStart).Should().BeInRange(goodStart + 0.5, goodEnd + 0.5);
            double.Parse(good[2], CultureInfo.InvariantCulture).Should().BeGreaterThan(0.7,
                "a real burst holds DCD for most of its length");
            good[3].Should().Be("1", "the first burst decodes");
            int.Parse(good[4], CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
            double.Parse(good[5], CultureInfo.InvariantCulture).Should().BeApproximately(0, 2,
                "the frame's own offset reading rides along, and the signal was on frequency");

            string[] damaged = rows[2].Split(',');
            SecondsInto(damaged[0], chunkStart).Should().BeInRange(damagedStart, damagedStart + 1);
            damaged[3].Should().Be("0", "the second burst is beyond Reed-Solomon");
            damaged[4].Should().Be("0");
            long.Parse(damaged[6], CultureInfo.InvariantCulture).Should().BeGreaterThan(0,
                "sync was found and the frame refused - the DAMAGED-BPSK verdict, distinct "
                + "from a foreign signal that never syncs at all");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static double SecondsInto(string iso, DateTimeOffset chunkStart) =>
        (DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
            - chunkStart).TotalSeconds;

    /// <summary>The same corruption as <c>BpskMultiModemTests.DamagedBurst</c>: sync intact,
    /// payload wrecked beyond Reed-Solomon, so DCD asserts and nothing decodes.</summary>
    private static float[] DamagedBurst(byte[] frame)
    {
        byte[] wire = Il2pCodec.Encode(frame, appendCrc: true);
        for (int i = Il2pCodec.HeaderWireLength; i < wire.Length; i += 2)
        {
            wire[i] ^= 0xA5;
        }

        byte[] bits = Il2pFramer.FrameBits(wire, preambleBits: 90, Il2pFramer.PreambleStyle.Zeros);
        return new BpskModulator(SampleRate).Modulate(bits);
    }
}
