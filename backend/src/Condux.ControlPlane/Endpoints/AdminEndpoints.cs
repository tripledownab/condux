using Condux.ControlPlane.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The platform-operator console (source-available "super admin"). This file holds the platform-wide
/// reads (totals, every org, every user) plus the audit log; org edit/members live in
/// <see cref="AdminOrgEndpoints"/>, billing in <see cref="AdminBillingEndpoints"/>, spend in
/// <see cref="AdminSpendEndpoints"/>, and impersonation in <see cref="ImpersonationEndpoints"/>. Every
/// route is gated by <see cref="PlatformAdmin"/>, so a non-admin gets 404 and the surface stays hidden.
/// Membership is the CONDUX_PLATFORM_ADMIN_EMAILS allowlist. Mutations are audited (ADR-0027).
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/overview", async (AdminRepository admin) =>
            {
                var stats = await admin.GetStatsAsync();
                return TypedResults.Ok(
                    new PlatformStatsResponse(stats.Orgs, stats.Users, stats.Projects, stats.Issues));
            })
            .WithName("adminOverview").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapGet("/api/admin/orgs", async (AdminRepository admin) =>
                TypedResults.Ok((await admin.ListOrgsAsync())
                    .Select(o => new AdminOrgResponse(
                        o.Id, o.Slug, o.Name, o.Tier, o.CreatedAt, o.OwnerEmail, o.MemberCount, o.ProjectCount))
                    .ToList()))
            .WithName("adminListOrgs").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapGet("/api/admin/users", async (AdminRepository admin) =>
                TypedResults.Ok((await admin.ListUsersAsync())
                    .Select(u => new AdminUserResponse(u.Id, u.Email, u.CreatedAt, u.OrgCount, u.OrgId, u.OrgName))
                    .ToList()))
            .WithName("adminListUsers").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        // The admin audit trail (ADR-0027), newest first, optionally filtered to one org.
        app.MapGet("/api/admin/audit",
                async (AdminAuditRepository audit, long? orgId = null, int limit = 200) =>
                    TypedResults.Ok((await audit.ListAsync(limit, orgId))
                        .Select(a => new AdminAuditResponse(
                            a.Id, a.ActorId, a.ActorEmail, a.Action, a.TargetOrgId, a.TargetUserId,
                            a.Details, a.CreatedAt))
                        .ToList()))
            .WithName("adminAuditLog").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());
    }
}
