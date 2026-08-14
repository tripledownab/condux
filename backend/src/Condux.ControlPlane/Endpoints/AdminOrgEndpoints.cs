using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Admin org detail + edit + cross-tenant member management (ADR-0027). Cross-tenant, behind the
/// platform-admin gate. Editing covers the org name + AI-fix settings only; tier is deliberately not
/// editable here (it is Stripe/webhook owned, see <see cref="AdminBillingEndpoints"/>). Member mutations
/// re-implement the last-owner guard from <see cref="MemberEndpoints"/> because admins act cross-tenant,
/// outside the org-scoped role filter. Every mutation writes an audit entry.
/// </summary>
internal static class AdminOrgEndpoints
{
    public static void MapAdminOrgEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/orgs/{orgId:long}",
                async Task<Results<Ok<AdminOrgDetailResponse>, NotFound>> (
                    long orgId, HttpContext http, AdminRepository admin, OrgRepository orgs,
                    OrgMemberRepository members, StripeConfig stripe) =>
                {
                    var ct = http.RequestAborted;
                    if (await admin.GetOrgAsync(orgId, ct) is not { } row
                        || await orgs.GetAsync(orgId, ct) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }

                    var memberList = (await members.ListByOrgAsync(orgId))
                        .Select(m => new OrgMemberResponse(m.UserId, m.Email, OrgRoles.Name(m.Role), m.CreatedAt))
                        .ToList();
                    return TypedResults.Ok(new AdminOrgDetailResponse(
                        row.Id, row.Slug, row.Name, row.Tier, row.CreatedAt, row.OwnerEmail,
                        row.MemberCount, row.ProjectCount, org.AiFixMode, org.AiFixCostCapUsd,
                        AdminBillingEndpoints.BuildStatus(org, stripe), memberList));
                })
            .WithName("adminOrgDetail").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapPatch("/api/admin/orgs/{orgId:long}",
                async Task<Results<Ok<Org>, NotFound, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long orgId, AdminUpdateOrgRequest req, HttpContext http,
                    OrgRepository orgs, AdminAuditRepository audit) =>
                {
                    var ct = http.RequestAborted;
                    if (string.IsNullOrWhiteSpace(req.Name))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_name"));
                    }
                    if (!Enum.IsDefined((AiFixMode)req.AiFixMode))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_ai_fix_mode"));
                    }
                    if (req.AiFixCostCapUsd is < 0)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_cost_cap"));
                    }
                    if (await orgs.GetAsync(orgId, ct) is not { } current)
                    {
                        return TypedResults.NotFound();
                    }
                    // Same guard as the tenant edit: auto-fix on a tier that does not allow it would never
                    // fire. Gated on AutoFix, since a tier can include runs while still requiring a human.
                    if ((AiFixMode)req.AiFixMode == AiFixMode.Auto && !PlanCatalog.For((Tier)current.Tier).AutoFix)
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fixes_requires_upgrade"));
                    }

                    var name = req.Name.Trim();
                    if (name != current.Name)
                    {
                        await orgs.RenameAsync(orgId, name, ct);
                        await AdminAudit.WriteAsync(http, audit, "org.rename", orgId,
                            details: new { before = current.Name, after = name });
                    }
                    if (req.AiFixMode != current.AiFixMode || req.AiFixCostCapUsd != current.AiFixCostCapUsd)
                    {
                        await AdminAudit.WriteAsync(http, audit, "org.settings", orgId, details: new
                        {
                            before = new { mode = current.AiFixMode, capUsd = current.AiFixCostCapUsd },
                            after = new { mode = req.AiFixMode, capUsd = req.AiFixCostCapUsd },
                        });
                    }

                    // Where the org's fixes execute is carried through unchanged. This endpoint edits the
                    // AI-fix settings, and passing anything else here would silently move a self-hosting
                    // org's work back onto our compute as a side effect of an unrelated admin edit.
                    return await orgs.UpdateSettingsAsync(
                            orgId, req.AiFixMode, req.AiFixCostCapUsd, current.FixExecution, ct) is { } org
                        ? TypedResults.Ok(org)
                        : TypedResults.NotFound();
                })
            .WithName("adminUpdateOrg").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapGet("/api/admin/orgs/{orgId:long}/members",
                async (long orgId, OrgMemberRepository members) =>
                    TypedResults.Ok((await members.ListByOrgAsync(orgId))
                        .Select(m => new OrgMemberResponse(m.UserId, m.Email, OrgRoles.Name(m.Role), m.CreatedAt))
                        .ToList()))
            .WithName("adminListOrgMembers").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapPatch("/api/admin/orgs/{orgId:long}/members/{userId:long}",
                async Task<Results<NoContent, NotFound<ErrorResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long orgId, long userId, UpdateMemberRoleRequest req, HttpContext http,
                    OrgMemberRepository members, AdminAuditRepository audit) =>
                {
                    if (!OrgRoles.TryParse(req.Role, out var role))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_role"));
                    }
                    if (await members.GetRoleAsync(orgId, userId) is not { } current)
                    {
                        return TypedResults.NotFound(new ErrorResponse("member_not_found"));
                    }
                    if (current == OrgRole.Owner && role != OrgRole.Owner && await members.CountOwnersAsync(orgId) <= 1)
                    {
                        return TypedResults.Conflict(new ErrorResponse("last_owner"));
                    }

                    await members.UpdateRoleAsync(orgId, userId, role);
                    await AdminAudit.WriteAsync(http, audit, "member.role", orgId, userId,
                        new { from = OrgRoles.Name(current), to = OrgRoles.Name(role) });
                    return TypedResults.NoContent();
                })
            .WithName("adminUpdateMemberRole").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapDelete("/api/admin/orgs/{orgId:long}/members/{userId:long}",
                async Task<Results<NoContent, NotFound<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long orgId, long userId, HttpContext http,
                    OrgMemberRepository members, AdminAuditRepository audit) =>
                {
                    if (await members.GetRoleAsync(orgId, userId) is not { } current)
                    {
                        return TypedResults.NotFound(new ErrorResponse("member_not_found"));
                    }
                    if (current == OrgRole.Owner && await members.CountOwnersAsync(orgId) <= 1)
                    {
                        return TypedResults.Conflict(new ErrorResponse("last_owner"));
                    }

                    await members.RemoveAsync(orgId, userId);
                    await AdminAudit.WriteAsync(http, audit, "member.remove", orgId, userId,
                        new { role = OrgRoles.Name(current) });
                    return TypedResults.NoContent();
                })
            .WithName("adminRemoveMember").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());
    }
}
