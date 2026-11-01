using System.Net;
using Condux.Relay.Ingest;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>Which address a body is charged as: a trusted proxy's last X-Forwarded-For entry, the peer
/// otherwise, with IPv6 grouped by /64.</summary>
public class IngestSendersTests
{
    private static readonly IPNetwork[] Proxies = [IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("fd00::/8")];

    [Theory]
    [InlineData("203.0.113.5", "198.51.100.7", "203.0.113.5")]            // not a proxy: the peer is the client
    [InlineData("10.1.2.3", "198.51.100.7", "10.1.2.3")]                  // private, but not listed
    [InlineData("127.0.0.1", "198.51.100.7", "127.0.0.1")]
    [InlineData("172.18.0.3", "198.51.100.7", "198.51.100.7")]            // a listed proxy names the client
    [InlineData("172.18.0.3", "6.6.6.6, 198.51.100.7", "198.51.100.7")]   // what the client claimed is ignored
    [InlineData("172.18.0.3", " 198.51.100.7 ", "198.51.100.7")]
    [InlineData("172.18.0.3", "not an address", "172.18.0.3")]
    [InlineData("172.18.0.3", "198.51.100.7,", "172.18.0.3")]
    [InlineData("172.18.0.3", null, "172.18.0.3")]
    [InlineData("172.15.255.255", "198.51.100.7", "172.15.255.255")]      // just outside the listed range
    [InlineData("172.31.255.255", "198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:172.18.0.3", "198.51.100.7", "198.51.100.7")]     // a listed IPv4 proxy seen over IPv6
    [InlineData("::ffff:203.0.113.5", null, "203.0.113.5")]
    [InlineData("fd12::1", "198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8::1", "198.51.100.7", "2001:db8::/64")]           // an IPv6 client is its /64
    [InlineData("172.18.0.3", "::ffff:198.51.100.7", "198.51.100.7")]
    public void The_address_is_a_trusted_proxys_last_entry_or_the_peer(string peer, string? forwarded, string expected)
    {
        Assert.Equal(expected, IngestSenders.ToAddress(IPAddress.Parse(peer), new StringValues(forwarded), Proxies));
    }

    [Fact]
    public void With_no_proxy_trusted_the_header_is_never_read()
    {
        Assert.Equal("172.18.0.3", IngestSenders.ToAddress(IPAddress.Parse("172.18.0.3"), "198.51.100.7", []));
    }

    [Fact]
    public void The_last_entry_of_the_last_header_is_the_one_the_nearest_proxy_wrote()
    {
        var forwarded = new StringValues(["6.6.6.6", "7.7.7.7, 198.51.100.7"]);

        Assert.Equal("198.51.100.7", IngestSenders.ToAddress(IPAddress.Parse("172.18.0.3"), forwarded, Proxies));
    }

    // One host is usually given a whole /64, so its addresses are one sender.
    [Fact]
    public void IPv6_addresses_in_one_64_are_one_sender_and_in_another_are_not()
    {
        var peer = IPAddress.Parse("172.18.0.3");

        var a = IngestSenders.ToAddress(peer, "2001:db8:1:2:aaaa::1", Proxies);
        var b = IngestSenders.ToAddress(peer, "2001:db8:1:2:bbbb::2", Proxies);
        var c = IngestSenders.ToAddress(peer, "2001:db8:1:3::1", Proxies);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void No_peer_is_one_unknown_sender()
    {
        Assert.Equal("-", IngestSenders.ToAddress(null, "198.51.100.7", Proxies));
    }
}
