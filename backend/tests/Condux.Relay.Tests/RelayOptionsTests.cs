using Condux.Relay.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>How the relay reads CONDUX_TRUSTED_PROXIES: unset trusts no one, and a list that does not
/// parse stops the relay rather than trusting more or less than was meant.</summary>
public class RelayOptionsTests
{
    private static RelayOptions From(string? trustedProxies) => RelayOptions.FromEnv(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CONDUX_TRUSTED_PROXIES"] = trustedProxies })
            .Build());

    [Fact]
    public void Unset_trusts_no_proxy()
    {
        Assert.Empty(From(null).TrustedProxies);
        Assert.Empty(From("").TrustedProxies);
    }

    [Fact]
    public void A_list_of_ranges_is_read_whole()
    {
        var proxies = From(" 172.16.0.0/12, 192.168.0.0/16 ,fd00::/8").TrustedProxies;

        Assert.Equal(["172.16.0.0/12", "192.168.0.0/16", "fd00::/8"], proxies.Select(p => p.ToString()));
    }

    [Theory]
    [InlineData("172.16.0.0/12,not-a-range")]
    [InlineData("10.0.0.0/33")]
    public void A_list_that_does_not_parse_stops_the_relay(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => From(value));
        Assert.Contains("CONDUX_TRUSTED_PROXIES", error.Message);
    }
}
