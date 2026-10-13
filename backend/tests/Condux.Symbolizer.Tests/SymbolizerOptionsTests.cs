using Condux.Symbolizer;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.Symbolizer.Tests;

/// <summary>A cache size that is set must be usable: unset takes the default, and anything that would be
/// ignored or would evict each file as it is stored stops the service at startup.</summary>
public sealed class SymbolizerOptionsTests
{
    private static SymbolizerOptions Read(string? cacheMaxBytes) => SymbolizerOptions.FromConfiguration(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CONDUX_SYMBOLIZER_CACHE_MAX_BYTES"] = cacheMaxBytes })
            .Build());

    [Fact]
    public void An_unset_cache_size_takes_the_default()
    {
        Assert.Equal(2L * 1024 * 1024 * 1024, Read(null).CacheMaxBytes);
    }

    [Fact]
    public void A_cache_that_holds_at_least_one_file_is_accepted()
    {
        Assert.Equal(SymbolizerOptions.DefaultMaxFileBytes, Read($"{SymbolizerOptions.DefaultMaxFileBytes}").CacheMaxBytes);
    }

    [Theory]
    [InlineData("2GB")]
    [InlineData("-1")]
    [InlineData("1048576")]
    public void A_cache_size_that_cannot_hold_a_file_stops_the_service(string value)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Read(value));
        Assert.Contains("CONDUX_SYMBOLIZER_CACHE_MAX_BYTES", refused.Message);
    }
}
