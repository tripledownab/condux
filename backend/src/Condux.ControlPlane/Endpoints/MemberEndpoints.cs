using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Org member listing + role management (#48). Mutations are owner-only.</summary>
internal static class MemberEndpoints
{
    public static void MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orgs/{orgId:long}/members", async (long orgId, OrgMemberRepository members) =>
                TypedResults.Ok((await members.ListByOrgAsync(orgId))
                    .Select(m => new OrgMemberResponse(m.UserId, m.Email, OrgRoles.Name(m.Role), m.CreatedAt))
                    .ToList()))
            .WithName("listMembers").WithTags("Members")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapPatch("/api/orgs/{orgId:long}/members/{userId:long}",
                async Task<Results<NoContent, NotFound<ErrorResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long orgId, long userId, UpdateMemberRoleRequest req, OrgMemberRepository members) =>
                {
                    if (!OrgRoles.TryParse(req.Role, out var role))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_role"));
                    }

                    if (await members.GetRoleAsync(orgId, userId) is not { } current)
                    {
                        return TypedResults.NotFound(new ErrorResponse("member_not_found"));
                    }

                    // Don't let an org lose its last owner.
                    if (current == OrgRole.Owner && role != OrgRole.Owner && await members.CountOwnersAsync(orgId) <= 1)
                    {
                        return TypedResults.Conflict(new ErrorResponse("last_owner"));
                    }

                    await members.UpdateRoleAsync(orgId, userId, role);
                    return TypedResults.NoContent();
                })
            .WithName("updateMemberRole").WithTags("Members")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Owner));

        app.MapDelete("/api/orgs/{orgId:long}/members/{userId:long}",
                async Task<Results<NoContent, NotFound<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long orgId, long userId, OrgMemberRepository members) =>
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
                    return TypedResults.NoContent();
                })
            .WithName("removeMember").WithTags("Members")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Owner));
    }
}
