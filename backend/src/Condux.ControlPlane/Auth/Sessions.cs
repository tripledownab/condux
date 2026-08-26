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
    /// <param name="mfaPending">
    /// True when the password succeeded but the second factor has not. The cookie is issued either way,
    /// which is what lets a redirect-based sign-in (Google) carry the challenge without threading a token
    /// through a URL. What differs is that the session resolves to nobody until it is promoted, and that
    /// it expires in minutes rather than weeks: a half-authenticated session left alive for the full
    /// session lifetime would give anyone holding the password a month to grind codes against it.
    /// </param>
    public static async Task IssueAsync(
        User user, SessionRepository sessions, HttpContext http, bool mfaPending = false)
    {
        var (raw, hash) = SessionTokens.Create();
        var expires = DateTimeOffset.UtcNow.Add(mfaPending ? SessionAuth.PendingLifetime : SessionAuth.Lifetime);
        await sessions.CreateAsync(user.Id, hash, expires, mfaPending: mfaPending);
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
