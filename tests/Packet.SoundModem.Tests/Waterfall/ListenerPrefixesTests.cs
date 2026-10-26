using System.Net;
using AwesomeAssertions;
using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests.Waterfall;

public class ListenerPrefixesTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    public void Every_interface_is_one_wildcard_prefix(string bind)
    {
        ListenerPrefixes.For(bind, 8107, matchesHostOnly: false, () => IPAddress.Loopback)
            .Should().Equal("http://+:8107/");
    }

    [Fact]
    public void Loopback_also_answers_localhost_where_it_resolves_to_the_bound_address()
    {
        ListenerPrefixes.For("127.0.0.1", 8107, matchesHostOnly: false, () => IPAddress.Loopback)
            .Should().Equal("http://127.0.0.1:8107/", "http://localhost:8107/");
    }

    [Fact]
    public void Loopback_leaves_localhost_alone_where_it_would_open_another_socket()
    {
        ListenerPrefixes.For("127.0.0.1", 8107, matchesHostOnly: false, () => IPAddress.IPv6Loopback)
            .Should().Equal("http://127.0.0.1:8107/");
    }

    [Fact]
    public void Loopback_leaves_localhost_alone_where_it_does_not_resolve()
    {
        ListenerPrefixes.For("127.0.0.1", 8107, matchesHostOnly: false, () => null)
            .Should().Equal("http://127.0.0.1:8107/");
    }

    [Fact]
    public void Loopback_always_answers_localhost_where_prefixes_match_on_host_alone()
    {
        ListenerPrefixes.For("127.0.0.1", 8107, matchesHostOnly: true, () => IPAddress.IPv6Loopback)
            .Should().Equal("http://127.0.0.1:8107/", "http://localhost:8107/");
    }

    [Fact]
    public void Ipv6_loopback_answers_localhost_where_it_resolves_there()
    {
        ListenerPrefixes.For("[::1]", 8107, matchesHostOnly: false, () => IPAddress.IPv6Loopback)
            .Should().Equal("http://[::1]:8107/", "http://localhost:8107/");
    }

    [Fact]
    public void An_interface_address_is_only_itself()
    {
        ListenerPrefixes.For("192.0.2.10", 8107, matchesHostOnly: true, () => IPAddress.Loopback)
            .Should().Equal("http://192.0.2.10:8107/");
    }
}
