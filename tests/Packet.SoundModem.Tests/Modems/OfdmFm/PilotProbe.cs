using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

public class PilotProbe
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void A_Profile_Reads_Its_Header_However_Many_Pilots_It_Has(int pilots)
    {
        OfdmFmParameters p = OfdmFmParameters.Synthetic;
        p = p with { PilotCarriers = pilots, DataCarriers = p.DataCarriers + p.PilotCarriers - pilots };
        var codec = new OfdmFmBurstCodec(p);
        var payload = new byte[24];
        new Random(3).NextBytes(payload);
        float[] audio = codec.Modulate(payload, OfdmFmConstellation.Qpsk, 2);

        OfdmFmBurst? burst = codec.Demodulate(audio);
        burst.Should().NotBeNull($"a profile with {pilots} pilot carriers must still decode");
        burst!.Payload.Should().Equal(payload);
    }
}
