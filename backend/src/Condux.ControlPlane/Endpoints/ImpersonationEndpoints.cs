using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Start and stop a read-only "view as org" impersonation session (ADR-0027). Both are behind the
/// platform-admin gate. Opt-in via <see cref="ImpersonationConfig"/>: when the signing key is unset the
/// routes 404 (the whole capability is off). Start mints the signed <c>condux_impersonation</c> cookie
/// (bound to this admin + the target org, ~30 min) and audits it; stop clears the cookie and audits it.
/// The read-only enforcement + scope grant live in <see cref="Impersonation"/> / <see cref="OrgAuthorization"/>.
/// </summary>
internal static class ImpersonationEndpoints
{
    public static void MapImpersonationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/impersonation/{orgId:long}",
                async Task<Results<Ok<ImpersonationStateResponse>, NotFound>> (
                    long orgId, HttpContext http, ImpersonationConfig config,
                    OrgRepository orgs, AdminAuditRepository audit) =>
                {
                    if (!config.Enabled || await orgs.GetAsync(orgId, http.RequestAborted) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }

                    var expiresAt = DateTimeOffset.UtcNow + Impersonation.Lifetime;
                    var token = ImpersonationToken.Create(
                        OrgAuthorization.CurrentUserId(http.User), org.Id, expiresAt, config.SigningKey!);
                    Impersonation.SetCookie(http, token, expiresAt);
                    await AdminAudit.WriteAsync(http, audit, "impersonation.start", org.Id,
                        details: new { orgSlug = org.Slug, expiresAt });

                    return TypedResults.Ok(new ImpersonationStateResponse(org.Id, org.Slug, org.Name));
                })
            .WithName("adminImpersonateOrg").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        // Allowlisted in the impersonation middleware so it works mid-session (a POST would otherwise be
        // blocked read-only). Ends the session even when the cookie is already stale.
        app.MapPost("/api/admin/impersonation/stop",
                async Task<NoContent> (HttpContext http, AdminAuditRepository audit) =>
                {
                    var target = Impersonation.TargetOrgId(http);
                    Impersonation.ClearCookie(http);
                    if (target is { } orgId)
                    {
                        await AdminAudit.WriteAsync(http, audit, "impersonation.stop", orgId);
                    }

                    return TypedResults.NoContent();
                })
            .WithName("adminStopImpersonation").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());
    }
}
