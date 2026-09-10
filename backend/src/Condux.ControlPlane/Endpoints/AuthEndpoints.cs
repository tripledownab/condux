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
    // A reset mails a stranger-supplied address, so it is a spam vector pointed at our own SMTP
    // reputation as much as at the recipient. Bounded per account rather than per request: the address is
    // what the sender controls, and it is what receives the mail.
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(1);
    private const int MaxResetsPerWindow = 3;

    /// <summary>
    /// Sends the reset mail outside the request. Failures are logged, never surfaced: telling a caller
    /// that delivery failed still tells them the address exists.
    /// </summary>
    private static async Task SendResetAsync(
        PasswordResetMailer mailer, string email, string rawToken, ILogger log)
    {
        try
        {
            await mailer.SendAsync(email, rawToken, CancellationToken.None);
        }
        catch (Exception failure)
        {
            log.LogError(failure, "Password reset email failed to send.");
        }
    }

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

        // Change password while signed in. Re-asks for the current one rather than trusting the session
        // cookie, and ends every OTHER session, because a password someone else knew must stop working
        // the moment it is replaced. The caller's own session survives so they are not signed out of the
        // page they just used.
        app.MapPost("/api/auth/password",
                async Task<Results<NoContent, UnauthorizedHttpResult, BadRequest<ErrorResponse>>> (
                    ChangePasswordRequest req, UserRepository users,
                    SessionRepository sessions, HttpContext http) =>
                {
                    // Same bounds as signup, stated once there and repeated here because this is a second
                    // way in and an unbounded value would reach the hasher either way.
                    if (string.IsNullOrEmpty(req.NewPassword)
                        || req.NewPassword.Length < 8 || req.NewPassword.Length > 200)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_password"));
                    }

                    // A federated account has no password to verify, so this returns null and the answer
                    // is 401 rather than an offer to set one: their identity provider owns the credential.
                    if (await Sessions.ReauthenticateAsync(http, users, req.CurrentPassword) is not { } user)
                    {
                        return TypedResults.Unauthorized();
                    }

                    await users.SetPasswordHashAsync(user.Id, PasswordHasher.Hash(req.NewPassword));
                    await Sessions.RevokeOtherSessionsAsync(http, sessions, user.Id);
                    return TypedResults.NoContent();
                })
            .WithName("changePassword").WithTags("Auth").RequireAuthorization();

        // Ask for a reset link. ALWAYS 202, whatever happens next, because any other answer turns this
        // into an oracle for which addresses have accounts. That means the failures below are logged and
        // not returned: no such user, a federated account with no password to reset, SMTP off, too many
        // requests. The user is told "if that address has an account, we have sent a link" either way.
        app.MapPost("/api/auth/password/forgot",
                async Task<Accepted> (
                    ForgotPasswordRequest req, UserRepository users, PasswordResetRepository resets,
                    PasswordResetMailer mailer, ILogger<Program> log, HttpContext http) =>
                {
                    var email = Emails.IsValid(req.Email) ? Emails.Normalize(req.Email) : null;
                    var user = email is null ? null : await users.GetByEmailAsync(email);

                    // A federated account has no password, so a reset would set one behind the identity
                    // provider's back and create a second way in that the org never approved.
                    if (user is { PasswordHash: not null }
                        && await resets.CountSinceAsync(user.Id, ThrottleWindow, http.RequestAborted)
                            < MaxResetsPerWindow)
                    {
                        var (raw, hash) = SessionTokens.Create();
                        await resets.CreateAsync(
                            user.Id, hash, DateTimeOffset.UtcNow.Add(PasswordResetEmailText.Lifetime),
                            http.RequestAborted);
                        // Not awaited, and NOT on RequestAborted: a constant status is only half of
                        // "this endpoint says nothing". Waiting for SMTP would make a known address
                        // measurably slower than an unknown one, which is the same oracle by a
                        // stopwatch instead of a status code. Detached, and cancelled by nothing, so
                        // the send outlives the response it must not delay.
                        _ = SendResetAsync(mailer, user.Email, raw, log);
                    }
                    else if (user is not null)
                    {
                        log.LogInformation(
                            "Password reset not sent for user {UserId}: federated account or throttled.",
                            user.Id);
                    }

                    return TypedResults.Accepted((string?)null);
                })
            .WithName("forgotPassword").WithTags("Auth");

        // Redeem a reset link. Deliberately does NOT sign the user in: it sends them to sign in with the
        // new password, so an account with a second factor is still challenged for it. Signing them in
        // here would make mailbox access alone enough to bypass MFA entirely.
        app.MapPost("/api/auth/password/reset",
                async Task<Results<NoContent, BadRequest<ErrorResponse>>> (
                    ResetPasswordRequest req, UserRepository users, PasswordResetRepository resets,
                    SessionRepository sessions, HttpContext http) =>
                {
                    if (string.IsNullOrEmpty(req.NewPassword)
                        || req.NewPassword.Length < 8 || req.NewPassword.Length > 200)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_password"));
                    }

                    if (string.IsNullOrEmpty(req.Token)
                        || await resets.RedeemAsync(SessionTokens.HashToken(req.Token), http.RequestAborted)
                            is not { } redeemed)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_token"));
                    }

                    await users.SetPasswordHashAsync(redeemed.UserId, PasswordHasher.Hash(req.NewPassword));
                    // Every session, not all-but-one: whoever is resetting is not signed in here, and the
                    // reason to reset is usually that someone else might be.
                    await sessions.RevokeAllExceptAsync(redeemed.UserId, string.Empty, http.RequestAborted);
                    return TypedResults.NoContent();
                })
            .WithName("resetPassword").WithTags("Auth");

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
                        PlatformAdmin.IsPlatformAdmin(http.User), user?.OnboardedAt is not null, impersonation,
                        WeeklySummaryOptOut: user?.WeeklySummaryOptOut ?? false));
                })
            .WithName("me").WithTags("Auth").RequireAuthorization();

        // A member's own digest preference. On the user rather than the org, because the org endpoint
        // decides whether the digest runs at all and on what schedule, while this decides only whether
        // this one inbox receives it. Any signed-in user may set their own; there is no role to check.
        app.MapPut("/api/auth/me/weekly-summary",
                async Task<NoContent> (
                    UpdateWeeklySummarySubscriptionRequest request, HttpContext http, UserRepository users) =>
                {
                    await users.SetWeeklySummaryOptOutAsync(
                        OrgAuthorization.CurrentUserId(http.User), request.OptOut, http.RequestAborted);
                    return TypedResults.NoContent();
                })
            .WithName("updateWeeklySummarySubscription").WithTags("Auth").RequireAuthorization();
    }

}
