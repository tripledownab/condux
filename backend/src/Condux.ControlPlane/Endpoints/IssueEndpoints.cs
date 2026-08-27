using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.Issues;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Reading issues: the list, its facet counts, the per-user new-issue watermark, and one
/// issue's detail and events. Triage, stats, saved views and the live event stream each have their own
/// file beside this one. Every route requires org membership on the owning project.</summary>
internal static class IssueEndpoints
{
    private const int DefaultIssuesPageSize = 50;
    private const string DefaultSort = "lastSeen";

    public static void MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        // Grouped issues for a project (most-recently-seen first). projectId is the numeric project id
        // (the ClickHouse event store keys on its string form; Postgres issues on the bigint).
        app.MapGet("/api/projects/{projectId:long}/issues",
                async Task<Results<Ok<IssuesPageResponse>, BadRequest<ErrorResponse>>> (
                    long projectId, string? q, string? sort, int? limit, int? offset,
                    HttpContext http, IssueRepository issues) =>
                {
                    if (PageParams.Resolve(limit, offset, DefaultIssuesPageSize) is not { } window)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_issues_page"));
                    }
                    var sortColumn = sort ?? DefaultSort;
                    var (page, hasMore) = await issues.ListPageAsync(
                        projectId, IssueQuery.Parse(q), OrgAuthorization.CurrentUserId(http.User),
                        sortColumn, window.Limit, window.Offset);
                    return TypedResults.Ok(new IssuesPageResponse(page, hasMore));
                })
            .WithName("listIssues").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Facet counts for the current query (status + level), so the views + severity rails don't need
        // the full issue set — part of the server-side subset.
        app.MapGet("/api/projects/{projectId:long}/issues/counts",
                async Task<Ok<IssueCountsResponse>> (
                    long projectId, string? q, HttpContext http, IssueRepository issues) =>
                {
                    var (byStatus, byLevel) = await issues.CountsAsync(
                        projectId, IssueQuery.Parse(q), OrgAuthorization.CurrentUserId(http.User));
                    return TypedResults.Ok(new IssueCountsResponse(byStatus, byLevel));
                })
            .WithName("listIssueCounts").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // The per-user "new issues since you last looked" nav badge count (ADR-0030). Literal sub-paths
        // (new-count, seen) are declared before the {issueId:guid} route; the guid constraint keeps them
        // off it anyway, but ordering makes the intent explicit.
        app.MapGet("/api/projects/{projectId:long}/issues/new-count",
                async Task<Ok<NewIssueCountResponse>> (
                    long projectId, HttpContext http, IssueSeenRepository seen) =>
                    TypedResults.Ok(new NewIssueCountResponse(await seen.CountNewAsync(
                        OrgAuthorization.CurrentUserId(http.User), projectId, http.RequestAborted))))
            .WithName("newIssueCount").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Mark the project's issue list as seen for the current user (clears their badge). Idempotent.
        app.MapPost("/api/projects/{projectId:long}/issues/seen",
                async Task<NoContent> (long projectId, HttpContext http, IssueSeenRepository seen) =>
                {
                    await seen.MarkSeenAsync(
                        OrgAuthorization.CurrentUserId(http.User), projectId, http.RequestAborted);
                    return TypedResults.NoContent();
                })
            .WithName("markIssuesSeen").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // A single grouped issue plus its most-recent raw events (from the ClickHouse event store).
        // Keyed by the public UUID; the internal id it resolves to is what the event store keys on.
        app.MapGet("/api/projects/{projectId:long}/issues/{issueId:guid}",
                async Task<Results<Ok<IssueDetailResponse>, NotFound>> (
                    long projectId, Guid issueId, IssueRepository issues, ClickHouseEventReader events,
                    EventSymbolication symbolication, CancellationToken cancellationToken) =>
                {
                    if (await issues.GetByPublicIdAsync(projectId, issueId, cancellationToken) is not { } found)
                    {
                        return TypedResults.NotFound();
                    }

                    // Only the latest event rides the detail payload now; older events lazy-load via the
                    // events endpoint below, so a busy issue's detail page stays light. Frames are de-minified
                    // at read time when source maps are configured (ADR-0028); a no-op otherwise.
                    var stored = await symbolication.SymbolicateAsync(
                        projectId,
                        await events.RecentByIssueAsync(
                            projectId.ToString(CultureInfo.InvariantCulture), (ulong)found.InternalId, limit: 1),
                        cancellationToken);

                    return TypedResults.Ok(new IssueDetailResponse(found.Summary, stored));
                })
            .WithName("getIssue").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // A page of the issue's sampled events (newest-first, offset-paginated) for the detail's lazy-loaded
        // events table — so older events load on demand instead of all riding the initial detail payload.
        app.MapGet("/api/projects/{projectId:long}/issues/{issueId:guid}/events",
                async Task<Results<Ok<EventsPageResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, int? limit, int? offset,
                    IssueRepository issues, ClickHouseEventReader events, EventSymbolication symbolication,
                    CancellationToken cancellationToken) =>
                {
                    // Paging is explicit: the client always states the window, so a missing or out-of-range
                    // page is a 400, never a silently-defaulted one.
                    if (PageParams.Require(limit, offset) is not { } paging)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_events_page"));
                    }
                    if (await issues.GetByPublicIdAsync(projectId, issueId, cancellationToken) is not { } found)
                    {
                        return TypedResults.NotFound();
                    }
                    // Fetch one extra row so we can report hasMore without a separate count query.
                    var page = await events.PageByIssueAsync(
                        projectId.ToString(CultureInfo.InvariantCulture), (ulong)found.InternalId,
                        paging.Limit + 1, paging.Offset);
                    var hasMore = page.Count > paging.Limit;
                    IReadOnlyList<StoredEvent> window = hasMore ? page.Take(paging.Limit).ToList() : page.ToList();
                    // De-minify at read time when source maps are configured (ADR-0028); a no-op otherwise.
                    window = await symbolication.SymbolicateAsync(projectId, window, cancellationToken);
                    return TypedResults.Ok(new EventsPageResponse(window, hasMore));
                })
            .WithName("listIssueEvents").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
