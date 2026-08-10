using Condux.ControlPlane.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.ControlPlane.Tests;

// AppUrls.BaseUrl builds the absolute base for links that must work outside the browser: invite emails and
// the Stripe checkout/portal return URLs. The billing regression it guards: in same-origin production
// CONDUX_CORS_ORIGINS is empty, so a CORS-derived origin came out relative and Stripe rejected the checkout
// and portal calls. The base must come from CONDUX_APP_BASE_URL and stay absolute.
public class AppUrlsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Prod_uses_the_absolute_app_base_url_even_when_cors_origins_is_empty()
    {
        var baseUrl = AppUrls.BaseUrl(Config(
            ("CONDUX_APP_BASE_URL", "https://app.condux.ai"),
            ("CONDUX_CORS_ORIGINS", "")));

        Assert.Equal("https://app.condux.ai", baseUrl);
    }

    [Fact]
    public void Dev_falls_back_to_the_first_cors_origin_when_app_base_url_is_unset()
    {
        var baseUrl = AppUrls.BaseUrl(Config(("CONDUX_CORS_ORIGINS", "http://localhost:3000")));

        Assert.Equal("http://localhost:3000", baseUrl);
    }

    [Fact]
    public void Trailing_slash_is_trimmed_so_a_leading_slash_path_concatenates_cleanly()
    {
        var baseUrl = AppUrls.BaseUrl(Config(("CONDUX_APP_BASE_URL", "https://app.condux.ai/")));

        Assert.Equal("https://app.condux.ai", baseUrl);
    }

    [Fact]
    public void Empty_when_neither_is_configured()
    {
        Assert.Equal(string.Empty, AppUrls.BaseUrl(Config()));
    }
}
