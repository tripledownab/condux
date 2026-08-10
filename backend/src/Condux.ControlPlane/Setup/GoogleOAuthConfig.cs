namespace Condux.ControlPlane.Setup;

/// <summary>
/// "Sign in with Google" (OIDC) configuration, from env (<c>CONDUX_GOOGLE_*</c>). Opt-in: <see cref="Enabled"/>
/// only when the client id, client secret, and redirect uri are all present. A partial config fails fast
/// (a likely typo) rather than silently disabling the feature — matching the GitHub App config. When
/// unset the <c>/api/auth/oauth/google/*</c> routes return 404 and the login page hides the button.
/// </summary>
internal sealed record GoogleOAuthConfig(string? ClientId, string? ClientSecret, string? RedirectUri)
{
    public bool Enabled => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret)
        && !string.IsNullOrEmpty(RedirectUri);

    public static GoogleOAuthConfig FromEnv(IConfiguration cfg)
    {
        var clientId = cfg["CONDUX_GOOGLE_CLIENT_ID"];
        var clientSecret = cfg["CONDUX_GOOGLE_CLIENT_SECRET"];
        var redirectUri = cfg["CONDUX_GOOGLE_REDIRECT_URI"];

        var anySet = new[] { clientId, clientSecret, redirectUri }.Any(v => !string.IsNullOrEmpty(v));
        if (!anySet)
        {
            return new GoogleOAuthConfig(null, null, null); // feature off
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(redirectUri))
        {
            throw new InvalidOperationException(
                "Google sign-in is partially configured. Set CONDUX_GOOGLE_CLIENT_ID, CONDUX_GOOGLE_CLIENT_SECRET, "
                + "and CONDUX_GOOGLE_REDIRECT_URI, or unset them all.");
        }

        return new GoogleOAuthConfig(clientId, clientSecret, redirectUri);
    }
}
