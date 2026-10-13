using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.Core.Repos;
using Condux.Core.SourceControl;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Condux.Storage.SourceMaps;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The Conductor's request surface (#60/#89): trigger a fix for an issue and read its suggestions.
/// Triggering resolves the issue (public UUID → internal id) and the project's linked repo, then
/// publishes a job to the topic the Conductor worker drains — the run itself is async and always ends
/// in a human-reviewed draft PR (never auto-applied). Tenancy enforced: trigger needs admin, reads member.
/// </summary>
internal static class FixEndpoints
{

    public static void MapFixEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/issues/{issueId:guid}/fix",
                async Task<Results<Accepted, NotFound, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>>> (
                    long projectId, Guid issueId, RequestFixRequest req, HttpContext http,
                    IssueRepository issues, ProjectRepository projects, OrgRepository orgs,
                    RepoLinkRepository repos, ReleaseRepository releases,
                    GithubInstallationRepository installations,
                    ClickHouseEventReader events, IAiFixQuota quota, IAiFixSpend spend,
                    IFixRequestPublisher publisher, PostgresJobLeaseStore leases, IFixStore fixes,
                    ProjectEventNotifier projectEvents, EventSymbolication symbolication,
                    ILoggerFactory loggerFactory) =>
                {
                    if (await issues.GetByPublicIdAsync(projectId, issueId) is not { } issue)
                    {
                        return TypedResults.NotFound();
                    }

                    // Resolve the project's org so the allowance below is reserved against the right one.
                    var record = await projects.GetAsync(projectId);
                    var org = record is null ? null : await orgs.GetAsync(record.OrgId);
                    // Every tier includes Conductor runs (ADR-0035), so there is no plan gate here any more.
                    // An unresolvable org is a data-integrity case, not an upgrade prompt: the role filter
                    // already 404s a project the caller cannot see, so reaching here with no org means the
                    // project's org row is missing.
                    if (org is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var limits = PlanCatalog.For((Tier)org.Tier);
                    // The lifetime grant is dormant (no tier sets one, ADR-0035) but the machinery is
                    // intact: a tier that defines one spends it instead of the monthly counter.
                    var onLifetimeGrant = limits.AiFixesLifetime > 0;

                    // A fix needs a repo to act on. Linking one (#89) is a prerequisite.
                    var linked = await repos.ListByProjectAsync(projectId);
                    if (linked.Count == 0)
                    {
                        return TypedResults.Conflict(new ErrorResponse("no_repo_linked"));
                    }

                    // The fix targets one repo + base branch. The caller picks them (a project may link
                    // several repos, and any branch of a repo is fair game); default to the sole/first
                    // linked repo and its default branch. Resolved before reserving allowance so an
                    // invalid choice never burns a run.
                    var repo = linked[0];
                    if (req.RepoId is { } chosenRepoId)
                    {
                        if (linked.FirstOrDefault(candidate => candidate.Id == chosenRepoId) is not { } chosen)
                        {
                            return TypedResults.Conflict(new ErrorResponse("repo_not_linked"));
                        }
                        repo = chosen;
                    }
                    var baseBranch = string.IsNullOrWhiteSpace(req.BaseBranch)
                        ? repo.DefaultBranch
                        : req.BaseBranch.Trim();

                    // Cost cap (ADR-0020/0027): refuse once month-to-date spend has reached the effective
                    // ceiling — the org's own override, else the tier's fair-use compute default
                    // (Team/Business compute ceiling; Enterprise/BYO has no default, so it's a pure customer
                    // budget). Checked before reserving a run so a capped org burns no allowance.
                    if (AiFixBudget.IsOverCap(
                        AiFixBudget.EffectiveCapUsd(
                            org.AiFixCostCapUsd, limits, (FixExecution)org.FixExecution),
                        await spend.MonthToDateUsdAsync(org.Id, DateTimeOffset.UtcNow, http.RequestAborted)))
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fix_cost_cap_exceeded"));
                    }

                    // Allowance (#100): atomically reserve one run from the org's monthly counter (the
                    // lifetime path is dormant, ADR-0035). Reserving at request time counts
                    // in-flight runs too, so rapid requests cannot over-consume; a failed run is refunded.
                    // Checked last so a rejected request (no repo) never burns allowance.
                    var reservedAt = DateTimeOffset.UtcNow;
                    var reserved = onLifetimeGrant
                        ? await quota.TryConsumeLifetimeAsync(
                            org.Id, limits.AiFixesLifetime, reservedAt, http.RequestAborted)
                        : await quota.TryConsumeAsync(
                            org.Id, limits.AiFixesPerMonth, reservedAt, http.RequestAborted);
                    if (!reserved)
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fix_quota_exceeded"));
                    }

                    var actor = OrgAuthorization.CurrentUserId(http.User).ToString(CultureInfo.InvariantCulture);
                    var logger = loggerFactory.CreateLogger(nameof(FixEndpoints));
                    // Hands the reservation back: the lifetime grant or the month, whichever it came from.
                    Task RefundAsync() => onLifetimeGrant
                        ? quota.RefundLifetimeAsync(org.Id, CancellationToken.None)
                        : quota.RefundAsync(org.Id, reservedAt, CancellationToken.None);

                    // The run is reserved before the context is built, so an org with none left costs no
                    // GitHub or source-map call. No job is sent while the context is built, so any failure
                    // there hands the run back.
                    FixContext context;
                    long installationId;
                    try
                    {
                        (context, installationId) = await FixRequestContext.BuildAsync(
                            projectId, issue.InternalId, issue.Summary.FirstRelease, repo, org.Id,
                            installations, releases, events, symbolication, repos, http.RequestServices, logger,
                            http.RequestAborted);
                    }
                    catch
                    {
                        await RefundAsync();
                        throw;
                    }

                    var job = new FixJob(issue.InternalId, repo.RepoFullName, baseBranch, actor, ModelDefaults.Fix)
                    {
                        Prompt = context.Prompt,
                        ScopedPaths = context.ScopedPaths,
                        InstallationId = installationId,
                        OrgId = org.Id,
                        JobId = Guid.CreateVersion7(),
                        ReservedAt = reservedAt,
                    };

                    // The run is reserved, so whatever stops it reaching the queue hands it back, and a
                    // broker outage surfaces as a clear 503, not a silently burnt fix. A request refused
                    // because the issue has a run in progress gets its reservation back too.
                    var enqueued = await FixEnqueue.EnqueueAsync(
                        (FixExecution)org.FixExecution, job, context, leases, fixes, publisher, logger,
                        http.RequestAborted);
                    if (enqueued.Failure is not null)
                    {
                        // Logged even when the run went ahead: a publish can fail after the job arrived.
                        logger.LogError(
                            enqueued.Failure, "fix enqueue failed org={OrgId} issue={IssueId} outcome={Outcome}",
                            org.Id, issue.InternalId, enqueued.Outcome);
                    }
                    if (enqueued.Outcome == FixQueueOutcome.InProgress)
                    {
                        await RefundAsync();
                        return TypedResults.Conflict(new ErrorResponse("fix_in_progress"));
                    }
                    if (enqueued.Outcome != FixQueueOutcome.Queued)
                    {
                        // An unsettled reservation is kept; FixQueueOutcome.Unsettled says how it is settled.
                        if (enqueued.Outcome == FixQueueOutcome.Withdrawn)
                        {
                            await RefundAsync();
                        }
                        return TypedResults.Json(
                            new ErrorResponse("fix_enqueue_failed"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    // A new run exists; nudge every open dashboard for this project (ADR-0030) so the
                    // fixes surface shows it without waiting for a poll — the requester's own tab already
                    // knows via its mutation, but any other viewer would otherwise see nothing.
                    await ProjectEventNudge.TrySendAsync(
                        projectEvents, logger, projectId, http.RequestAborted);

                    return TypedResults.Accepted($"/api/projects/{projectId}/issues/{issueId}/fixes");
                })
            .WithName("requestFix").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/issues/{issueId:guid}/fixes",
                async Task<Results<Ok<IReadOnlyList<FixSuggestion>>, NotFound>> (
                    long projectId, Guid issueId, IssueRepository issues, IFixStore fixes) =>
                {
                    return await issues.GetByPublicIdAsync(projectId, issueId) is { } issue
                        ? TypedResults.Ok(await fixes.ListByIssueAsync(issue.InternalId))
                        : TypedResults.NotFound();
                })
            .WithName("listFixes").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
