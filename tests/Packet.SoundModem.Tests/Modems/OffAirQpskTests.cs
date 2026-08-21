using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;
using Packet.SoundModem.MultiDecode;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The off-air QPSK fixture of issue #326: a real <c>qpsk600</c> burst off an FM receiver at
/// +1.7 dB (3 kHz-referenced), committed because nothing in the tree could copy it. Its frame
/// was recovered on the bench (the folder's README carries it) and the catalogue receiver has
/// copied it since the timing-diversity and clock-hold work of PR #330. This is the regression
/// test the folder was committed to be: the burst sits within two bytes of the Reed-Solomon
/// limit, so a receive-path change that costs it is a change that costs real traffic.
/// </summary>
public class OffAirQpskTests
{
    private static string Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "samples", "offair", "2026-08-21", "packet-24738.wav");
    }

    /// <summary>The TARPN ID beacon in the burst, as the README records it.</summary>
    private static readonly byte[] Beacon = Convert.FromHexString(
        "928840404040E09C6492A4B4406503F04E3249525A2D322020444F4E20202068" +
        "747470733A2F2F746172706E2E6E6574202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E" +
        "2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E" +
        "2E2E2E2E2E200D");

    [Fact]
    public void The_Off_Air_Qpsk600_Burst_Copies_Through_The_Catalogue_Receiver()
    {
        var (samples, sampleRate, _) = WavFile.ReadChannel(Fixture());
        int dspRate = ModemCatalog.DspRateFor("qpsk600");
        float[] audio = Resampler.Resample(samples, sampleRate, dspRate);

        var frames = new List<byte[]>();
        var qualities = new List<FrameQuality>();
        IModem rx = ModemCatalog.Create("qpsk600", dspRate, frames.Add);
        rx.FrameDecoded += (_, quality) => qualities.Add(quality);
        int block = dspRate / 10;
        for (int pos = 0; pos < audio.Length; pos += block)
        {
            rx.Process(audio.AsSpan(pos, Math.Min(block, audio.Length - pos)));
        }

        frames.Should().ContainSingle("the burst holds one frame and the bank delivers it once")
            .Which.Should().Equal(Beacon);
        qualities.Should().ContainSingle().Which.CrcValid.Should().BeTrue();
    }
}
