using Packet.SoundModem.Survey;

namespace Packet.SoundModem.Tests.Survey;

/// <summary>
/// The look-back that makes capturing a burst possible at all: the detector reports one once it
/// has ended, by which point the audio is already past.
/// </summary>
public class AudioRingBufferTests
{
    private static float[] Ramp(int from, int count)
    {
        var block = new float[count];
        for (int i = 0; i < count; i++)
        {
            block[i] = from + i;
        }

        return block;
    }

    [Fact]
    public void Audio_Written_Can_Be_Read_Back_By_Absolute_Position()
    {
        var ring = new AudioRingBuffer(1000);
        ring.Write(Ramp(0, 300));
        ring.Write(Ramp(300, 300));

        var read = new float[400];
        ring.TryCopy(100, read).Should().BeTrue();

        read.Should().Equal(Ramp(100, 400), "position is absolute, not relative to the ring");
        ring.Written.Should().Be(600);
    }

    [Fact]
    public void A_Read_That_Wraps_The_Ring_Is_Still_Contiguous()
    {
        var ring = new AudioRingBuffer(1000);
        ring.Write(Ramp(0, 700));
        ring.Write(Ramp(700, 700));   // this block straddles the seam

        var read = new float[600];
        ring.TryCopy(600, read).Should().BeTrue();

        read.Should().Equal(Ramp(600, 600));
    }

    [Fact]
    public void A_Block_That_Wrapped_Several_Times_Leaves_Its_Tail_At_The_Right_Position()
    {
        // The head of an over-long block is dropped, so the tail does not start where the block
        // does. Getting that offset wrong shifts every later read by the difference - real audio
        // from the wrong moment, which is worse than none, because nothing about it looks wrong.
        var ring = new AudioRingBuffer(1000);
        ring.Write(Ramp(0, 1500));

        var read = new float[400];
        ring.TryCopy(1000, read).Should().BeTrue();

        read.Should().Equal(Ramp(1000, 400));
    }

    [Fact]
    public void Audio_That_Has_Been_Overwritten_Is_Refused_Rather_Than_Guessed()
    {
        // A capture that arrives too late must be skipped. Returning stale or zeroed audio would
        // put a WAV of the wrong signal - or of silence, reading as a dead channel - beside a
        // sidecar describing a real burst.
        var ring = new AudioRingBuffer(1000);
        ring.Write(Ramp(0, 2000));

        ring.TryCopy(500, new float[100]).Should().BeFalse("that audio is long gone");
        ring.TryCopy(1900, new float[200]).Should().BeFalse("that audio has not arrived yet");
        ring.TryCopy(1500, new float[100]).Should().BeTrue();
    }

    [Fact]
    public void A_Block_Bigger_Than_The_Ring_Leaves_Its_Tail_Behind()
    {
        // Copying the part that would be immediately overwritten is work with no result, but the
        // sample clock still has to advance by the whole block or every later position is wrong.
        var ring = new AudioRingBuffer(100);
        ring.Write(Ramp(0, 250));

        ring.Written.Should().Be(250);
        var read = new float[100];
        ring.TryCopy(150, read).Should().BeTrue();
        read.Should().Equal(Ramp(150, 100));
    }
}
