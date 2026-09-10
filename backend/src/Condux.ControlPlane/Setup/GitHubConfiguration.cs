using Condux.GitHub;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// The control-plane's GitHub App configuration, from env (<c>CONDUX_GITHUB_*</c>). Opt-in: <see cref="Enabled"/>
/// only when every value is present. A partial config fails fast (a likely typo) rather than silently
/// disabling the feature, matching the fail-fast datastore config.
/// </summary>
/// <remarks>
/// The user-authorization pair (client secret and callback URL) used to be optional, and is not any more.
/// It is the only way to ask GitHub which installations a given person can reach: the app JWT lists every
/// installation of the app, meaning every customer, so it can never answer that. Without the pair the
/// Setup URL had to take an <c>installation_id</c> from its query and trust it, which let anyone who could
/// name a live but unlinked installation attach it to their own org and mint repository tokens for it.
/// There is no safe degraded mode, so a deployment that enables the app must configure the whole flow.
/// </remarks>
internal sealed record GitHubAppConfig(
    GitHubAppOptions? Options, string? AppSlug, string? ClientSecret = null, string? OAuthRedirectUri = null)
{
    public bool Enabled => Options is not null && !string.IsNullOrEmpty(AppSlug)
        && !string.IsNullOrEmpty(ClientSecret) && !string.IsNullOrEmpty(OAuthRedirectUri);

    public static GitHubAppConfig FromEnv(IConfiguration cfg)
    {
        var clientId = cfg["CONDUX_GITHUB_CLIENT_ID"];
        var webhookSecret = cfg["CONDUX_GITHUB_WEBHOOK_SECRET"];
        var slug = cfg["CONDUX_GITHUB_APP_SLUG"];
        var pem = GitHubPrivateKey.Resolve(
            cfg["CONDUX_GITHUB_PRIVATE_KEY"], cfg["CONDUX_GITHUB_PRIVATE_KEY_PATH"]);
        var clientSecret = cfg["CONDUX_GITHUB_CLIENT_SECRET"];
        var redirectUri = cfg["CONDUX_GITHUB_OAUTH_REDIRECT_URI"];

        var anySet = new[] { clientId, webhookSecret, slug, pem, clientSecret, redirectUri }
            .Any(v => !string.IsNullOrEmpty(v));
        if (!anySet)
        {
            return new GitHubAppConfig(null, null); // feature off
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(webhookSecret)
            || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(pem)
            || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(redirectUri))
        {
            throw new InvalidOperationException(
                "GitHub App is partially configured. Set CONDUX_GITHUB_CLIENT_ID, CONDUX_GITHUB_WEBHOOK_SECRET, "
                + "CONDUX_GITHUB_APP_SLUG, a readable CONDUX_GITHUB_PRIVATE_KEY_PATH (or "
                + "CONDUX_GITHUB_PRIVATE_KEY), CONDUX_GITHUB_CLIENT_SECRET and "
                + "CONDUX_GITHUB_OAUTH_REDIRECT_URI (the app's callback URL), or unset them all. The last "
                + "two were optional before and are not any more: without them the Setup URL has to trust "
                + "an installation id from its own query string.");
        }

        return new GitHubAppConfig(
            new GitHubAppOptions(clientId, pem, webhookSecret), slug, clientSecret, redirectUri);
    }

}
