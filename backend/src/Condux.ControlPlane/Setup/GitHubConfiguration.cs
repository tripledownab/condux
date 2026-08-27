using Condux.GitHub;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// The control-plane's GitHub App configuration, from env (<c>CONDUX_GITHUB_*</c>). Opt-in: <see cref="Enabled"/>
/// only when the client id, webhook secret, app slug, and a readable private key are all present. A partial
/// config fails fast (a likely typo) rather than silently disabling the feature — matching the fail-fast
/// datastore config.
/// </summary>
internal sealed record GitHubAppConfig(
    GitHubAppOptions? Options, string? AppSlug, string? ClientSecret = null, string? OAuthRedirectUri = null)
{
    public bool Enabled => Options is not null && !string.IsNullOrEmpty(AppSlug);

    /// <summary>
    /// Whether connect may run the user-authorization (OAuth) leg, which needs the client secret and a
    /// registered callback URL on top of the app credentials. Optional so an existing deployment keeps
    /// working: without it connect falls back to the plain install URL, which cannot re-link an app that
    /// is already installed.
    /// </summary>
    public bool UserOAuthEnabled =>
        Enabled && !string.IsNullOrEmpty(ClientSecret) && !string.IsNullOrEmpty(OAuthRedirectUri);

    public static GitHubAppConfig FromEnv(IConfiguration cfg)
    {
        var clientId = cfg["CONDUX_GITHUB_CLIENT_ID"];
        var webhookSecret = cfg["CONDUX_GITHUB_WEBHOOK_SECRET"];
        var slug = cfg["CONDUX_GITHUB_APP_SLUG"];
        var pem = ResolveKey(cfg["CONDUX_GITHUB_PRIVATE_KEY_PATH"], cfg["CONDUX_GITHUB_PRIVATE_KEY"]);
        var clientSecret = cfg["CONDUX_GITHUB_CLIENT_SECRET"];
        var redirectUri = cfg["CONDUX_GITHUB_OAUTH_REDIRECT_URI"];

        var anySet = new[] { clientId, webhookSecret, slug, pem, clientSecret, redirectUri }
            .Any(v => !string.IsNullOrEmpty(v));
        if (!anySet)
        {
            return new GitHubAppConfig(null, null); // feature off
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(webhookSecret)
            || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(pem))
        {
            throw new InvalidOperationException(
                "GitHub App is partially configured. Set CONDUX_GITHUB_CLIENT_ID, CONDUX_GITHUB_WEBHOOK_SECRET, "
                + "CONDUX_GITHUB_APP_SLUG, and a readable CONDUX_GITHUB_PRIVATE_KEY_PATH (or "
                + "CONDUX_GITHUB_PRIVATE_KEY), or unset them all.");
        }

        // The OAuth pair is optional, but half of it is a typo rather than a choice.
        if (string.IsNullOrEmpty(clientSecret) != string.IsNullOrEmpty(redirectUri))
        {
            throw new InvalidOperationException(
                "GitHub user authorization is partially configured. Set both CONDUX_GITHUB_CLIENT_SECRET and "
                + "CONDUX_GITHUB_OAUTH_REDIRECT_URI (the app's callback URL), or neither.");
        }

        return new GitHubAppConfig(
            new GitHubAppOptions(clientId, pem, webhookSecret), slug, clientSecret, redirectUri);
    }

    // Inline key wins; otherwise read the .pem at the given path if it exists.
    //
    // A key that exists but cannot be read is a configuration error, not an absent one, so it still fails
    // fast. What it must not do is fail opaquely: the containers run as a non-root user, so a key owned by
    // root is invisible to them, and the bare UnauthorizedAccessException this used to surface points at
    // nothing. Startup then dies, every route 502s, and the reported symptom is unrelated (sign-in buttons
    // vanish, because the login page hides them when /api/auth/providers is unreachable).
    private static string? ResolveKey(string? path, string? inline)
    {
        if (!string.IsNullOrEmpty(inline))
        {
            return inline;
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The GitHub App private key at '{path}' exists but could not be read as "
                + $"'{Environment.UserName}'. A host-mounted key must be owned by the user the container "
                + "runs as, not root: derive that uid from the image rather than assuming it "
                + "(docker inspect <container> --format '{{.Config.User}}') and chown the file to it.",
                failure);
        }
    }
}
