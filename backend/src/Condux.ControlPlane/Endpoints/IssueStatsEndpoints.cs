using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.Core.Auth;
using Condux.Core.Stats;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Issue event histograms (#102, #103): the detail chart and the list sparklines. Both read the
/// issue_stats_1h rollup rather than the raw events, because the consumer counts EVERY event into the
/// rollup while raw events are sampled, so only the rollup gives an exact series.</summary>
internal static class IssueStatsEndpoints
{
    public static void MapIssueStatsEndpoints(this IEndpointRouteBuilder app)
    {
        // Zero-filled buckets over a window: hourly up to 7 days, daily for 30, aligned ending at the
        // current bucket.
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
}
