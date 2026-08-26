using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// TOTP second factor for a user's own account (ADR-0039).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every mutating route re-authenticates with the password</b>, not merely a valid session. Without
/// that, a stolen session cookie for an account that has no MFA yet is enough to enrol an attacker's own
/// authenticator; confirming it then revokes every other session, so session theft becomes full takeover
/// with the real owner locked out. A session proves "this browser signed in at some point", which is
/// exactly the thing under suspicion when the second factor is being changed.
/// </para>
/// <para>
/// <c>verify</c> is the sole exception and the sole route that accepts a half-authenticated session,
/// via <see cref="SessionRepository.GetPendingUserAsync"/>. It cannot require a session in the ordinary
/// sense, because a pending session deliberately resolves to nobody.
/// </para>
/// </remarks>
internal static class MfaEndpoints
{
    public static void MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/mfa",
                async Task<Ok<MfaStatusResponse>> (HttpContext http, MultiFactor mfa) =>
                {
                    var userId = OrgAuthorization.CurrentUserId(http.User);
                    var enabled = await mfa.IsEnabledAsync(userId, http.RequestAborted);
                    return TypedResults.Ok(new MfaStatusResponse(
                        enabled, mfa.Available,
                        enabled ? await mfa.RemainingRecoveryCodesAsync(userId, http.RequestAborted) : 0));
                })
            .WithName("mfaStatus").WithTags("Auth").RequireAuthorization();

        app.MapPost("/api/auth/mfa/enroll",
                async Task<Results<Ok<MfaEnrolmentResponse>, UnauthorizedHttpResult, Conflict<ErrorResponse>,
                    NotFound<ErrorResponse>>> (
                    PasswordConfirmation req, HttpContext http, MultiFactor mfa, UserRepository users) =>
                {
                    if (!mfa.Available)
                    {
                        // A 404 here would read as "this product has no MFA". Name the variable instead.
                        return TypedResults.NotFound(new ErrorResponse("secret_key_not_configured"));
                    }

                    if (await Reauthenticate(http, users, req.Password) is not { } user)
                    {
                        return TypedResults.Unauthorized();
                    }

                    return await mfa.BeginEnrolmentAsync(user.Id, user.Email, http.RequestAborted) is { } started
                        ? TypedResults.Ok(new MfaEnrolmentResponse(started.Secret, started.Uri))
                        : TypedResults.Conflict(new ErrorResponse("mfa_already_enabled"));
                })
            .WithName("enrollMfa").WithTags("Auth").RequireAuthorization();

        app.MapPost("/api/auth/mfa/confirm",
                async Task<Results<Ok<RecoveryCodesResponse>, UnauthorizedHttpResult, BadRequest<ErrorResponse>>> (
                    MfaConfirmation req, HttpContext http, MultiFactor mfa,
                    UserRepository users, SessionRepository sessions) =>
                {
                    if (await Reauthenticate(http, users, req.Password) is not { } user)
                    {
                        return TypedResults.Unauthorized();
                    }

                    if (await mfa.ConfirmAsync(user.Id, req.Code, http.RequestAborted) is not { } codes)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_code"));
                    }

                    // Sessions opened before a second factor existed must not outlive it.
                    await RevokeOtherSessions(http, sessions, user.Id);
                    return TypedResults.Ok(new RecoveryCodesResponse(codes));
                })
            .WithName("confirmMfa").WithTags("Auth").RequireAuthorization();

        app.MapPost("/api/auth/mfa/recovery-codes",
                async Task<Results<Ok<RecoveryCodesResponse>, UnauthorizedHttpResult, BadRequest<ErrorResponse>>> (
                    PasswordConfirmation req, HttpContext http, MultiFactor mfa, UserRepository users) =>
                {
                    if (await Reauthenticate(http, users, req.Password) is not { } user)
                    {
                        return TypedResults.Unauthorized();
                    }

                    return await mfa.RegenerateRecoveryCodesAsync(user.Id, http.RequestAborted) is { } codes
                        ? TypedResults.Ok(new RecoveryCodesResponse(codes))
                        : TypedResults.BadRequest(new ErrorResponse("mfa_not_enabled"));
                })
            .WithName("regenerateRecoveryCodes").WithTags("Auth").RequireAuthorization();

        app.MapPost("/api/auth/mfa/disable",
                async Task<Results<NoContent, UnauthorizedHttpResult>> (
                    PasswordConfirmation req, HttpContext http, MultiFactor mfa,
                    UserRepository users, SessionRepository sessions) =>
                {
                    if (await Reauthenticate(http, users, req.Password) is not { } user)
                    {
                        return TypedResults.Unauthorized();
                    }

                    await mfa.DisableAsync(user.Id, http.RequestAborted);
                    await RevokeOtherSessions(http, sessions, user.Id);
                    return TypedResults.NoContent();
                })
            .WithName("disableMfa").WithTags("Auth").RequireAuthorization();

        // The one route that acts on a half-authenticated session, so it cannot use RequireAuthorization:
        // a pending session resolves to nobody by design.
        app.MapPost("/api/auth/mfa/verify",
                async Task<Results<Ok<AuthUserResponse>, UnauthorizedHttpResult, BadRequest<ErrorResponse>>> (
                    MfaChallenge req, HttpContext http, MultiFactor mfa,
                    SessionRepository sessions, EmailAllowlist platformAdmins) =>
                {
                    if (http.Request.Cookies[SessionAuth.Cookie] is not { Length: > 0 } raw)
                    {
                        return TypedResults.Unauthorized();
                    }

                    var hash = SessionTokens.HashToken(raw);
                    if (await sessions.GetPendingUserAsync(hash, http.RequestAborted) is not { } user)
                    {
                        return TypedResults.Unauthorized();
                    }

                    switch (await mfa.VerifyAsync(user.Id, hash, req.Code, http.RequestAborted))
                    {
                        case MfaVerdict.Accepted:
                            await sessions.CompleteMfaAsync(
                                hash, DateTimeOffset.UtcNow.Add(SessionAuth.Lifetime), http.RequestAborted);
                            return TypedResults.Ok(new AuthUserResponse(
                                user.Id, user.Email, platformAdmins.Contains(user.Email),
                                Onboarded: user.OnboardedAt is not null));

                        case MfaVerdict.Exhausted:
                            // End the pending session, so another attempt costs the password again.
                            await sessions.RevokeAsync(hash, http.RequestAborted);
                            http.Response.Cookies.Delete(SessionAuth.Cookie);
                            return TypedResults.BadRequest(new ErrorResponse("too_many_attempts"));

                        case MfaVerdict.CooledDown:
                            return TypedResults.BadRequest(new ErrorResponse("too_many_attempts"));

                        default:
                            // Deliberately identical for a wrong code and an already-used one. Saying
                            // which confirms to an attacker that the value they tried was real.
                            return TypedResults.BadRequest(new ErrorResponse("invalid_code"));
                    }
                })
            .WithName("verifyMfa").WithTags("Auth");
    }

    /// <summary>The caller, only if they can produce their password again. Null otherwise.</summary>
    private static async Task<User?> Reauthenticate(HttpContext http, UserRepository users, string? password)
    {
        var user = await users.GetByIdAsync(OrgAuthorization.CurrentUserId(http.User), http.RequestAborted);
        return user?.PasswordHash is not null && PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash)
            ? user
            : null;
    }

    private static async Task RevokeOtherSessions(HttpContext http, SessionRepository sessions, long userId)
    {
        var keep = http.Request.Cookies[SessionAuth.Cookie] is { Length: > 0 } raw
            ? SessionTokens.HashToken(raw)
            : string.Empty;
        await sessions.RevokeAllExceptAsync(userId, keep, http.RequestAborted);
    }
}
