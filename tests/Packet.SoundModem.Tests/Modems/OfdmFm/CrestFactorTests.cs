using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

/// <summary>
/// No symbol in a burst may tower over the others.
/// </summary>
/// <remarks>
/// <para>An FM transmitter is peak deviation limited, so the loudest instant in a burst sets the
/// drive for every other instant in it. One symbol with an unusually high peak therefore does not
/// cost only itself - it holds the whole burst below the deviation the radio is set up for, and
/// every symbol pays.</para>
/// <para>This is not a theoretical worry. The last payload symbol used to be padded with zeros,
/// which put a run of carriers all on the same constellation point, which in the time domain is
/// close to an impulse: it peaked at 2.238 while every other symbol in the burst peaked between
/// 0.70 and 0.89, and it set the burst peak on every burst. Padding with pseudo-random bits
/// instead - the receiver never reads them, so their value was always free - took the burst crest
/// factor from 18.5 dB to 11.9 dB and bought about 3 dB of sensitivity, which is more than any
/// coding change measured in this repository has been worth.</para>
/// <para>The ratio pinned here is deliberately loose. OFDM symbol peaks vary with the data and a
/// tight bound would flap; what this is for is catching a symbol that is structurally peaky, which
/// shows up as a factor of two or more, not a few per cent.</para>
/// </remarks>
public class CrestFactorTests
{
    [Theory]
    [InlineData(OfdmFmConstellation.Bpsk)]
    [InlineData(OfdmFmConstellation.Qpsk)]
    [InlineData(OfdmFmConstellation.Qam16)]
    [InlineData(OfdmFmConstellation.Qam64)]
    public void No_Symbol_Towers_Over_The_Rest_Of_Its_Burst(OfdmFmConstellation constellation)
    {
        // On the narrow preset's layout, which is the span the ratio below was measured on. It is
        // also the one that puts the most payload symbols in a burst of a given size, so a
        // structurally peaky symbol has the most company to tower over.
        OfdmFmParameters profile = OfdmFmTestProfiles.Narrow;

        var codec = new OfdmFmBurstCodec(profile);

        foreach (int length in (ReadOnlySpan<int>)[16, 64, 160])
        {
            for (int seed = 0; seed < 4; seed++)
            {
                var payload = new byte[length];
                new Random(seed).NextBytes(payload);
                float[] audio = codec.Modulate(payload, constellation, 1);

                var peaks = new List<double>();
                for (int s = 1; s * profile.SymbolSamples < audio.Length; s++)
                {
                    double peak = 0;
                    int end = Math.Min((s + 1) * profile.SymbolSamples, audio.Length);
                    for (int i = s * profile.SymbolSamples; i < end; i++)
                    {
                        peak = Math.Max(peak, Math.Abs(audio[i]));
                    }

                    peaks.Add(peak);
                }

                double loudest = peaks.Max();
                double typical = peaks.Order().ElementAt(peaks.Count / 2);

                (loudest / typical).Should().BeLessThan(
                    2.0,
                    "no symbol may set the burst's drive on its own ({0}, {1} bytes, seed {2}): "
                    + "loudest symbol {3:F3}, median symbol {4:F3}",
                    constellation,
                    length,
                    seed,
                    loudest,
                    typical);
            }
        }
    }
}
