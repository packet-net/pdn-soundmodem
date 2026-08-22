using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The three ways of building a BPSK transmitter for the same catalogue mode - the
/// single-modem factory, the frequency-diversity bank the catalogue deploys, and the
/// catalogue itself - must produce sample-identical audio. Issue #340: the bank builds its
/// branches through <see cref="BpskModem"/>'s constructor rather than the per-mode factory,
/// so a per-mode default set in the factory alone silently diverges from what the daemon
/// (and pdn-qso, which transmits through the bank's centre branch) actually puts on air.
/// That happened once: the factory carried roll-off 0.20 for three days before the bank
/// existed, the bank shipped at the 0.35 constructor default, and the discrepancy went
/// unnoticed for a month because the occupied-bandwidth test certified the factory. These
/// tests make the divergence a failure rather than a finding.
/// </summary>
public class BpskTxShapingParityTests
{
    private const int SampleRate = 12000;

    private static byte[] Frame()
    {
        var frame = new byte[16 + 60];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(11).NextBytes(frame.AsSpan(16));
        return frame;
    }

    [Theory]
    [InlineData("bpsk300", 300)]
    [InlineData("bpsk1200", 1200)]
    public void The_Bank_And_The_Catalogue_Transmit_Exactly_What_The_Factory_Transmits(string mode, int baud)
    {
        byte[] frame = Frame();
        float[] factory = (baud == 300
                ? BpskModem.Bpsk300(SampleRate, static _ => { })
                : BpskModem.Bpsk1200(SampleRate, static _ => { }))
            .Modulate(frame, txDelayMilliseconds: 300);
        float[] bank = (baud == 300
                ? BpskMultiModem.Bpsk300(SampleRate, static _ => { })
                : BpskMultiModem.Bpsk1200(SampleRate, static _ => { }))
            .Modulate(frame, txDelayMilliseconds: 300);
        float[] catalogue = ModemCatalog.Create(mode, SampleRate, static _ => { })
            .Modulate(frame, txDelayMilliseconds: 300);

        bank.Should().Equal(factory,
            "the bank's centre branch and the '{0}' factory must be the same transmitter", mode);
        catalogue.Should().Equal(factory,
            "the catalogue's '{0}' arm and the factory must be the same transmitter", mode);
    }
}
