using Packet.SoundModem.Ardop;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Tests.Dsp;

namespace Packet.SoundModem.Tests.Ardop;

/// <summary>
/// Occupied-bandwidth guard for the ARDOP transmitter, per bandwidth class
/// (docs/ardop-design.md §6.3): the reference is not the spec's nominal figure but
/// ardopcf's own transmit audio — the checked-in oracle fixture for the same frame
/// type and payload, measured with the same meter. Ours must never be wider than the
/// reference's own skirts (the honest bar; same discipline as the NinoTNC OBW tests).
/// </summary>
public class ArdopObwTests
{
    [Theory]
    [InlineData("txframe_4FSK.200.50S.E.wav", 0x48)]   // 200 Hz class
    [InlineData("txframe_4FSK.500.100.E.wav", 0x4A)]   // 500 Hz class
    [InlineData("txframe_4FSK.500.100S.E.wav", 0x4C)]  // 500 Hz class, short
    [InlineData("txframe_4FSK.2000.600.E.wav", 0x7A)]  // 2000 Hz class (600 Bd FM)
    [InlineData("txframe_4FSK.2000.600S.E.wav", 0x7C)] // 2000 Hz class, short
    public void Our_Transmit_Is_Never_Wider_Than_Ardopcf(string referenceWav, byte type)
    {
        // The reference: ardopcf's transmission of this exact frame (payload from the
        // fixture manifest), so both signals carry identical symbol statistics.
        (float[] reference, int rate) = WavFile.ReadMono(
            Path.Combine(ArdopReferenceVectorTests.SamplesDir(), referenceWav));
        rate.Should().Be(ArdopModulator.SampleRate);

        string manifestLine = File.ReadAllLines(
                Path.Combine(ArdopReferenceVectorTests.SamplesDir(), "txframe-manifest.txt"))
            .Single(line => line.StartsWith(referenceWav + " ", StringComparison.Ordinal));
        byte[] payload = Convert.FromHexString(manifestLine.Split(' ')[3]);

        short[] oursShort = new ArdopModulator().Modulate(
            ArdopFrameCodec.EncodeDataFrame(type, payload, 0xFF));
        var ours = new float[oursShort.Length];
        for (int i = 0; i < oursShort.Length; i++)
        {
            ours[i] = oursShort[i] / 32768f;
        }

        var referenceObw = OccupiedBandwidth.Measure(reference, rate);
        var oursObw = OccupiedBandwidth.Measure(ours, rate);

        // Identical modulator lineage (same templates, same TX filter) should land
        // within one FFT bin (12000/4096 ≈ 2.9 Hz) of the reference.
        double binHz = 12000.0 / 4096;
        oursObw.WidthHz.Should().BeLessThanOrEqualTo(referenceObw.WidthHz + binHz,
            "our TX must never occupy more bandwidth than ardopcf's for the same frame");
    }
}
