using Condux.ControlPlane.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.ControlPlane.Tests;

// LinearConfig.FromEnv decides whether Linear is on (ADR-0053). Every integration test sets the full config,
// so an edit that let a partial one start, or let one start with nothing to seal the grant with, would leave
// the rest of the suite green. These are the only tests of those two refusals.
public class LinearConfigTests
{
    private static readonly (string Key, string Value)[] Complete =
    [
        ("CONDUX_LINEAR_CLIENT_ID", "lin-id"),
        ("CONDUX_LINEAR_CLIENT_SECRET", "lin-secret"),
        ("CONDUX_LINEAR_OAUTH_REDIRECT_URI", "https://app.example.com/api/linear/oauth/callback"),
    ];

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void No_linear_variables_leaves_the_feature_off_with_or_without_a_secret_key()
    {
        Assert.False(LinearConfig.FromEnv(Config(), secretsEnabled: true).Enabled);
        Assert.False(LinearConfig.FromEnv(Config(), secretsEnabled: false).Enabled);
    }

    [Fact]
    public void A_complete_config_with_a_secret_key_turns_it_on()
    {
        var config = LinearConfig.FromEnv(Config(Complete), secretsEnabled: true);

        Assert.True(config.Enabled);
        Assert.Equal("lin-id", config.Options!.ClientId);
        Assert.Equal("https://app.example.com/api/linear/oauth/callback", config.Options.RedirectUri);
    }

    [Theory]
    [InlineData("CONDUX_LINEAR_CLIENT_ID")]
    [InlineData("CONDUX_LINEAR_CLIENT_SECRET")]
    [InlineData("CONDUX_LINEAR_OAUTH_REDIRECT_URI")]
    public void Any_one_missing_refuses_to_start(string missing)
    {
        var partial = Complete.Where(e => e.Key != missing).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => LinearConfig.FromEnv(Config(partial), secretsEnabled: true));
        Assert.Contains("partially configured", error.Message);
    }

    // The grant is sealed at rest, so a deployment with Linear and no master key has nowhere safe to put it.
    [Fact]
    public void A_complete_config_without_a_secret_key_refuses_to_start_and_names_the_variable()
    {
        var error = Assert.Throws<InvalidOperationException>(() => LinearConfig.FromEnv(Config(Complete), secretsEnabled: false));
        Assert.Contains("CONDUX_SECRET_KEY", error.Message);
    }
}
