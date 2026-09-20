using Packet.SoundModem.Modems.OfdmFm;
namespace Packet.SoundModem.Tests.Modems.OfdmFm;

using Packet.SoundModem.Modems;

/// <summary>
/// The rate controller wired into the streaming modem, which is where it stops being a component
/// and starts being a station.
/// </summary>
public class AdaptiveModemTests
{
    // A clock the test moves by hand. Small enough not to be worth a package dependency, and this
    // is a plugin loaded into a host, where fewer assemblies is its own reward.
    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * TimestampFrequency);
    }

    private static readonly OfdmFmParameters Adaptive = OfdmFmParameters.Synthetic with
    {
        Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 9, 2, 3, true),
        Constellation = OfdmFmConstellation.Qpsk,
        AdaptiveRate = true,
    };

    private static byte[] Frame(int length, int seed = 3)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static (OfdmFmModem Modem, List<byte[]> Delivered) Build(
        OfdmFmParameters profile, TimeProvider? clock = null)
    {
        var delivered = new List<byte[]>();
        return (new OfdmFmModem("ofdm-fm:test", profile, delivered.Add, clock), delivered);
    }

    [Fact]
    public void A_Modem_That_Does_Not_Adapt_Transmits_What_It_Was_Configured_For()
    {
        (OfdmFmModem modem, _) = Build(OfdmFmParameters.Synthetic);

        modem.TransmittingAt.Should().BeNull();
        modem.AskingFor.Should().BeNull();
    }

    [Fact]
    public void An_Adapting_Modem_Opens_At_The_Rate_It_Was_Configured_For()
    {
        // Not at the bottom of the ladder. The first burst of any link goes out before anything has
        // been heard back, so a station that crawled from the bottom every time would open every
        // contact slower than the operator asked for.
        (OfdmFmModem modem, _) = Build(Adaptive);

        modem.TransmittingAt!.Constellation.Should().Be(OfdmFmConstellation.Qpsk);
        modem.TransmittingAt.Coding.Should().Be(Adaptive.Codes);
        modem.AskingFor!.Constellation.Should().Be(OfdmFmConstellation.Qpsk);
    }

    [Fact]
    public void A_Profile_Whose_Rate_Is_Not_On_The_Ladder_Opens_At_The_Most_Robust_One()
    {
        // K=7 is perfectly transmittable and the ladder is measured at K=9, so this is a real
        // configuration with no rung to start from. The most robust rate is the only other
        // defensible answer, and it must not throw or pick something arbitrary.
        var offLadder = Adaptive with
        {
            Coding = new OfdmFmCoding(OfdmFmFec.Convolutional, 7, 1, 2, true),
        };

        (OfdmFmModem modem, _) = Build(offLadder);

        modem.TransmittingAt.Should().Be(OfdmFmRateLadder.Rungs[OfdmFmRateLadder.Slowest]);
    }

    [Fact]
    public void A_Station_Hears_What_Its_Correspondent_Is_Asking_For_And_Transmits_At_It()
    {
        // The loop, over one hop of a clean path. B asks for something; A hears the ask on B's
        // burst and moves its own transmitter to it, without A having measured anything itself.
        (OfdmFmModem a, _) = Build(Adaptive);
        (OfdmFmModem b, _) = Build(Adaptive);

        OfdmFmRate opening = a.TransmittingAt!;
        for (int i = 0; i < 12; i++)
        {
            b.Process(b.Modulate(Frame(32, i), txDelayMilliseconds: 0));
            a.Process(a.Modulate(Frame(32, i), txDelayMilliseconds: 0));
            a.Process(b.Modulate(Frame(32, 100 + i), txDelayMilliseconds: 0));
        }

        b.AskingFor!.GoodputBitsPerSecond.Should().BeGreaterThan(
            opening.GoodputBitsPerSecond, "a clean path should have B asking for something faster");
        a.TransmittingAt.Should().Be(
            b.AskingFor, "and A transmits at what it was asked for, having measured nothing itself");
    }

    [Fact]
    public void A_Recommendation_Is_Forgotten_When_Its_Correspondent_Has_Not_Been_Heard_For_A_While()
    {
        // A recommendation describes a path, and a path nobody has been heard on for two minutes
        // may not be that path any more. Going back costs one slow burst; carrying on at a rate the
        // far end asked for before it drove into a valley costs the frame, and then costs it again.
        var clock = new Clock();
        (OfdmFmModem a, _) = Build(Adaptive, clock);
        (OfdmFmModem b, _) = Build(Adaptive);

        for (int i = 0; i < 12; i++)
        {
            b.Process(b.Modulate(Frame(32, i), txDelayMilliseconds: 0));
            a.Process(b.Modulate(Frame(32, 100 + i), txDelayMilliseconds: 0));
        }

        OfdmFmRate learned = a.TransmittingAt!;
        learned.Should().NotBe(
            OfdmFmRateLadder.Rungs[OfdmFmRateLadder.Slowest], "it should have learnt something");

        clock.Advance(TimeSpan.FromSeconds(Adaptive.RecommendationLifeSeconds + 1));
        _ = a.Modulate(Frame(32), txDelayMilliseconds: 0);

        a.TransmittingAt!.Constellation.Should().Be(
            OfdmFmConstellation.Qpsk, "and go back to where it started, not to what it had learnt");
        a.TransmittingAt.Should().NotBe(learned);
    }

    [Fact]
    public void A_Burst_Retried_At_Several_Offsets_Is_One_Report_And_Not_Several()
    {
        // The debounce, which is not tidiness. A payload that fails is retried a sample on, several
        // times, because the commonest cause is a sync committed a few samples off a peak that
        // noise moved. Reporting each attempt would read one wrecked burst as a run of failures and
        // walk the rate down several rungs for it.
        (OfdmFmModem modem, List<byte[]> delivered) = Build(Adaptive);
        for (int i = 0; i < 12; i++)
        {
            modem.Process(modem.Modulate(Frame(32, i), txDelayMilliseconds: 0));
        }

        OfdmFmRate climbed = modem.AskingFor!;
        int before = OfdmFmRateLadder.IndexOf(climbed.Constellation, climbed.Coding);
        before.Should().BeGreaterThan(0, "a clean path should have taken it up the ladder");

        // The whole payload, not one symbol of it. How many payload symbols there are depends on
        // the rate the modem has climbed to, so a fixed-size hole no longer means a fixed amount of
        // damage - and at a fast rate the code absorbs one symbol without noticing.
        float[] audio = modem.Modulate(Frame(32), txDelayMilliseconds: 0);
        int from = Adaptive.SymbolSamples + new OfdmFmBurstCodec(Adaptive).HeaderEndOffset;
        for (int n = from; n < audio.Length; n++)
        {
            audio[n] = 0f;
        }

        delivered.Clear();
        modem.Process(audio);

        delivered.Should().BeEmpty("the payload was wrecked");
        OfdmFmRateLadder.IndexOf(modem.AskingFor!.Constellation, modem.AskingFor.Coding)
            .Should().BeGreaterThanOrEqualTo(
                before - 1, "one wrecked burst is one step down, however many times it was retried");
    }
}
