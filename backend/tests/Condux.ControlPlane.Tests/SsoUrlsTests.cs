using Condux.ControlPlane.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.ControlPlane.Tests;

/// <summary>
/// The three addresses an org registers in its IdP. Pinned because two of them are compared for equality
/// by someone else: the IdP matches the redirect URI against what it has registered, and the assertion's
/// audience is checked against the entity ID. A change here silently breaks every configured org, and
/// the failure surfaces at their IdP rather than in our logs.
/// </summary>
public class SsoUrlsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Builds_all_three_from_the_configured_app_base_url()
    {
        var cfg = Config(("CONDUX_APP_BASE_URL", "https://app.condux.ai"));

        Assert.Equal("https://app.condux.ai/api/auth/sso/callback", SsoUrls.RedirectUri(cfg));
        Assert.Equal("https://app.condux.ai/api/auth/sso/saml", SsoUrls.SamlEntityId(cfg));
        Assert.Equal("https://app.condux.ai/api/auth/sso/saml/acs", SsoUrls.SamlAcsUrl(cfg));
    }

    [Fact]
    public void Trailing_slash_on_the_base_url_does_not_double_up()
    {
        // A pasted base URL often carries one, and "https://app.condux.ai//api/auth/sso/saml" is a
        // different entity ID to the IdP than the one without, so the assertion audience would not match.
        var cfg = Config(("CONDUX_APP_BASE_URL", "https://app.condux.ai/"));

        Assert.Equal("https://app.condux.ai/api/auth/sso/saml", SsoUrls.SamlEntityId(cfg));
    }

    [Fact]
    public void Falls_back_to_the_first_cors_origin_in_split_origin_dev()
    {
        // Dev serves the dashboard and the API on different ports, so there is no single origin to infer
        // from. AppUrls already resolves this; the point here is that all three follow it.
        var cfg = Config(("CONDUX_CORS_ORIGINS", "http://localhost:3000,http://localhost:3001"));

        Assert.Equal("http://localhost:3000/api/auth/sso/callback", SsoUrls.RedirectUri(cfg));
        Assert.Equal("http://localhost:3000/api/auth/sso/saml/acs", SsoUrls.SamlAcsUrl(cfg));
    }
}
