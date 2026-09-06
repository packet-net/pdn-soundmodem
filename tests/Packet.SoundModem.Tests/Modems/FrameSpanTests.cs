using AwesomeAssertions;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// The marks a demodulator sets to say where a frame was, and the two rules that stop one
/// frame's level being measured over another's audio.
/// </summary>
/// <remarks>
/// Both rules are here because the first cut of the span mechanism broke both, and the failure
/// was invisible from outside: a frame with no mark of its own was handed the previous frame's,
/// and a diversity bank that asked for a span it did not get stored the leftovers anyway. The
/// symptom in each case was a correct frame reading the level of whatever was loudest between
/// it and the frame before it, and being badged TOO LOUD for it.
/// </remarks>
public class FrameSpanTests
{
    [Fact]
    public void A_Frame_Is_The_Span_Between_Its_Own_Sync_And_Its_Own_Last_Bit()
    {
        var span = new FrameSpan();
        span.Sync(0, 1000);
        span.Complete(0, 13000);

        span.TryTakeFrameSpan(out long from, out long to).Should().BeTrue();
        from.Should().Be(1000);
        to.Should().Be(13000);
    }

    /// <summary>
    /// A mark belongs to one frame. A second frame that never marked a sync of its own gets no
    /// span, rather than one running back to the first frame's sync.
    /// </summary>
    /// <remarks>
    /// Measured before this rule existed: a frame that decoded correctly reported -1.4 dBFS,
    /// which was a tone a second earlier and between the two frames, and the row was badged
    /// TOO LOUD. The span handed out was 41 seconds long.
    /// </remarks>
    [Fact]
    public void A_Frame_With_No_Sync_Of_Its_Own_Gets_No_Span()
    {
        var span = new FrameSpan();
        span.Sync(0, 1000);
        span.Complete(0, 13000);
        span.TryTakeFrameSpan(out _, out _).Should().BeTrue();

        span.Complete(0, 500000);

        span.TryTakeFrameSpan(out _, out _).Should().BeFalse(
            "the mark was spent by the frame that used it");
    }

    [Fact]
    public void A_Span_Is_Taken_Once()
    {
        var span = new FrameSpan();
        span.Sync(0, 1000);
        span.Complete(0, 13000);

        span.TryTakeFrameSpan(out _, out _).Should().BeTrue();
        span.TryTakeFrameSpan(out _, out _).Should().BeFalse(
            "a span that can be read twice can be read stale");
    }

    /// <summary>
    /// Each deframer marks its own. A timing phase that decoded nothing must not be able to move
    /// the mark of the phase that did.
    /// </summary>
    /// <remarks>
    /// The HDLC framings raise their sync hook on every flag, and a frame's closing flag is the
    /// next frame's opening one - so a phase a few samples ahead of the delivering one bumps the
    /// mark just before it is read. Measured with one shared mark: an fsk9600 frame that had
    /// lasted 30 ms reported a span of 1.9 ms, and so got no level at all.
    /// </remarks>
    [Fact]
    public void One_Phase_Cannot_Move_Anothers_Mark()
    {
        var span = new FrameSpan(3);
        span.Sync(0, 1000);
        span.Sync(1, 1002);

        // Phase 2 opens a frame of its own on the closing flag, just before phase 0 delivers.
        span.Sync(2, 12998);
        span.Complete(0, 13000);

        span.TryTakeFrameSpan(out long from, out long to).Should().BeTrue();
        from.Should().Be(1000, "phase 0 delivered, so phase 0's mark is the frame's");
        to.Should().Be(13000);
    }

    /// <summary>
    /// A bank that is given an empty range has nothing to report, which is what it must say when
    /// the branch that decoded the frame had no span for it.
    /// </summary>
    [Fact]
    public void An_Empty_Range_Is_A_Bank_Saying_It_Has_Nothing()
    {
        var span = new FrameSpan();
        span.Set(0, 0);

        span.TryTakeFrameSpan(out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// The out values of a failed take are not a span, which is the contract every diversity
    /// bank now depends on.
    /// </summary>
    /// <remarks>
    /// They are whatever the last successful take left, because the parameters are assigned
    /// before the answer is known. Three of the four banks stored them regardless, and the frame
    /// they were stored against was measured over the previous frame's audio.
    /// </remarks>
    [Fact]
    public void A_Failed_Take_Leaves_Stale_Out_Values_And_Says_So_In_Its_Return()
    {
        var span = new FrameSpan();
        span.Sync(0, 1000);
        span.Complete(0, 13000);
        span.TryTakeFrameSpan(out _, out _).Should().BeTrue();

        span.Complete(0, 500000);   // no sync of its own

        bool had = span.TryTakeFrameSpan(out long from, out long to);
        had.Should().BeFalse();
        (from, to).Should().Be((1000L, 13000L),
            "which is exactly why a caller must read the return and not the out values");
    }
}
