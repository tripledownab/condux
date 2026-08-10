using System.Text.Json;

namespace Condux.Core.Auth;

/// <summary>The verified identity from an OIDC id_token: a stable subject id + the account email.</summary>
public sealed record OidcIdentity(string Subject, string Email);

/// <summary>
/// Parses and validates an OIDC id_token's claims. The control-plane obtains the token directly from the
/// provider's token endpoint over TLS during the authorization-code exchange, so per OpenID Connect Core
/// 3.1.3.7 the signature does not need to be re-verified here; this validates the security-relevant claims
/// instead: the audience is our client, the issuer is one of the expected issuers, the token has not
/// expired, and the email is verified. Pure and dependency-free, so the whole validation is unit-tested in
/// CI with no network. Shared by Google sign-in and per-org enterprise SSO — each passes its own issuer(s).
/// </summary>
public static class OidcIdToken
{
    // A small allowance for clock skew between us and the provider when checking expiry.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns the identity when <paramref name="idToken"/> is a well-formed JWT whose claims satisfy:
    /// <c>aud</c> equals <paramref name="audience"/> (our client id), <c>iss</c> is one of
    /// <paramref name="issuers"/>, <c>exp</c> is in the future (with clock-skew allowance),
    /// <c>email_verified</c> is true, and both <c>email</c> and <c>sub</c> are present. Returns null on any
    /// failure — malformed token, wrong audience, wrong issuer, expired, or an unverified email.
    /// </summary>
    public static OidcIdentity? Validate(
        string? idToken, string[] issuers, string audience, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        JsonElement claims;
        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            claims = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }

        var issuer = GetString(claims, "iss");
        if (GetString(claims, "aud") != audience || issuer is null || Array.IndexOf(issuers, issuer) < 0)
        {
            return null;
        }

        if (!claims.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number
            || DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) + ClockSkew < now)
        {
            return null;
        }

        if (!IsEmailVerified(claims))
        {
            return null;
        }

        var email = GetString(claims, "email");
        var subject = GetString(claims, "sub");
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(subject))
        {
            return null;
        }

        return new OidcIdentity(subject, email);
    }

    // email_verified rides the token as a JSON bool, but some issuers stringify it ("true"); accept both.
    private static bool IsEmailVerified(JsonElement claims) =>
        claims.TryGetProperty("email_verified", out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
