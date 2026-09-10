using System.Security.Cryptography;
using System.Text;
using Condux.Core.Http;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Shared CSRF-state and post-flow redirect helpers for the browser flows that leave the site and come
/// back: Google sign-in (#71), per-org enterprise SSO (#72), and GitHub connect through
/// <see cref="GithubConnectFlow"/>. The state cookie is a double-submit CSRF defense (no server-side state
/// table). The dashboard origin comes from <see cref="AppOrigins"/>: the first CORS origin in split-origin
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

    // SameSite=Lax still rides the top-level GET redirect back from the identity provider. A flow whose
    // return leg is a cross-site POST (the SAML ACS) needs crossSite: Lax cookies do not accompany those,
    // and SameSite=None requires Secure even in dev (browsers accept Secure on http://localhost), which
    // is why that one case sets Secure here. Transport is not this function's decision: CookieSecurity
    // sets Secure for every cookie the app writes, and it only ever adds the attribute. A flow whose own
    // state outlives StateLifetime passes its lifetime, so the cookie never expires first and fails a
    // return the state would still have accepted.
    public static CookieOptions StateCookieOptions(
        bool crossSite = false, TimeSpan? lifetime = null) => new()
        {
            HttpOnly = true,
            Secure = crossSite,
            SameSite = crossSite ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(lifetime ?? StateLifetime),
            Path = "/",
        };

    /// <summary>Land on the dashboard root after a successful sign-in.</summary>
    public static string DashboardUrl(IConfiguration cfg) => $"{DashboardOrigin(cfg)}/";

    /// <summary>
    /// Back to login to finish a second factor. A redirect sign-in cannot return JSON, so the page is
    /// told to show the challenge by a query flag; the session cookie already carries the actual state,
    /// so this parameter is a hint and forging it grants nothing.
    /// </summary>
    public static string MfaChallengeUrl(IConfiguration cfg) => $"{DashboardOrigin(cfg)}/login?mfa=1";

    /// <summary>Back to login with an error code the page surfaces as a banner.</summary>
    public static string LoginUrl(IConfiguration cfg, string error) => $"{DashboardOrigin(cfg)}/login?error={error}";

    // Deliberately NOT AppUrls.BaseUrl: this one must not fall back to CONDUX_APP_BASE_URL. Same-origin
    // production sets no CORS origin, and an empty origin here is what makes the redirect same-host and
    // relative, which is the behaviour that flow wants. Only the parse is shared.
    private static string DashboardOrigin(IConfiguration cfg) =>
        AppOrigins.Parse(cfg["CONDUX_CORS_ORIGINS"]).FirstOrDefault() ?? string.Empty;
}
