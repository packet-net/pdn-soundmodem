namespace Packet.SoundModem.Windows.Tests;

public class SampleRingTests
{
    [Fact]
    public void Reads_come_back_in_the_order_they_were_written_across_the_wrap()
    {
        var ring = new SampleRing(4);
        ring.Write([1, 2, 3]).Should().Be(3);
        var two = new float[2];
        ring.Read(two).Should().Be(2);
        ring.Write([4, 5, 6]).Should().Be(3);

        var all = new float[4];
        ring.Read(all).Should().Be(4);
        all.Should().Equal(3, 4, 5, 6);
        ring.Count.Should().Be(0);
    }

    [Fact]
    public void A_write_into_a_full_ring_takes_only_what_fits()
    {
        var ring = new SampleRing(3);
        ring.Write([1, 2, 3, 4, 5]).Should().Be(3);
        ring.Free.Should().Be(0);
    }

    [Fact]
    public void Discard_drops_the_oldest_samples()
    {
        var ring = new SampleRing(4);
        ring.Write([1, 2, 3, 4]);
        ring.Discard(2).Should().Be(2);
        ring.Write([5, 6]);

        var all = new float[4];
        ring.Read(all);
        all.Should().Equal(3, 4, 5, 6);
    }

    [Fact]
    public void Discard_never_drops_more_than_is_queued()
    {
        var ring = new SampleRing(4);
        ring.Write([1]);
        ring.Discard(10).Should().Be(1);
        ring.Count.Should().Be(0);
    }
}
