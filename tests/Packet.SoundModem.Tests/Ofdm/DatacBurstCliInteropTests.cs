using System.Diagnostics;
using Packet.SoundModem.Ofdm;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>
/// Live CLI-tools interop: our burst transmissions decoded by codec2's own
/// <c>freedv_data_raw_rx</c> binary — the full FreeDV-tools loop in the ours→codec2 direction
/// (the codec2→ours direction is the checked-in-vector tests in <see cref="DatacBurstTests"/>).
/// Runs only when <c>CODEC2_BUILD_SRC</c> points at a codec2 <c>build/src</c> directory
/// containing <c>freedv_data_raw_rx</c> and <c>libcodec2.so</c> (codec2 1.2.0, git 310777b —
/// the pin the checked-in vectors were generated with).
/// </summary>
[Trait("Category", "Interop")]
public class DatacBurstCliInteropTests(ITestOutputHelper output)
{
    private static string? Codec2BuildSrc()
    {
        string? dir = Environment.GetEnvironmentVariable("CODEC2_BUILD_SRC");
        return dir is not null && File.Exists(Path.Combine(dir, "freedv_data_raw_rx")) ? dir : null;
    }

    private static byte[] Payload(int nbytes, int packet)
    {
        var p = new byte[nbytes];
        for (int i = 0; i < nbytes; i++)
        {
            p[i] = (byte)(((packet * 31) + (i * 17) + 5) & 0xff);
        }

        return p;
    }

    [SkippableTheory]
    [InlineData("datac0", 14, 5)]
    [InlineData("datac3", 126, 2)]
    public void Our_Burst_Transmission_Decodes_On_Codec2_Freedv_Data_Raw_Rx(string modeName, int payloadBytes, int bursts)
    {
        string? buildSrc = Codec2BuildSrc();
        Skip.If(buildSrc is null, "CODEC2_BUILD_SRC not set / freedv_data_raw_rx not found");

        OfdmMode mode = modeName == "datac0" ? OfdmMode.Datac0 : OfdmMode.Datac3;
        var payloads = Enumerable.Range(0, bursts).Select(p => Payload(payloadBytes, p)).ToList();

        // One burst per payload, framed exactly as freedv_data_raw_tx does: initial silence, then
        // [burst][2 modem frames of silence] each. codec2's RX defaults to framesperburst 1.
        var tx = new DatacTransmitter(mode);
        int silence = 2 * mode.SamplesPerFrame;
        var audio = new List<Cf>();
        audio.AddRange(new Cf[silence]);
        foreach (byte[] p in payloads)
        {
            audio.AddRange(tx.ModulateBurst([p]));
            audio.AddRange(new Cf[silence]);
        }

        short[] pcm = DatacTransmitter.ToPcm16(audio.ToArray());
        string dir = Directory.CreateTempSubdirectory("pdn-burst-interop").FullName;
        try
        {
            string wavIn = Path.Combine(dir, "ours.s16");
            string bytesOut = Path.Combine(dir, "out.bin");
            var raw = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, raw, 0, raw.Length);
            File.WriteAllBytes(wavIn, raw);

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(buildSrc!, "freedv_data_raw_rx"),
                ArgumentList = { modeName, wavIn, bytesOut },
                RedirectStandardError = true,
            };
            psi.Environment["LD_LIBRARY_PATH"] = buildSrc!;

            using Process proc = Process.Start(psi)!;
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000).Should().BeTrue("freedv_data_raw_rx must finish");
            output.WriteLine(stderr.Trim());

            byte[] got = File.ReadAllBytes(bytesOut);
            byte[] expected = payloads.SelectMany(p => p).ToArray();
            got.Should().Equal(expected, "codec2's RX must recover every payload from our bursts, in order");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
