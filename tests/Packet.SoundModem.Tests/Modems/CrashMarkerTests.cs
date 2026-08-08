using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The crash marker (workstream 6 leg 2): an in-band envelope-ratio detector whose marks
/// crush the confidence of crash-hit symbols so the erasure ladder treats them as located
/// damage. Deterministic fixtures - a steady tone as the reference-setting signal, a
/// burst of amplified noise as the crash.
/// </summary>
public class CrashMarkerTests
{
    private const int Rate = 12000;
    private const int SamplesPerSymbol = 40;

    private static void FeedTone(CrashMarker marker, double amplitude, int samples)
    {
        for (int i = 0; i < samples; i++)
        {
            marker.Update((float)Math.Abs(amplitude * Math.Sin(2 * Math.PI * 1500 * i / Rate)));
        }
    }

    [Fact]
    public void A_Steady_Signal_Is_Never_Marked()
    {
        var marker = new CrashMarker(Rate, SamplesPerSymbol);
        FeedTone(marker, 0.1, Rate);

        bool marked = false;
        for (int i = 0; i < Rate; i++)
        {
            marked |= marker.Update((float)Math.Abs(0.1 * Math.Sin(2 * Math.PI * 1500 * i / Rate)));
        }

        marked.Should().BeFalse("a steady signal's own envelope peaks must stay under the enter ratio");
    }

    [Fact]
    public void A_Crash_Well_Above_The_Signal_Is_Marked_And_Released()
    {
        var marker = new CrashMarker(Rate, SamplesPerSymbol);
        FeedTone(marker, 0.1, Rate);

        // A 30 ms crash at 20 dB over the signal envelope.
        bool markedDuring = false;
        for (int i = 0; i < Rate * 3 / 100; i++)
        {
            markedDuring |= marker.Update(1.0f);
        }

        markedDuring.Should().BeTrue("a 20 dB excursion is a crash by any measure");

        // After the crash plus the one-symbol hold, the signal must unmark.
        int stillMarked = 0;
        for (int i = 0; i < Rate / 2; i++)
        {
            if (marker.Update((float)Math.Abs(0.1 * Math.Sin(2 * Math.PI * 1500 * i / Rate))))
            {
                stillMarked = i + 1;
            }
        }

        stillMarked.Should().BeLessThan(Rate / 10,
            "the mark must release within ~100 ms of the crash ending, not latch");
    }

    [Fact]
    public void The_Hold_Extends_The_Mark_By_About_One_Symbol()
    {
        var marker = new CrashMarker(Rate, SamplesPerSymbol);
        FeedTone(marker, 0.1, Rate);

        for (int i = 0; i < Rate / 100; i++)
        {
            marker.Update(1.0f);
        }

        // Immediately post-crash the hold keeps the mark up for the matched filter's smear.
        int held = 0;
        while (marker.Update(0.001f) && held < Rate)
        {
            held++;
        }

        held.Should().BeInRange(1, 4 * SamplesPerSymbol,
            "the hold covers filter smear without marking whole swaths of clean symbols");
    }

    [Fact]
    public void A_Sustained_Level_Change_Self_Heals_Rather_Than_Latching()
    {
        var marker = new CrashMarker(Rate, SamplesPerSymbol);
        FeedTone(marker, 0.02, Rate);

        // The level jumps 14 dB and STAYS there (a stronger station taking the channel) -
        // the reference must catch up and the mark must clear without the level dropping.
        bool markedAtEnd = false;
        for (int i = 0; i < Rate * 40; i++)
        {
            markedAtEnd = marker.Update((float)Math.Abs(0.1 * Math.Sin(2 * Math.PI * 1500 * i / Rate)));
        }

        markedAtEnd.Should().BeFalse("the slow reference keeps adapting during a mark, so it heals");
    }

    [Fact]
    public void Reset_Clears_All_State()
    {
        var marker = new CrashMarker(Rate, SamplesPerSymbol);
        FeedTone(marker, 0.1, Rate);
        for (int i = 0; i < Rate / 100; i++)
        {
            marker.Update(1.0f);
        }

        marker.Reset();

        marker.Active.Should().BeFalse();
        marker.Update(0.001f).Should().BeFalse("post-reset state is cold, not mid-crash");
    }
}
