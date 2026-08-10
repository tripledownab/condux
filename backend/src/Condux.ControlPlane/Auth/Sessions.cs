using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>Issues the server-side session cookie (isolated so OIDC/SSO #71 can reuse the seam).</summary>
internal static class Sessions
{
    /// <summary>
    /// Mints a server-side session for a user and sets it as a first-party session cookie. <c>Secure</c>
    /// is tied to the request scheme so the cookie is Secure in prod (https) yet still round-trips over
    /// the in-memory http TestServer. <c>SameSite=Lax</c> suits the same-origin dashboard.
    /// </summary>
    public static async Task IssueAsync(User user, SessionRepository sessions, HttpContext http)
    {
        var (raw, hash) = SessionTokens.Create();
        var expires = DateTimeOffset.UtcNow.Add(SessionAuth.Lifetime);
        await sessions.CreateAsync(user.Id, hash, expires);
        http.Response.Cookies.Append(SessionAuth.Cookie, raw, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = expires,
            Path = "/",
        });
    }
}
