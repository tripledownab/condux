using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Cancels a fix run that nothing holds, an issue fix or a CVE bump, and hands its allowance back:
/// one still pending, or a runner's whose lease lapsed. A run queued for a customer's runner is concluded
/// only by that runner's report, and the stale-run sweep leaves runner runs alone, so without this a runner
/// that never came, or died holding a lease, would hold its issue or advisory with a 409 for good. A run
/// held by the conductor or by a live lease, or one already ended, is refused with fix_not_cancellable,
/// since cancelling the row could not stop work that is still going.
/// Admin+, like the requests that queue a run.
/// </summary>
internal static class FixWithdrawEndpoints
{
    public static void MapFixWithdrawEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/fixes/{fixId:guid}/cancel",
                async Task<Results<NoContent, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, Guid fixId, HttpContext http, PostgresFixCatalog catalog,
                    PostgresFixStore fixes, ProjectEventNotifier projectEvents, ILoggerFactory loggerFactory) =>
                {
                    var userId = OrgAuthorization.CurrentUserId(http.User);
                    // The catalog read scopes the run to the project, which the route's role filter checked.
                    if (await catalog.GetAsync(projectId, fixId, userId, http.RequestAborted) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    if (!await fixes.TryWithdrawAsync(fixId, DateTimeOffset.UtcNow, http.RequestAborted))
                    {
                        return TypedResults.Conflict(new ErrorResponse("fix_not_cancellable"));
                    }

                    // Committed by now, so neither write after it may fail the request or take its token.
                    var logger = loggerFactory.CreateLogger(nameof(FixWithdrawEndpoints));
                    await FixAudit.TryWriteAsync(
                        fixes, logger, fixId, userId.ToString(CultureInfo.InvariantCulture), "cancelled", new { },
                        CancellationToken.None);
                    await ProjectEventNudge.TrySendAsync(projectEvents, logger, projectId, CancellationToken.None);
                    return TypedResults.NoContent();
                })
            .WithName("cancelFix").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/repos/{repoId:guid}/cve-fixes/{runId:guid}/cancel",
                async Task<Results<NoContent, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, Guid repoId, Guid runId, HttpContext http, RepoLinkRepository repos,
                    PostgresCveFixStore cveFixes, ProjectEventNotifier projectEvents, ILoggerFactory loggerFactory) =>
                {
                    // The run must belong to a repository linked to this project.
                    if (await repos.GetAsync(projectId, repoId, http.RequestAborted) is null
                        || !(await cveFixes.ListByRepoAsync(repoId, http.RequestAborted)).Any(r => r.Id == runId))
                    {
                        return TypedResults.NotFound();
                    }
                    if (!await cveFixes.TryWithdrawAsync(runId, DateTimeOffset.UtcNow, http.RequestAborted))
                    {
                        return TypedResults.Conflict(new ErrorResponse("fix_not_cancellable"));
                    }

                    // A bump writes no audit (ADR-0023): its run row is the whole record.
                    await ProjectEventNudge.TrySendAsync(
                        projectEvents, loggerFactory.CreateLogger(nameof(FixWithdrawEndpoints)), projectId,
                        CancellationToken.None);
                    return TypedResults.NoContent();
                })
            .WithName("cancelCveFix").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));
    }
}
