using Condux.Linear;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// The Linear OAuth application, from <c>CONDUX_LINEAR_{CLIENT_ID,CLIENT_SECRET,OAUTH_REDIRECT_URI}</c>
/// (ADR-0053). Opt-in: unset, the Linear routes answer 404 and the dashboard shows no Linear surface. A
/// partial config fails fast, like the GitHub App, and so does a complete one without
/// <c>CONDUX_SECRET_KEY</c>, because the grant is sealed at rest and there would be nothing to seal it with.
/// </summary>
internal sealed record LinearConfig(LinearOptions? Options)
{
    public bool Enabled => Options is not null;

    public static LinearConfig FromEnv(IConfiguration cfg, bool secretsEnabled)
    {
        var clientId = cfg["CONDUX_LINEAR_CLIENT_ID"];
        var clientSecret = cfg["CONDUX_LINEAR_CLIENT_SECRET"];
        var redirectUri = cfg["CONDUX_LINEAR_OAUTH_REDIRECT_URI"];
        string?[] values = [clientId, clientSecret, redirectUri];

        if (values.All(string.IsNullOrEmpty))
        {
            return new LinearConfig(Options: null);
        }
        if (values.Any(string.IsNullOrEmpty))
        {
            throw new InvalidOperationException(
                "Linear is partially configured. Set CONDUX_LINEAR_CLIENT_ID, CONDUX_LINEAR_CLIENT_SECRET and "
                + "CONDUX_LINEAR_OAUTH_REDIRECT_URI (the OAuth application's callback URL), or unset them all.");
        }
        if (!secretsEnabled)
        {
            throw new InvalidOperationException(
                "Linear is configured but CONDUX_SECRET_KEY is not. The Linear grant is stored encrypted, so "
                + "set CONDUX_SECRET_KEY or unset the CONDUX_LINEAR_* variables.");
        }
        return new LinearConfig(new LinearOptions(clientId!, clientSecret!, redirectUri!));
    }
}
