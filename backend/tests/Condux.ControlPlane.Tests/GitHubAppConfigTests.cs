using System.Security.Cryptography;
using Condux.ControlPlane.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.ControlPlane.Tests;

// GitHubAppConfig.FromEnv decides whether the GitHub App is on, and since ADR-0045 it is the rule that
// keeps the unverified path from coming back. Linking an installation requires asking GitHub which ones
// the user can reach, which needs the client secret and callback URL, so a config missing either has to
// refuse to start rather than fall back to trusting an installation id from a query string. Nothing else
// guards that: every other test sets the full config, so an edit making the pair optional again would
// leave the whole suite green.
public class GitHubAppConfigTests
{
    private static readonly string Pem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    private static readonly (string Key, string Value)[] Complete =
    [
        ("CONDUX_GITHUB_CLIENT_ID", "Iv1.test"),
        ("CONDUX_GITHUB_WEBHOOK_SECRET", "webhook-secret"),
        ("CONDUX_GITHUB_APP_SLUG", "condux-test"),
        ("CONDUX_GITHUB_PRIVATE_KEY", Pem),
        ("CONDUX_GITHUB_CLIENT_SECRET", "client-secret"),
        ("CONDUX_GITHUB_OAUTH_REDIRECT_URI", "https://app.example.com/api/github/oauth/callback"),
    ];

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void No_github_variables_at_all_leaves_the_feature_off()
    {
        var config = GitHubAppConfig.FromEnv(Config(("CONDUX_POSTGRES", "irrelevant")));

        Assert.False(config.Enabled);
    }

    [Fact]
    public void The_complete_set_enables_it()
    {
        var config = GitHubAppConfig.FromEnv(Config(Complete));

        Assert.True(config.Enabled);
        Assert.Equal("condux-test", config.AppSlug);
        Assert.Equal("client-secret", config.ClientSecret);
        Assert.Equal("https://app.example.com/api/github/oauth/callback", config.OAuthRedirectUri);
    }

    // Each value in turn, so no single one can quietly become optional. The user-authorization pair is
    // the reason this test exists, but naming only those two would let the older required values drift.
    [Theory]
    [InlineData("CONDUX_GITHUB_CLIENT_ID")]
    [InlineData("CONDUX_GITHUB_WEBHOOK_SECRET")]
    [InlineData("CONDUX_GITHUB_APP_SLUG")]
    [InlineData("CONDUX_GITHUB_PRIVATE_KEY")]
    [InlineData("CONDUX_GITHUB_CLIENT_SECRET")]
    [InlineData("CONDUX_GITHUB_OAUTH_REDIRECT_URI")]
    public void Any_missing_value_refuses_to_start(string missing)
    {
        var partial = Complete.Where(e => e.Key != missing).ToArray();

        var failure = Assert.Throws<InvalidOperationException>(() => GitHubAppConfig.FromEnv(Config(partial)));

        // The message has to name what to set: a startup crash that does not is how a mounted-key
        // permission problem once read as the sign-in buttons disappearing.
        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }
}
