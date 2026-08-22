using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The diversity banks' content dedupe, and the one thing it must not do: swallow a frame the host
/// was going to get.
/// </summary>
/// <remarks>
/// Every IL2P+CRC bank now reads its bits as plain IL2P as well, so a burst whose trailing CRC
/// will not verify produces a copy that is shown and withheld where it used to produce nothing.
/// That copy passes through the same dedupe window as a real delivery. A station whose far end
/// retransmits a damaged frame - the ordinary AX.25 answer, and often within a second or two -
/// would then have the good copy deduped against the bad one and lose it, which is a far worse
/// outcome than the duplicate row the window exists to prevent.
/// </remarks>
public class FrameDeduperTests
{
    private const int Window = 12000;

    private static readonly byte[] Frame = [1, 2, 3, 4, 5];

    [Fact]
    public void An_Identical_Frame_Inside_The_Window_Is_Dropped()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0).Should().BeTrue();
        deduper.ShouldEmit(Frame, Window / 2).Should().BeFalse();
    }

    [Fact]
    public void An_Identical_Frame_Past_The_Window_Is_A_New_Transmission()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0).Should().BeTrue();
        deduper.ShouldEmit(Frame, Window + 1).Should().BeTrue(
            "two identical frames seconds apart is ordinary AX.25 retry behaviour");
    }

    [Fact]
    public void A_Withheld_Copy_Does_Not_Swallow_The_Host_Copy_That_Follows_It()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0, delivered: false).Should().BeTrue();
        deduper.ShouldEmit(Frame, Window / 2, delivered: true).Should().BeTrue(
            "the first copy was only ever shown; this one is the host's and must get through");
        deduper.ShouldEmit(Frame, Window / 2 + 1, delivered: true).Should().BeFalse(
            "and having got through, it owns the window like any other delivery");
    }

    [Fact]
    public void A_Delivered_Copy_Still_Suppresses_A_Withheld_One()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0, delivered: true).Should().BeTrue();
        deduper.ShouldEmit(Frame, Window / 2, delivered: false).Should().BeFalse(
            "a second, weaker reading of a frame already handed on is exactly the duplicate the "
            + "window is here to drop");
    }

    [Fact]
    public void Two_Withheld_Copies_Are_Still_One_Row()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0, delivered: false).Should().BeTrue();
        deduper.ShouldEmit(Frame, Window / 2, delivered: false).Should().BeFalse(
            "several branches reading the same unverifiable burst is one burst, not several");
    }

    [Fact]
    public void A_Carrier_Acquisition_Makes_An_Identical_Frame_A_New_Transmission()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0).Should().BeTrue();
        deduper.CarrierAcquired();
        deduper.ShouldEmit(Frame, Window / 2).Should().BeTrue(
            "the carrier dropped and re-synced between the copies, which is positive evidence "
            + "of two transmissions however close together they land (issue #342)");
    }

    [Fact]
    public void A_Recorded_Delivery_Suppresses_The_Other_Routes_Copy()
    {
        var deduper = new FrameDeduper(Window);

        deduper.RecordDelivery(Frame, 0);
        deduper.ShouldEmit(Frame, Window / 2).Should().BeFalse(
            "the recorded copy went to the host, so a second reading of the same bytes inside "
            + "the window is the duplicate the window exists to drop");
    }

    [Fact]
    public void A_Recorded_Delivery_Reanchors_The_Window_At_Itself()
    {
        var deduper = new FrameDeduper(Window);

        deduper.ShouldEmit(Frame, 0).Should().BeTrue();
        deduper.RecordDelivery(Frame, 2 * Window);
        deduper.ShouldEmit(Frame, 2 * Window + Window / 2).Should().BeFalse(
            "the later recorded copy owns the entry, so its trailing other-route reading still "
            + "dedupes even though the first copy's window has long expired");
    }
}
