using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The QPSK twin of <see cref="BpskTxShapingParityTests"/>: the single-modem factory, the
/// frequency-diversity bank the catalogue deploys, and the catalogue itself must produce
/// sample-identical audio for every QPSK mode. The QPSK bank routes its branches through the
/// per-mode factories precisely so per-mode configuration travels (which is why qpsk600
/// never suffered issue #340's split), but nothing pinned that structure until issue #344
/// moved qpsk600's roll-off and wanted the guarantee held as a test rather than an
/// architectural accident.
/// </summary>
public class QpskTxShapingParityTests
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
    [InlineData("qpsk600", 300)]
    [InlineData("qpsk2400", 1200)]
    [InlineData("qpsk3600", 1800)]
    public void The_Bank_And_The_Catalogue_Transmit_Exactly_What_The_Factory_Transmits(string mode, int baud)
    {
        byte[] frame = Frame();
        float[] factory = (baud switch
            {
                300 => QpskModem.Qpsk600(SampleRate, static _ => { }),
                1200 => QpskModem.Qpsk2400(SampleRate, static _ => { }),
                _ => QpskModem.Qpsk3600(SampleRate, static _ => { }),
            })
            .Modulate(frame, txDelayMilliseconds: 300);
        float[] bank = (baud switch
            {
                300 => QpskMultiModem.Qpsk600(SampleRate, static _ => { }),
                1200 => QpskMultiModem.Qpsk2400(SampleRate, static _ => { }),
                _ => QpskMultiModem.Qpsk3600(SampleRate, static _ => { }),
            })
            .Modulate(frame, txDelayMilliseconds: 300);
        float[] catalogue = ModemCatalog.Create(mode, SampleRate, static _ => { })
            .Modulate(frame, txDelayMilliseconds: 300);

        bank.Should().Equal(factory,
            "the bank's centre branch and the '{0}' factory must be the same transmitter", mode);
        catalogue.Should().Equal(factory,
            "the catalogue's '{0}' arm and the factory must be the same transmitter", mode);
    }
}
