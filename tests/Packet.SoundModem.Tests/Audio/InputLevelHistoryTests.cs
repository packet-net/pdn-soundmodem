using AwesomeAssertions;
using Packet.SoundModem.Audio;

namespace Packet.SoundModem.Tests.Audio;

/// <summary>
/// The fine-grained level history a per-frame level is read out of: 10 ms cells, each holding the
/// loudest magnitude in it and whether the card railed during it.
/// </summary>
/// <remarks>
/// The cells are the whole point. A meter reading of the last 200 ms cannot say anything about a
/// frame that lasted 90 of them, and on an FM receiver with the squelch open it says a great deal
/// about the hiss either side (issue #426). What matters here is that a cell's peak is its own
/// audio and nobody else's, and that a clip lands where it happened rather than over the whole
/// block it arrived in.
/// </remarks>
public class InputLevelHistoryTests
{
    private const int SampleRate = 12000;
    private const int CellSamples = 120;

    /// <summary>A block of a constant magnitude, alternating sign so it is not a DC level.</summary>
    private static float[] Block(int samples, float magnitude)
    {
        var block = new float[samples];
        for (int n = 0; n < samples; n++)
        {
            block[n] = n % 2 == 0 ? magnitude : -magnitude;
        }

        return block;
    }

    [Fact]
    public void A_Cell_Reports_The_Loudest_Sample_In_Its_Own_Ten_Milliseconds()
    {
        var history = new InputLevelHistory(SampleRate);
        history.CellSamples.Should().Be(CellSamples, "10 ms at 12 kHz");

        // Three cells: quiet, loud, quiet. The loud one is a tenth of full scale, the others a
        // hundredth, so the two are 20 dB apart and no rounding can confuse them.
        float[] block = Block(3 * CellSamples, 0.01f);
        for (int n = CellSamples; n < 2 * CellSamples; n++)
        {
            block[n] = n % 2 == 0 ? 0.1f : -0.1f;
        }

        history.Add(block);

        history.TryMeasure(CellSamples, 2 * CellSamples, out double loud, out _).Should().BeTrue();
        loud.Should().BeApproximately(-20, 0.1, "0.1 full scale is -20 dBFS");

        history.TryMeasure(0, CellSamples, out double quiet, out _).Should().BeTrue();
        quiet.Should().BeApproximately(-40, 0.1, "and the cell before it heard only the quiet");

        history.TryMeasure(0, 3 * CellSamples, out double all, out _).Should().BeTrue();
        all.Should().BeApproximately(-20, 0.1, "a window over all three takes the loudest of them");
    }

    /// <summary>
    /// A clip is placed into the cells it happened in, not smeared over the block it arrived in.
    /// </summary>
    /// <remarks>
    /// This is what makes the flag worth carrying on a frame at all. A 100 ms block is longer
    /// than a whole qpsk3600 frame, so a clip flag that covered its block would mark every frame
    /// in earshot of one - which is the same false alarm <see cref="InputLevelMeter.IsClipped"/>
    /// refuses to make by testing the end codes exactly rather than "near the rail".
    /// </remarks>
    [Fact]
    public void A_Card_Clip_Lands_In_The_Cells_It_Happened_In()
    {
        var history = new InputLevelHistory(SampleRate);

        // A card block of the same stretch at four times the rate, as a 48 kHz card delivers it,
        // railed for its first tenth and clean after that.
        float[] card = Block(40 * CellSamples, 0.5f);
        for (int n = 0; n < 4 * CellSamples; n++)
        {
            card[n] = n % 2 == 0 ? InputLevelMeter.TopCode : InputLevelMeter.BottomCode;
        }

        history.NoteCardClipping(card);
        history.Add(Block(10 * CellSamples, 0.5f));

        history.TryMeasure(0, CellSamples, out _, out bool? early).Should().BeTrue();
        early.Should().BeTrue("the card railed in the first tenth of the block");

        history.TryMeasure(5 * CellSamples, 10 * CellSamples, out _, out bool? late)
            .Should().BeTrue();
        late.Should().BeFalse("and had headroom for the rest of it");
    }

    /// <summary>
    /// A station whose card samples nobody hands over says it does not know, rather than saying
    /// no: an ubersdr receiver and a monitor relaying somebody else's frames have no converter of
    /// their own to have run out of codes.
    /// </summary>
    [Fact]
    public void A_History_With_No_Card_Samples_Does_Not_Claim_The_Card_Was_Clean()
    {
        var history = new InputLevelHistory(SampleRate);
        history.Add(Block(CellSamples, 0.5f));

        history.TryMeasure(0, CellSamples, out _, out bool? clipped).Should().BeTrue();
        clipped.Should().BeNull("nothing was in a position to judge the converter's range");
    }

    [Fact]
    public void A_Window_The_Ring_No_Longer_Holds_Is_Refused()
    {
        var history = new InputLevelHistory(SampleRate);
        int wholeRing = (int)(InputLevelHistory.MemorySeconds * SampleRate);
        history.Add(Block(wholeRing + (10 * CellSamples), 0.5f));

        history.TryMeasure(0, CellSamples, out _, out _).Should().BeFalse(
            "the first cell was overwritten a lap ago, and a stale reading is worse than none");
        history.TryMeasure(history.Position - CellSamples, history.Position, out _, out _)
            .Should().BeTrue("while the newest cell is still there");
        history.TryMeasure(history.Position, history.Position + CellSamples, out _, out _)
            .Should().BeFalse("and audio that has not arrived cannot be measured");
    }

    /// <summary>
    /// Nothing reads above 0 dBFS, whatever the decimator's ripple did on the way here - the
    /// same clamp <see cref="InputLevelMeter.DbFs"/> makes, and for the same reason: a number
    /// above full scale is not a measurement of anything a converter can deliver.
    /// </summary>
    [Fact]
    public void Nothing_Reads_Above_Full_Scale()
    {
        var history = new InputLevelHistory(SampleRate);
        history.Add(Block(CellSamples, 1.4f));

        history.TryMeasure(0, CellSamples, out double peak, out _).Should().BeTrue();
        peak.Should().Be(0);
    }
}
