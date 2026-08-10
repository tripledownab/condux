namespace Condux.Core.Auth;

/// <summary>
/// "Sign in with Google" id_token validation — a thin wrapper over the generic <see cref="OidcIdToken"/>
/// with Google's issuers baked in. Google delivers the token direct from its token endpoint over TLS, so
/// the signature is not re-verified (OIDC Core 3.1.3.7); see <see cref="OidcIdToken"/> for the claim checks.
/// </summary>
public static class GoogleIdToken
{
    private static readonly string[] Issuers = ["accounts.google.com", "https://accounts.google.com"];

    /// <summary>Validate a Google id_token: <see cref="OidcIdToken.Validate"/> with Google's issuers.</summary>
    public static OidcIdentity? Validate(string? idToken, string audience, DateTimeOffset now) =>
        OidcIdToken.Validate(idToken, Issuers, audience, now);
}
