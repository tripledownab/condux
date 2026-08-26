using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Local email+password accounts + cookie sessions (#47). Signup creates ONLY the user (ADR-0018:
/// the org is created deliberately in onboarding, or joined via an invite)
/// so a new user always has a tenant context (#48). Session issuance is isolated in
/// <see cref="Sessions"/> so OIDC/SSO (#71/#72) can reuse it.
/// </summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/signup",
                async Task<Results<Ok<AuthUserResponse>, Conflict<ErrorResponse>, BadRequest<ErrorResponse>>> (
                    Credentials req, UserRepository users,
                    SessionRepository sessions, EmailAllowlist platformAdmins, HttpContext http) =>
                {
                    if (!Emails.IsValid(req.Email) || string.IsNullOrEmpty(req.Password)
                        || req.Password.Length < 8 || req.Password.Length > 200)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_credentials"));
                    }

                    var email = Emails.Normalize(req.Email);
                    if (await users.GetByEmailAsync(email) is not null)
                    {
                        return TypedResults.Conflict(new ErrorResponse("email_taken"));
                    }

                    var user = await users.CreateAsync(email, PasswordHasher.Hash(req.Password));
                    await Sessions.IssueAsync(user, sessions, http);
                    return TypedResults.Ok(new AuthUserResponse(
                        user.Id, user.Email, platformAdmins.Contains(user.Email), Onboarded: user.OnboardedAt is not null));
                })
            .WithName("signup").WithTags("Auth");

        app.MapPost("/api/auth/login",
                async Task<Results<Ok<AuthUserResponse>, UnauthorizedHttpResult>> (
                    Credentials req, UserRepository users, SessionRepository sessions,
                    EmailAllowlist platformAdmins, MultiFactor mfa, HttpContext http) =>
                {
                    var user = await users.GetByEmailAsync(Emails.Normalize(req.Email ?? string.Empty));
                    if (user?.PasswordHash is null
                        || !PasswordHasher.Verify(req.Password ?? string.Empty, user.PasswordHash))
                    {
                        // Generic 401 for unknown email, wrong password, or a federated (no-password) account.
                        return TypedResults.Unauthorized();
                    }

                    // The cookie is issued either way; when a second factor is enrolled it is issued
                    // PENDING, which resolves to nobody until /api/auth/mfa/verify promotes it. Carrying
                    // the challenge on the session rather than a separate token is what lets the
                    // redirect-based sign-ins reuse this without putting state in a URL.
                    var mfaRequired = await mfa.IsEnabledAsync(user.Id, http.RequestAborted);
                    await Sessions.IssueAsync(user, sessions, http, mfaPending: mfaRequired);
                    return TypedResults.Ok(new AuthUserResponse(
                        user.Id, user.Email, platformAdmins.Contains(user.Email),
                        Onboarded: user.OnboardedAt is not null, MfaRequired: mfaRequired));
                })
            .WithName("login").WithTags("Auth");

        app.MapPost("/api/auth/logout",
                async Task<NoContent> (SessionRepository sessions, HttpContext http) =>
                {
                    if (http.Request.Cookies[SessionAuth.Cookie] is { Length: > 0 } raw)
                    {
                        await sessions.RevokeAsync(SessionTokens.HashToken(raw));
                    }

                    http.Response.Cookies.Delete(SessionAuth.Cookie);
                    Impersonation.ClearCookie(http); // never leave a view-as session dangling past logout
                    return TypedResults.NoContent();
                })
            .WithName("logout").WithTags("Auth");

        // The current user, resolved from the session cookie by the auth handler. Id/Email/IsPlatformAdmin
        // are always the caller's real identity; Impersonation is non-null only while this admin is in a
        // read-only view-as session (the middleware validated + stashed the target org), so the dashboard
        // can show the banner.
        app.MapGet("/api/auth/me",
                async Task<Ok<AuthUserResponse>> (HttpContext http, OrgRepository orgs, UserRepository users) =>
                {
                    ImpersonationStateResponse? impersonation = null;
                    if (Impersonation.TargetOrgId(http) is { } orgId
                        && await orgs.GetAsync(orgId, http.RequestAborted) is { } org)
                    {
                        impersonation = new ImpersonationStateResponse(org.Id, org.Slug, org.Name);
                    }

                    var userId = OrgAuthorization.CurrentUserId(http.User);
                    var user = await users.GetByIdAsync(userId, http.RequestAborted);
                    return TypedResults.Ok(new AuthUserResponse(
                        userId, OrgAuthorization.CurrentUserEmail(http.User),
                        PlatformAdmin.IsPlatformAdmin(http.User), user?.OnboardedAt is not null, impersonation));
                })
            .WithName("me").WithTags("Auth").RequireAuthorization();
    }

}
