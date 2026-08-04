using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Guards afsk300-il2pc against a real off-air capture of the live 40 m slot
/// (<c>samples/offair/m9psy-40m-afsk300-il2pc-qrm.wav</c> - provenance in that folder's
/// README). The clip carries an M0LTE SABM through the slot's everyday interference: a
/// neighbouring QSO ~200 Hz below the packet centre, inside a ±400 Hz receive passband
/// but outside a ±300 one. The ±400 filters this mode shipped with for its first year
/// lose the frame to discriminator capture; the current defaults decode it. This is the
/// measured regression that motivated both the narrower single-modem filters and the
/// <see cref="Afsk300MultiModem"/> bank.
/// </summary>
public class OffAirAfsk300Tests
{
    private const string Fixture = "samples/offair/m9psy-40m-afsk300-il2pc-qrm.wav";

    // M0LTE>GB7IOW-1 SABM+P - one of the connect attempts the capture caught.
    private static readonly byte[] Expected =
        Convert.FromHexString("8E846E929EAEE29A6098A88A40613F");

    // The clip is cut from the ubersdr band plan: dial 7.049450 USB, slot 7.050300,
    // so the packet centre sits at 850 Hz audio.
    private const double Centre = 850;

    private static float[] Load()
    {
        var (samples, rate) = WavFile.ReadMono(Path.Combine(FindRepoRoot(), Fixture));
        rate.Should().Be(12000);
        // Flush tail: the deframer finishes the last frame inside the FIR pipeline.
        Array.Resize(ref samples, samples.Length + rate / 2);
        return samples;
    }

    [Fact]
    public void The_Historical_Wide_Filters_Lose_The_Frame_To_The_Neighbouring_Qso()
    {
        var frames = new List<byte[]>();
        new Afsk300Modem(12000, frames.Add, Afsk300Framing.Il2pCrc, Centre,
            bandPassHalfWidth: 400, lowPassCutoff: 400).Process(Load());

        frames.Should().BeEmpty("±400 Hz filters admit the QSO below the slot and the discriminator follows it");
    }

    [Fact]
    public void The_Current_Single_Modem_Defaults_Decode_The_Frame()
    {
        var frames = new List<byte[]>();
        new Afsk300Modem(12000, frames.Add, Afsk300Framing.Il2pCrc, Centre).Process(Load());

        frames.Should().ContainSingle().Which.Should().Equal(Expected);
    }

    [Fact]
    public void The_Bank_Decodes_The_Frame()
    {
        var frames = new List<byte[]>();
        new Afsk300MultiModem(12000, frames.Add, Afsk300Framing.Il2pCrc, Centre).Process(Load());

        frames.Should().ContainSingle().Which.Should().Equal(Expected);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pdn-soundmodem.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
