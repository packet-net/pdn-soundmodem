using AwesomeAssertions;
using Packet.SoundModem.Modems.OfdmFmStation;

namespace Packet.SoundModem.Tests.Modems.OfdmFmStation;

/// <summary>The bookkeeping alone: no channel, no station, no threads.</summary>
public class TransmitBookTests
{
    [Theory]
    [InlineData("u960", "P960")]
    [InlineData("k960", "S960")]
    [InlineData("u960,u960,u480", "P960,P960,P480")]
    [InlineData("k960,k960", "S960,S960")]
    [InlineData("u960,k960,u960", "P960,S960,P960")]
    [InlineData("k240,u240,k240,u240,k1,u1", "S240,P240,S240,P240,S1,P1")]
    [InlineData("u1,k48000,u1,k1,k1,u7", "P1,S48000,P1,S1,S1,P7")]
    [InlineData("u0,k0,u5,k0,u0", "P5")]
    [InlineData("", "")]
    public void Any_sequence_of_blocks_makes_exactly_the_calls_in_order(string blocks, string expected)
    {
        var sink = new Recorder();
        var book = new TransmitBook(sink);
        long total = 0;

        foreach (string block in blocks.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int n = int.Parse(block[1..]);
            if (block[0] == 'u')
            {
                // The samples carry their own position, so a reorder or a repeat shows in the content too.
                var samples = new float[n];
                for (int i = 0; i < n; i++)
                {
                    samples[i] = total + i;
                }

                book.Heard(samples);
            }
            else
            {
                book.Keyed(n);
            }

            total += n;
            book.SampleCount.Should().Be(total, "the count is the samples seen, keyed or not");
        }

        string.Join(",", sink.Calls).Should().Be(expected);
        sink.NextPosition.Should().Be(total, "every sample of every pushed block arrived once, in order");
        book.SampleCount.Should().Be(total);
    }

    [Fact]
    public void A_negative_keyed_count_is_refused_and_counts_nothing()
    {
        var book = new TransmitBook(new Recorder());

        Action act = () => book.Keyed(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
        book.SampleCount.Should().Be(0);
    }

    [Fact]
    public void A_book_needs_a_station()
    {
        Action act = () => _ = new TransmitBook(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class Recorder : IStationClockInput
    {
        public List<string> Calls { get; } = [];

        /// <summary>The position the next pushed sample must carry: pushes and skips together.</summary>
        public long NextPosition { get; private set; }

        public void Push(ReadOnlySpan<float> samples)
        {
            Calls.Add($"P{samples.Length}");
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i].Should().Be(NextPosition + i);
            }

            NextPosition += samples.Length;
        }

        public void Skip(int count)
        {
            Calls.Add($"S{count}");
            NextPosition += count;
        }
    }
}
