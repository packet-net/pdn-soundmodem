using Packet.SoundModem.Pocsag;
using M0LTE.Dsp;

namespace Packet.SoundModem.Tests.Pocsag;

/// <summary>
/// Occupied-bandwidth guard for the POCSAG transmitter. There is no reference recording
/// to be "never wider than" (no NinoTNC equivalent, and no installable known-good POCSAG
/// modulator to record), so the bounds are absolute and stated: the baseband must stay
/// inside the 0.55·baud pulse-shaping filter it is built with — the same discipline the
/// direct-FSK (fsk9600/4800) baseband is held to, and measured with the same meter. For
/// scale: at 1200 bd the shaped baseband occupies ≲700 Hz of 99 % OBW, which after FM
/// modulation at ±4.5 kHz deviation is a Carson bandwidth of ~10 kHz — comfortably
/// inside the 20/25 kHz channels pagers and DAPNET operate in.
/// </summary>
public class PocsagObwTests
{
    private const int SampleRate = 48000;

    [Theory]
    [InlineData(512)]
    [InlineData(1200)]
    [InlineData(2400)]
    public void The_Baseband_Stays_Inside_Its_Shaping_Filter(int baud)
    {
        var messages = new List<PocsagMessage>
        {
            // Random-ish printable content so the measurement sees representative
            // symbol statistics rather than one lucky pattern.
            PocsagMessage.Alphanumeric(133703, MakeText(200, seed: 7)),
            PocsagMessage.Alphanumeric(2007287, MakeText(200, seed: 11)),
        };
        float[] audio = new PocsagEncoder(SampleRate, baud).Modulate(messages);

        // Skip the preamble: the figure the channel cares about is what the payload's
        // modulation produces (the reversal preamble is a narrow tone at baud/2 anyway).
        int skip = (int)(PocsagEncoder.PreambleBits * (double)SampleRate / baud);
        var (_, high, width, _) = OccupiedBandwidth.Measure(audio.AsSpan(skip), SampleRate);

        high.Should().BeLessThan(baud * 0.75, "the 0.55·baud shaping filter bounds the spectrum");
        width.Should().BeLessThan(baud * 0.75, "baseband NRZ power reaches down to DC");
    }

    private static string MakeText(int length, int seed)
    {
        var random = new Random(seed);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)random.Next(0x20, 0x7F);
        }

        return new string(chars);
    }
}
