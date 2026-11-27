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

    /// <summary>
    /// The transmit half as a script. Tokens: uN heard block of N samples, kN keyed block of N; rI@E the station requested I with earliest sample E; due asks for the request to hand over (D1 or D-);
    /// wI withdrawn; fI the channel refused I (F1 if the station is to be told, f- if not); aI@E the transmitter has I on air, earliest E; xI the transmitter has finished with I.
    /// The expected string is every station call and every answer, in order: P Push, S Skip, Start and End with the index they carry.
    /// </summary>
    [Theory]
    // The on-air mark before the first keyed block of the keyup: the start is that block's index and is reported before it is skipped.
    [InlineData("r1@0,due,a1@0,u960,k960,k960,u960", "D1,P960,Start1@960,S960,S960,End1@2880,P960")]
    // During: the run is in progress when the mark arrives, so the start is the run's first block, reported at once.
    [InlineData("r1@0,due,u960,k960,a1@0,k960,u960", "D1,P960,S960,Start1@960,S960,End1@2880,P960")]
    // After: the first run ended with nothing on air (somebody else's keyup), so the mark waits for the next run.
    [InlineData("r1@0,due,k960,u960,a1@0,k960,u960", "D1,S960,P960,Start1@1920,S960,End1@2880,P960")]
    // The mark arrives in the same block as the end of the run: start and end together, in order, before the block is pushed.
    [InlineData("r1@0,due,k960,k960,a1@0,u960", "D1,S960,S960,Start1@0,End1@1920,P960")]
    // A keyed run that is nobody's: only skips.
    [InlineData("k960,k960,u960,k480,u480", "S960,S960,P960,S480,P480")]
    [InlineData("u960,k1,u1", "P960,S1,P1")]
    // A request the station withdrew that went on air anyway: the station is told it started and ended, and the request is not handed over afterwards.
    [InlineData("r1@0,due,w1,due,a1@0,k960,u960", "D1,D-,Start1@0,S960,End1@960,P960")]
    // A withdrawal before hand-over: nothing is handed over.
    [InlineData("r1@0,w1,due", "D-")]
    // A keyed run with no block inside it: the finish with nothing seen reports start and end at the count.
    [InlineData("u960,r1@0,due,a1@0,x1,u960", "P960,D1,Start1@960,End1@960,P960")]
    // The finish while the run is going on is not its end: that comes at the first unkeyed block.
    [InlineData("r1@0,due,a1@0,k960,x1,k960,u960", "D1,Start1@0,S960,S960,End1@1920,P960")]
    // The finish after the end changes nothing.
    [InlineData("r1@0,due,a1@0,k960,u960,x1,u960", "D1,Start1@0,S960,End1@960,P960,P960")]
    // A request whose earliest sample is still ahead is not handed over until the count has reached it, and then once.
    [InlineData("r1@1920,due,u960,due,u960,due,due", "D-,P960,D-,P960,D1,D-")]
    // A keyup that was running when the request was handed over starts the run before the request's earliest sample: the start is clamped to it, never ahead of the count.
    [InlineData("k960,r1@500,due,a1@500,k960,u960", "S960,D1,Start1@500,S960,End1@1920,P960")]
    // A refusal is for the station unless the request is on air.
    [InlineData("r1@0,due,f1", "D1,F1")]
    [InlineData("r1@0,due,a1@0,k960,f1,u960", "D1,Start1@0,S960,f-,End1@960,P960")]
    [InlineData("r1@0,due,f1,due,r2@0,due", "D1,F1,D-,D2")]
    // A second request marked on air while one is: ignored, the first one's keyup is reported alone.
    [InlineData("a1@0,k960,a2@0,u960", "Start1@0,S960,End1@960,P960")]
    // A new request replaces one that was not handed over.
    [InlineData("r1@0,r2@0,due,due", "D2,D-")]
    // Back to back, one request after another, each its own run.
    [InlineData("r1@0,due,a1@0,k960,u960,r2@960,due,a2@960,k480,u960", "D1,Start1@0,S960,End1@960,P960,D2,Start2@1920,S480,End2@2400,P960")]
    public void The_transmit_half_reports_exact_indices_in_the_order_the_station_needs(string script, string expected)
    {
        var sink = new Recorder();
        var book = new TransmitBook(sink);
        var earliest = new Dictionary<long, long>();

        foreach (string token in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token[0])
            {
                case 'u':
                {
                    var samples = new float[int.Parse(token[1..])];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = sink.NextPosition + i;
                    }

                    book.Heard(samples);
                    break;
                }
                case 'k':
                    book.Keyed(int.Parse(token[1..]));
                    break;
                case 'r':
                {
                    string[] parts = token[1..].Split('@');
                    long id = long.Parse(parts[0]);
                    earliest[id] = long.Parse(parts[1]);
                    book.Requested(id, earliest[id]);
                    break;
                }
                case 'd':
                    sink.Calls.Add(book.TakeDue(out long due) ? $"D{due}" : "D-");
                    break;
                case 'w':
                    book.Withdrawn(long.Parse(token[1..]));
                    break;
                case 'f':
                    sink.Calls.Add(book.Refused(long.Parse(token[1..])) ? $"F{token[1..]}" : "f-");
                    break;
                case 'a':
                {
                    string[] parts = token[1..].Split('@');
                    long id = long.Parse(parts[0]);
                    earliest[id] = long.Parse(parts[1]);
                    book.OnAir(id, earliest[id]);
                    break;
                }
                case 'x':
                    book.Finished(long.Parse(token[1..]));
                    break;
                default:
                    throw new ArgumentException($"unknown token {token}");
            }

            book.SampleCount.Should().Be(sink.NextPosition, "the book's count is the station's clock after every step");
        }

        string.Join(",", sink.Calls).Should().Be(expected);
        sink.Starts.Should().OnlyContain(s => s.Index >= earliest[s.Id], "a start is never before the request's earliest sample");
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

        public List<(long Id, long Index)> Starts { get; } = [];

        private long _lastStart = -1;
        private long _openId = -1;

        public void TransmitStarted(long id, long sampleIndex)
        {
            sampleIndex.Should().BeLessThanOrEqualTo(NextPosition, "a start is never ahead of the station's clock");
            _openId.Should().Be(-1, "one transmission at a time");
            _openId = id;
            _lastStart = sampleIndex;
            Starts.Add((id, sampleIndex));
            Calls.Add($"Start{id}@{sampleIndex}");
        }

        public void TransmitEnded(long id, long sampleIndex)
        {
            sampleIndex.Should().BeLessThanOrEqualTo(NextPosition, "an end is never ahead of the station's clock");
            sampleIndex.Should().BeGreaterThanOrEqualTo(_lastStart, "an end is never before its start");
            _openId.Should().Be(id, "an end follows its own start");
            _openId = -1;
            Calls.Add($"End{id}@{sampleIndex}");
        }

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
