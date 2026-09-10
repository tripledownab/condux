using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>Issues the server-side session cookie (isolated so OIDC/SSO #71 can reuse the seam).</summary>
internal static class Sessions
{
    /// <summary>
    /// Mints a server-side session for a user and sets it as a first-party session cookie.
    /// <c>SameSite=Lax</c> suits the same-origin dashboard. <c>Secure</c> is deliberately absent here:
    /// the deployment decides it once for every cookie (<see cref="Setup.CookieSecurity"/>), because
    /// this request arrives over plain http from the edge proxy whatever the browser used.
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
            SameSite = SameSiteMode.Lax,
            Expires = expires,
            Path = "/",
        });
    }

    /// <summary>
    /// The caller, only if they can produce their password again. Null otherwise, including for a
    /// federated account, which has no password to produce.
    ///
    /// Sensitive actions re-ask rather than trusting the session: a session cookie proves someone signed
    /// in once on this browser, which is a weaker claim than "the person at the keyboard knows the
    /// password" and is exactly what an unattended machine or a stolen cookie gives an attacker.
    /// </summary>
    public static async Task<User?> ReauthenticateAsync(
        HttpContext http, UserRepository users, string? password)
    {
        var user = await users.GetByIdAsync(OrgAuthorization.CurrentUserId(http.User), http.RequestAborted);
        return user?.PasswordHash is not null && PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash)
            ? user
            : null;
    }

    /// <summary>
    /// Ends every other live session for the user, keeping the caller's own. Whatever just changed (the
    /// second factor, the password) was the rule those sessions were opened under, so leaving them alive
    /// would let the old credential keep working after the user believed they had replaced it.
    /// </summary>
    public static async Task RevokeOtherSessionsAsync(
        HttpContext http, SessionRepository sessions, long userId)
    {
        var keep = http.Request.Cookies[SessionAuth.Cookie] is { Length: > 0 } raw
            ? SessionTokens.HashToken(raw)
            : string.Empty;
        await sessions.RevokeAllExceptAsync(userId, keep, http.RequestAborted);
    }
}
