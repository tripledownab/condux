using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Invites;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Org invites (#84). An admin+ invites an email to join with a role; the invitee redeems an opaque
/// token (stored only as a hash) once logged in as that email. On create the invitee is emailed an accept
/// link (best-effort via <see cref="InviteMailer"/>, opt-in with SMTP); the raw token is also returned so
/// it can be shared out of band when email delivery is off.
/// </summary>
internal static class InviteEndpoints
{
    public static void MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orgs/{orgId:long}/invites",
                async Task<Results<Created<InviteResponse>, BadRequest<ErrorResponse>, ForbidHttpResult>> (
                    long orgId, CreateInviteRequest req, HttpContext http,
                    OrgMemberRepository members, OrgInviteRepository invites, OrgRepository orgs,
                    InviteMailer mailer, ILogger<CreateInviteRequest> logger) =>
                {
                    if (!Emails.IsValid(req.Email) || !OrgRoles.TryParse(req.Role, out var role))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_invite"));
                    }

                    // The filter guaranteed admin+, so the inviter's role is non-null; don't let them grant
                    // a role above their own (no privilege escalation).
                    var userId = OrgAuthorization.CurrentUserId(http.User);
                    if (role > await members.GetRoleAsync(orgId, userId))
                    {
                        return TypedResults.Forbid();
                    }

                    var (raw, hash) = SessionTokens.Create();
                    var invite = await invites.CreateAsync(
                        orgId, Emails.Normalize(req.Email), role, hash, userId,
                        DateTimeOffset.UtcNow.Add(InviteLifetime.Duration));

                    // Email the invitee an accept link. Best-effort: a delivery failure must not fail the
                    // request (the raw token is still returned for out-of-band sharing).
                    try
                    {
                        var org = await orgs.GetAsync(orgId);
                        await mailer.SendAsync(
                            invite.Email, org?.Name ?? "your organization",
                            OrgAuthorization.CurrentUserEmail(http.User), OrgRoles.Name(invite.Role), raw);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Invite email delivery failed for {Email}.", invite.Email);
                    }

                    return TypedResults.Created($"/api/orgs/{orgId}/invites/{invite.Id}",
                        new InviteResponse(invite.Id, invite.Email, OrgRoles.Name(invite.Role), invite.ExpiresAt, raw));
                })
            .WithName("createInvite").WithTags("Invites")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapGet("/api/orgs/{orgId:long}/invites", async (long orgId, OrgInviteRepository invites) =>
                TypedResults.Ok((await invites.ListPendingByOrgAsync(orgId))
                    .Select(i => new InvitePendingResponse(i.Id, i.Email, OrgRoles.Name(i.Role), i.CreatedAt, i.ExpiresAt))
                    .ToList()))
            .WithName("listInvites").WithTags("Invites")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapDelete("/api/orgs/{orgId:long}/invites/{inviteId:long}",
                async Task<Results<NoContent, NotFound>> (long orgId, long inviteId, OrgInviteRepository invites) =>
                    await invites.RevokeAsync(orgId, inviteId) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithName("revokeInvite").WithTags("Invites")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // Accept an invite as the logged-in user — their email must match the invited one. A user
        // belongs to exactly one org (ADR-0018): a solo personal org (they are its only member) is
        // deleted on the way in; membership in a shared org refuses the invite instead.
        app.MapPost("/api/invites/accept",
                async Task<Results<Ok<OrgMembershipResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, ForbidHttpResult>> (
                    AcceptInviteRequest req, HttpContext http,
                    OrgInviteRepository invites, OrgMemberRepository members, OrgRepository orgs, UserRepository users) =>
                {
                    if (string.IsNullOrEmpty(req.Token)
                        || await invites.GetActiveByTokenHashAsync(SessionTokens.HashToken(req.Token)) is not { } invite)
                    {
                        return TypedResults.NotFound(new ErrorResponse("invite_invalid"));
                    }

                    if (!string.Equals(invite.Email, OrgAuthorization.CurrentUserEmail(http.User), StringComparison.Ordinal))
                    {
                        return TypedResults.Forbid();
                    }

                    var userId = OrgAuthorization.CurrentUserId(http.User);
                    var outcome = await MembershipProvisioning.JoinSingleOrgAsync(
                        members, orgs, userId, invite.OrgId, invite.Role, http.RequestAborted);
                    if (outcome == JoinOutcome.AlreadyMember)
                    {
                        return TypedResults.Conflict(new ErrorResponse("already_member"));
                    }
                    if (outcome == JoinOutcome.BlockedSharedOrg)
                    {
                        return TypedResults.Conflict(new ErrorResponse("already_in_org"));
                    }

                    await invites.MarkAcceptedAsync(invite.Id);
                    // Joining an already-set-up org completes onboarding (they never walk the create-org
                    // /create-project flow), so the dashboard gate opens straight to the app.
                    await users.MarkOnboardedAsync(userId);
                    var org = await orgs.GetAsync(invite.OrgId);
                    return TypedResults.Ok(new OrgMembershipResponse(org!, OrgRoles.Name(invite.Role)));
                })
            .WithName("acceptInvite").WithTags("Invites").RequireAuthorization();
    }
}
