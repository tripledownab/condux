using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.Realtime;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Alerting;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.Issues;
using Condux.Core.Stats;
using Condux.Notifications;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Issue list + detail. Require org membership on the owning project.</summary>
internal static class IssueEndpoints
{
    private const int DefaultIssuesPageSize = 50;
    private const string DefaultSort = "lastSeen";

    private static readonly HashSet<string> SavedViewSorts =
        ["lastSeen", "firstSeen", "events", "severity"];

    // Bounded free-form fields; the sort must be one the dashboard can apply.
    private static ErrorResponse? ValidateSavedView(SavedViewRequest request) =>
        string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80
            ? new ErrorResponse("invalid_view_name")
            : request.Query.Length > 500
                ? new ErrorResponse("invalid_view_query")
                : !SavedViewSorts.Contains(request.Sort)
                    ? new ErrorResponse("invalid_view_sort")
                    : null;

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

        // Live per-project Server-Sent Events stream (ADR-0030): a generic "this project changed" channel
        // the dashboard's nav badges listen on to refetch their counts the instant a new/regressed issue
        // lands (fed by the consumer's NOTIFY over Postgres LISTEN/NOTIFY). Not part of the REST contract,
        // so it is excluded from the OpenAPI doc + TS client; the browser's EventSource speaks it directly.
        // A ~20s heartbeat comment keeps proxies from closing an idle connection.
        app.MapGet("/api/projects/{projectId:long}/events",
                async (long projectId, HttpContext http, ProjectEventHub hub) =>
                {
                    var ct = http.RequestAborted;
                    http.Response.Headers.ContentType = "text/event-stream";
                    http.Response.Headers.CacheControl = "no-cache";
                    http.Response.Headers["X-Accel-Buffering"] = "no";
                    using var subscription = hub.Subscribe(projectId);
                    await http.Response.WriteAsync(": connected\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            beat.CancelAfter(TimeSpan.FromSeconds(20));
                            try
                            {
                                await subscription.Reader.WaitToReadAsync(beat.Token);
                                while (subscription.Reader.TryRead(out _)) { }
                                // An unnamed event, so the browser's EventSource.onmessage fires. The
                                // payload is a generic "project changed, refetch" nudge, not data.
                                await http.Response.WriteAsync("data: 1\n\n", ct);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                await http.Response.WriteAsync(": ping\n\n", ct); // heartbeat tick
                            }
                            await http.Response.Body.FlushAsync(ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The client disconnected (or the server is shutting down); nothing to do.
                    }
                })
            .WithName("projectEvents").ExcludeFromDescription()
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

        // Triage: set the issue's status (1 unresolved, 2 resolved, 3 ignored). Member+ — triage is
        // day-to-day operations, not configuration, so every member of the org can do it.
        app.MapPatch("/api/projects/{projectId:long}/issues/{issueId:guid}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, UpdateIssueStatusRequest request,
                    IssueRepository issues, AlertDispatcher alerts, ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    if (request.Status is not (1 or 2 or 3))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_status"));
                    }
                    if (!await issues.UpdateStatusAsync(projectId, issueId, request.Status))
                    {
                        return TypedResults.NotFound();
                    }
                    // A manual resolve (status 2) fires the project's Resolved alert rules, best-effort.
                    if (request.Status == 2)
                    {
                        await FireIssueAlertAsync(
                            alerts, issues, loggerFactory, projectId, issueId, AlertEventType.Resolved,
                            cancellationToken);
                    }
                    return TypedResults.NoContent();
                })
            .WithName("updateIssueStatus").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Triage: assign the issue to an org member (null unassigns). Member+, like status — triage
        // is day-to-day operations. The assignee must belong to the project's org.
        app.MapPatch("/api/projects/{projectId:long}/issues/{issueId:guid}/assignee",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, AssignIssueRequest request,
                    IssueRepository issues, ProjectRepository projects, OrgMemberRepository members,
                    AlertDispatcher alerts, ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    if (request.UserId is { } userId)
                    {
                        var record = await projects.GetAsync(projectId);
                        if (record is null || await members.GetRoleAsync(record.OrgId, userId) is null)
                        {
                            return TypedResults.BadRequest(new ErrorResponse("assignee_not_a_member"));
                        }
                    }
                    if (!await issues.UpdateAssigneeAsync(projectId, issueId, request.UserId))
                    {
                        return TypedResults.NotFound();
                    }
                    // Assigning to a user (not unassigning) fires the project's Assigned alert rules, best-effort.
                    if (request.UserId is not null)
                    {
                        await FireIssueAlertAsync(
                            alerts, issues, loggerFactory, projectId, issueId, AlertEventType.Assigned,
                            cancellationToken);
                    }
                    return TypedResults.NoContent();
                })
            .WithName("assignIssue").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Saved views (#108): the caller's named search+ordering presets for this project. All
        // routes are scoped to the current user; a view id outside their set reads as not-found.
        app.MapGet("/api/projects/{projectId:long}/views",
                async (long projectId, HttpContext http, SavedViewRepository views) =>
                    TypedResults.Ok(await views.ListAsync(
                        OrgAuthorization.CurrentUserId(http.User), projectId)))
            .WithName("listSavedViews").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPost("/api/projects/{projectId:long}/views",
                async Task<Results<Ok<SavedView>, BadRequest<ErrorResponse>>> (
                    long projectId, SavedViewRequest request, HttpContext http,
                    SavedViewRepository views) =>
                    ValidateSavedView(request) is { } error
                        ? TypedResults.BadRequest(error)
                        : TypedResults.Ok(await views.UpsertAsync(
                            OrgAuthorization.CurrentUserId(http.User), projectId,
                            request.Name.Trim(), request.Query.Trim(), request.Sort)))
            .WithName("createSavedView").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPatch("/api/projects/{projectId:long}/views/{viewId:long}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, long viewId, SavedViewRequest request, HttpContext http,
                    SavedViewRepository views) =>
                    ValidateSavedView(request) is { } error
                        ? TypedResults.BadRequest(error)
                        : await views.UpdateAsync(
                            viewId, OrgAuthorization.CurrentUserId(http.User), projectId,
                            request.Name.Trim(), request.Query.Trim(), request.Sort)
                            ? TypedResults.NoContent()
                            : TypedResults.NotFound())
            .WithName("updateSavedView").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapDelete("/api/projects/{projectId:long}/views/{viewId:long}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, long viewId, HttpContext http, SavedViewRepository views) =>
                    await views.DeleteAsync(viewId, OrgAuthorization.CurrentUserId(http.User), projectId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteSavedView").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // The issue's exact event histogram (#102): zero-filled buckets over a window, from the
        // issue_stats_1h rollup the consumer counts EVERY event into (raw events are sampled; the rollup
        // is not). Hourly buckets up to 7 days, daily for 30, aligned ending at the current bucket.
        app.MapGet("/api/projects/{projectId:long}/issues/{issueId:guid}/stats",
                async Task<Results<Ok<IssueStatsResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, int? hours,
                    IssueRepository issues, ClickHouseIssueStatsReader stats) =>
                {
                    var windowHours = hours ?? 24;
                    if (windowHours is not (24 or 168 or 720))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_stats_window"));
                    }

                    if (await issues.GetByPublicIdAsync(projectId, issueId) is not { } found)
                    {
                        return TypedResults.NotFound();
                    }

                    var bucketSeconds = IssueHistogram.BucketSeconds(windowHours);
                    var sparse = await stats.BucketsAsync(
                        projectId.ToString(CultureInfo.InvariantCulture), (ulong)found.InternalId,
                        windowHours, daily: bucketSeconds > 3_600);
                    return TypedResults.Ok(new IssueStatsResponse(
                        IssueHistogram.Build(sparse, DateTimeOffset.UtcNow, windowHours), bucketSeconds));
                })
            .WithName("getIssueStats").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Batch sparklines for the issue list (#103): every issue's last-24h hourly series in ONE
        // ClickHouse query + one Postgres id-map read, so the list never fans out a request per row.
        // Issues with no events in the window simply have no entry (the client zero-fills nothing).
        app.MapGet("/api/projects/{projectId:long}/issues/stats",
                async (long projectId, IssueRepository issues, ClickHouseIssueStatsReader stats) =>
                {
                    const int windowHours = 24;
                    var publicIds = await issues.PublicIdsByProjectAsync(projectId);
                    var byIssue = await stats.BucketsByIssueAsync(
                        projectId.ToString(CultureInfo.InvariantCulture), windowHours);

                    var now = DateTimeOffset.UtcNow;
                    var sparklines = byIssue
                        .Where(pair => publicIds.ContainsKey(pair.Key))
                        .Select(pair => new IssueSparkline(
                            publicIds[pair.Key], IssueHistogram.Build(pair.Value, now, windowHours)))
                        .ToList();
                    return TypedResults.Ok(new IssueSparklinesResponse(
                        sparklines, IssueHistogram.BucketSeconds(windowHours)));
                })
            .WithName("listIssueSparklines").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    // Best-effort: load the issue summary and fire the project's alert rules for a triage event
    // (Resolved/Assigned). Guarded so a load or delivery failure never fails the triage request that
    // already committed; the dispatcher additionally swallows its own per-channel delivery errors.
    private static async Task FireIssueAlertAsync(
        AlertDispatcher alerts, IssueRepository issues, ILoggerFactory loggerFactory,
        long projectId, Guid issueId, AlertEventType eventType, CancellationToken cancellationToken)
    {
        try
        {
            if (await issues.GetByPublicIdAsync(projectId, issueId, cancellationToken) is not { } found)
            {
                return;
            }
            var summary = found.Summary;
            var notification = new AlertNotification(
                projectId, summary.Id, summary.Title, summary.Culprit, (Level)summary.Level, eventType);
            await alerts.DispatchAsync(projectId, eventType, notification, cancellationToken);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Condux.ControlPlane.IssueAlerts").LogWarning(
                ex, "issue alert dispatch failed project={ProjectId} event={Event}", projectId, eventType);
        }
    }
}
