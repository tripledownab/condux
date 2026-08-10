using System.Security.Cryptography;
using System.Text;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Shared CSRF-state and post-flow redirect helpers for the OIDC browser flows — Google sign-in (#71) and
/// per-org enterprise SSO (#72). The state cookie is a double-submit CSRF defense (no server-side state
/// table). The dashboard origin mirrors the GitHub connect redirect: the first CORS origin in split-origin
/// dev, empty (a same-host relative redirect) in same-origin production.
/// </summary>
internal static class OidcFlow
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>An unguessable, URL-safe token for the CSRF state cookie.</summary>
    public static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Constant-time compare of the returned state against the cookie, to resist timing oracles.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // SameSite=Lax still rides the top-level GET redirect back from the identity provider.
    public static CookieOptions StateCookieOptions(HttpContext http) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.Add(StateLifetime),
        Path = "/",
    };

    /// <summary>Land on the dashboard root after a successful sign-in.</summary>
    public static string DashboardUrl(IConfiguration cfg) => $"{DashboardOrigin(cfg)}/";

    /// <summary>Back to login with an error code the page surfaces as a banner.</summary>
    public static string LoginUrl(IConfiguration cfg, string error) => $"{DashboardOrigin(cfg)}/login?error={error}";

    private static string DashboardOrigin(IConfiguration cfg)
    {
        var origins = (cfg["CONDUX_CORS_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return origins.Length > 0 ? origins[0] : string.Empty;
    }
}
