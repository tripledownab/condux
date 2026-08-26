using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Condux.ControlPlane.Auth;

/// <summary>Session cookie/scheme constants shared by the auth handler and the endpoints.</summary>
internal static class SessionAuth
{
    public const string Scheme = "Condux";
    public const string Cookie = "condux_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a session that has passed the password but not the second factor stays usable. Minutes,
    /// not the full session lifetime: it is a challenge window, and anything longer is time an attacker
    /// who already has the password can spend guessing codes.
    /// </summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Resolves the session cookie to a <see cref="ClaimsPrincipal"/> (user id + email). No cookie /
/// invalid session → NoResult (anonymous), which <c>RequireAuthorization</c> turns into a 401.
/// </summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionRepository sessions,
    EmailAllowlist platformAdmins)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Cookies[SessionAuth.Cookie] is not { Length: > 0 } raw)
        {
            return AuthenticateResult.NoResult();
        }

        if (await sessions.GetActiveUserAsync(SessionTokens.HashToken(raw)) is not { } user)
        {
            return AuthenticateResult.NoResult();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Email, user.Email),
        };
        // Platform-admin is an env allowlist resolved fresh on every request (never a DB flag), so it
        // can't be escalated by a data write and always reflects the current CONDUX_PLATFORM_ADMIN_EMAILS.
        if (platformAdmins.Contains(user.Email))
        {
            claims.Add(new Claim(PlatformAdmin.Claim, "true"));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
