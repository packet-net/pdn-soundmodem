using AwesomeAssertions;
using Packet.SoundModem.OfdmFmTool;
using Packet.SoundModem.Tests.Channel;

namespace Packet.SoundModem.Tests.OfdmFmTool;

public class FormatTests
{
    [Fact]
    public void A_line_starts_with_the_clock_to_hundredths_and_the_text_follows_unchanged()
    {
        var clock = new VirtualAir.Clock();
        clock.Elapse(TimeSpan.FromSeconds(3661.239));

        StampedLog.Format(clock, "Heard GB7RDG-1: 2 datagrams, signal 31 dB").Should().Be("01:01:01.23  Heard GB7RDG-1: 2 datagrams, signal 31 dB", "hundredths are cut, not rounded, so the line never reads ahead of the clock");
    }

    [Fact]
    public void Anything_but_plain_ascii_is_replaced()
    {
        StampedLog.Format(new VirtualAir.Clock(), "aéb\tc").Should().Be("00:00:00.00  a?b?c");
    }

    [Fact]
    public async Task Lines_reach_every_sink_in_order_stamped_when_written_not_when_printed()
    {
        var clock = new VirtualAir.Clock();
        var first = new List<string>();
        var second = new List<string>();
        var log = new StampedLog(clock, first.Add, second.Add);

        log.Write("one");
        clock.Elapse(TimeSpan.FromSeconds(2));
        log.Write("two");
        await log.DisposeAsync();

        first.Should().Equal("00:00:00.00  one", "00:00:02.00  two");
        second.Should().Equal(first);
    }

    [Fact]
    public async Task A_sink_that_fails_does_not_stop_the_others()
    {
        var kept = new List<string>();
        var log = new StampedLog(new VirtualAir.Clock(), _ => throw new IOException("disk full"), kept.Add);

        log.Write("one");
        log.Write("two");
        await log.DisposeAsync();

        kept.Should().HaveCount(2);
    }

    [Fact]
    public void A_rate_is_written_as_the_modems_trace_writes_it()
    {
        PingSummary.Rate(250).Should().Be("0.25");
        PingSummary.Rate(12500).Should().Be("12.5");
        PingSummary.Rate(70400).Should().Be("70.4");
    }

    [Fact]
    public void The_summary_says_both_ways_at_the_lower_of_the_two_rates()
    {
        new PingSummary(5, 5, 70400, 12500).ToString().Should().Be("5 sent, 5 answered, 12.5 kbit/s both ways");
        new PingSummary(5, 3, 12500, 70400).ToString().Should().Be("5 sent, 3 answered, 12.5 kbit/s both ways");
        new PingSummary(5, 5, 12500, 12500).BothWayBitsPerSecond.Should().Be(12500);
    }

    [Fact]
    public void The_summary_leaves_the_rate_out_when_one_direction_is_not_known()
    {
        new PingSummary(2, 0, null, null).ToString().Should().Be("2 sent, 0 answered");
        new PingSummary(2, 1, 12500, null).ToString().Should().Be("2 sent, 1 answered");
    }

    [Fact]
    public void An_echo_is_the_request_with_the_kind_changed_and_the_same_length()
    {
        byte[] request = Echo.MakeRequest(0x0102, 40);

        request.Should().HaveCount(40);
        Echo.TryRead(request, out byte kind, out ushort sequence).Should().BeTrue();
        (kind, sequence).Should().Be((Echo.Request, (ushort)0x0102));

        byte[] reply = Echo.MakeReply(request);
        reply.Should().HaveCount(40);
        Echo.TryRead(reply, out kind, out sequence).Should().BeTrue();
        (kind, sequence).Should().Be((Echo.Reply, (ushort)0x0102));
        reply.AsSpan(1).SequenceEqual(request.AsSpan(1)).Should().BeTrue();
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 9, 0, 1 })]
    public void Anything_else_is_not_an_echo(byte[] payload) => Echo.TryRead(payload, out _, out _).Should().BeFalse();

    [Fact]
    public void The_echo_protocol_is_not_one_the_modem_names()
    {
        new byte[] { 0x00, 0xCC, 0xCD, 0xCF, 0xF0 }.Should().NotContain(Echo.Protocol);
    }
}
