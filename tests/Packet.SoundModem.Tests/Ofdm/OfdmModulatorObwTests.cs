using Packet.SoundModem.Dsp;
using Packet.SoundModem.Ofdm;
using Packet.SoundModem.Tests.Dsp;
using Xunit.Abstractions;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>
/// Occupied-bandwidth guard for the datac OFDM transmitter, mirroring
/// <see cref="OccupiedBandwidthTests"/>. Each datac mode occupies roughly <c>(Nc−1)·Rs</c> of
/// carrier spread plus the pilot/clipper skirt, band-limited by the mode's TX BPF. datac0/3 ride
/// ~500 Hz, datac1 ~1.7 kHz, and the narrow datac4/13/14 a few hundred Hz — matching FreeDV's
/// published channel footprints. Measured on the real part at Fs=8000 (what
/// <c>freedv_rawdatatx</c> emits), payload symbols only.
/// </summary>
public class OfdmModulatorObwTests(ITestOutputHelper output)
{
    private const int Fs = 8000;

    // nominalHz is the carrier spread (Nc−1)·Rs; the measured 99 % OBW is that plus the pilot
    // and clipper skirt left after the mode's TX BPF, so the pinned [low,high] window sits above
    // the nominal. Values are deterministic (fixed payload, fixed meter) — this is a regression
    // guard against the modulator going wide (splatter) or collapsing (broken/DC).
    [Theory]
    [InlineData("datac0", 500, 620, 820)]
    [InlineData("datac1", 1625, 1500, 1850)]
    [InlineData("datac3", 500, 620, 820)]
    [InlineData("datac4", 188, 230, 370)]
    [InlineData("datac13", 125, 200, 330)]
    [InlineData("datac14", 167, 270, 430)]
    public void Occupied_Bandwidth_Matches_The_Mode_Footprint(
        string name, double nominalHz, double lowHz, double highHz)
    {
        var mode = OfdmMode.ForName(name);
        var mod = new OfdmModulator(mode);

        // A full packet of pseudo-random payload bits, real part only.
        byte[] bits = new byte[mode.BitsPerPacket];
        var rng = new Random(101);
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)(rng.Next() & 1);
        }

        Cf[] tx = mod.ModulatePacketBits(bits);
        float[] real = Array.ConvertAll(tx, s => s.Re);

        int fftSize = 2048;
        var (loEdge, hiEdge, width, peak) =
            OccupiedBandwidth.Measure(real, Fs, fftSize: fftSize);

        output.WriteLine(
            $"{name}: OBW={width:F0} Hz  [{loEdge:F0}..{hiEdge:F0}]  peak={peak:F0} Hz  (nominal ~{nominalHz})");

        width.Should().BeInRange(lowHz, highHz,
            "mode {0} occupies ~{1} Hz ((Nc-1)*Rs + skirt)", name, nominalHz);

        // The lobe sits around the carrier centre (BPF centre), not aliased.
        peak.Should().BeInRange(mod.BpfCentreHz - 400, mod.BpfCentreHz + 400);
    }
}
