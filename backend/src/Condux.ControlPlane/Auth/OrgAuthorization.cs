using System.Globalization;
using System.Security.Claims;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Claim helpers + endpoint-filter factories that enforce org membership/role. Non-members get 404
/// (existence hidden); an under-privileged member gets 403.
/// </summary>
internal static class OrgAuthorization
{
    /// <summary>The authenticated user's id from the session-derived claims.</summary>
    public static long CurrentUserId(ClaimsPrincipal user) =>
        long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0", CultureInfo.InvariantCulture);

    /// <summary>The authenticated user's email from the session-derived claims.</summary>
    public static string CurrentUserEmail(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

    /// <summary>Filter for org-scoped routes (<c>{orgId}</c>): requires membership at <paramref name="min"/>.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireOrgRole(
        OrgRole min) =>
        async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!TryRouteLong(http, "orgId", out var orgId))
            {
                return TypedResults.NotFound();
            }

            var members = http.RequestServices.GetRequiredService<OrgMemberRepository>();
            var role = await members.GetRoleAsync(orgId, CurrentUserId(http.User));
            return role is null ? (ImpersonatingRead(http, orgId) ? await next(ctx) : TypedResults.NotFound())
                : role < min ? TypedResults.Forbid()
                : await next(ctx);
        };

    /// <summary>Like <see cref="RequireOrgRole"/> but for project-scoped routes — resolves the project's org.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireProjectRole(
        OrgRole min) =>
        async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!TryRouteLong(http, "projectId", out var projectId))
            {
                return TypedResults.NotFound();
            }

            var project = await http.RequestServices.GetRequiredService<ProjectRepository>().GetAsync(projectId);
            if (project is null)
            {
                return TypedResults.NotFound();
            }

            var members = http.RequestServices.GetRequiredService<OrgMemberRepository>();
            var role = await members.GetRoleAsync(project.OrgId, CurrentUserId(http.User));
            return role is null ? (ImpersonatingRead(http, project.OrgId) ? await next(ctx) : TypedResults.NotFound())
                : role < min ? TypedResults.Forbid()
                : await next(ctx);
        };

    /// <summary>Like <see cref="RequireProjectRole"/> but the route carries the project's public UUID
    /// (<c>{projectId:guid}</c>) — the id exposed in dashboard URLs and the project resource API.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>>
        RequireProjectRoleByPublicId(OrgRole min) =>
        async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!TryRouteGuid(http, "projectId", out var publicId))
            {
                return TypedResults.NotFound();
            }

            var project = await http.RequestServices.GetRequiredService<ProjectRepository>()
                .GetByPublicIdAsync(publicId);
            if (project is null)
            {
                return TypedResults.NotFound();
            }

            var members = http.RequestServices.GetRequiredService<OrgMemberRepository>();
            var role = await members.GetRoleAsync(project.OrgId, CurrentUserId(http.User));
            return role is null ? (ImpersonatingRead(http, project.OrgId) ? await next(ctx) : TypedResults.NotFound())
                : role < min ? TypedResults.Forbid()
                : await next(ctx);
        };

    // The read-only impersonation scope grant (ADR-0027): a platform admin who is not a member of this org
    // still gets owner-equivalent READ access to it while a validated view-as session targets THIS org.
    // Safe-method-only and org-scoped as defense in depth — even if the read-only middleware were bypassed
    // no write scope is ever handed out, and impersonating org A never unlocks org B. The middleware has
    // already validated the cookie (feature enabled, caller is admin, token is this admin's) before
    // stashing TargetOrgId, so this only re-checks the method + org match.
    private static bool ImpersonatingRead(HttpContext http, long orgId) =>
        (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        && PlatformAdmin.IsPlatformAdmin(http.User)
        && Impersonation.TargetOrgId(http) == orgId;

    private static bool TryRouteLong(HttpContext http, string key, out long value)
    {
        value = 0;
        return http.Request.RouteValues.TryGetValue(key, out var v)
            && v is string s && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryRouteGuid(HttpContext http, string key, out Guid value)
    {
        value = Guid.Empty;
        return http.Request.RouteValues.TryGetValue(key, out var v)
            && v is string s && Guid.TryParse(s, out value);
    }
}
