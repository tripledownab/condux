using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The project-wide Fixes section (#118): list every fix run for a project (joined with its issue),
/// read one with its audit trail, mark it viewed (the needs-attention badge), and archive/restore it.
/// All member+ — reading and triaging fixes is day-to-day work, like issue status. Tenancy is enforced
/// by the project-role filter plus a project_id scope on every query.
/// </summary>
internal static class FixCatalogEndpoints
{
    public static void MapFixCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // Active fixes by default; ?archived=true lists the archived ones.
        app.MapGet("/api/projects/{projectId:long}/fixes",
                async Task<Ok<IReadOnlyList<FixListItem>>> (
                    long projectId, bool? archived, HttpContext http, PostgresFixCatalog catalog) =>
                    TypedResults.Ok(await catalog.ListAsync(
                        projectId, OrgAuthorization.CurrentUserId(http.User),
                        active: archived != true, http.RequestAborted)))
            .WithName("listProjectFixes").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // The needs-attention badge count. Declared before {fixId:guid} — the guid constraint keeps
        // "unviewed-count" from matching that route anyway, but ordering makes the intent explicit.
        app.MapGet("/api/projects/{projectId:long}/fixes/unviewed-count",
                async Task<Ok<UnviewedFixCountResponse>> (
                    long projectId, HttpContext http, PostgresFixCatalog catalog) =>
                    TypedResults.Ok(new UnviewedFixCountResponse(await catalog.CountNeedsAttentionAsync(
                        projectId, OrgAuthorization.CurrentUserId(http.User), http.RequestAborted))))
            .WithName("unviewedFixCount").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Conductor spend for the project (#120): per-model token totals + derived USD cost over a
        // window (default 30 days, clamped 1..365). Declared before {fixId:guid} for intent; the guid
        // constraint would keep "cost" off that route anyway.
        app.MapGet("/api/projects/{projectId:long}/fixes/cost",
                async Task<Ok<FixCostRollup>> (
                    long projectId, int? days, HttpContext http, PostgresFixCatalog catalog) =>
                {
                    var window = Math.Clamp(days ?? 30, 1, 365);
                    var since = DateTimeOffset.UtcNow.AddDays(-window);
                    return TypedResults.Ok(await catalog.CostRollupAsync(projectId, since, http.RequestAborted));
                })
            .WithName("fixCostRollup").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapGet("/api/projects/{projectId:long}/fixes/{fixId:guid}",
                async Task<Results<Ok<FixDetail>, NotFound>> (
                    long projectId, Guid fixId, HttpContext http, PostgresFixCatalog catalog) =>
                    await catalog.GetAsync(
                        projectId, fixId, OrgAuthorization.CurrentUserId(http.User), http.RequestAborted)
                        is { } detail
                        ? TypedResults.Ok(detail)
                        : TypedResults.NotFound())
            .WithName("getFix").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Mark viewed by the current user — idempotent, tenancy-guarded, fire-and-forget from the detail.
        app.MapPost("/api/projects/{projectId:long}/fixes/{fixId:guid}/view",
                async Task<NoContent> (
                    long projectId, Guid fixId, HttpContext http, PostgresFixCatalog catalog) =>
                {
                    await catalog.MarkViewedAsync(
                        projectId, fixId, OrgAuthorization.CurrentUserId(http.User), http.RequestAborted);
                    return TypedResults.NoContent();
                })
            .WithName("markFixViewed").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Archive or restore (soft — the audit artifact is never destroyed).
        app.MapPatch("/api/projects/{projectId:long}/fixes/{fixId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid fixId, ArchiveFixRequest request, PostgresFixCatalog catalog,
                    HttpContext http) =>
                    await catalog.SetArchivedAsync(projectId, fixId, request.Archived, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("archiveFix").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
