using System.Globalization;
using System.Text.Json;
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

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The Conductor's request surface (#60/#89): trigger a fix for an issue and read its suggestions.
/// Triggering resolves the issue (public UUID → internal id) and the project's linked repo, then
/// publishes a job to the topic the Conductor worker drains — the run itself is async and always ends
/// in a human-reviewed draft PR (never auto-applied). Tenancy enforced: trigger needs admin, reads member.
/// </summary>
internal static class FixEndpoints
{
    private const string DefaultModel = "claude-opus-4-8";

    public static void MapFixEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/issues/{issueId:guid}/fix",
                async Task<Results<Accepted, NotFound, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>>> (
                    long projectId, Guid issueId, RequestFixRequest req, HttpContext http,
                    IssueRepository issues, ProjectRepository projects, OrgRepository orgs,
                    RepoLinkRepository repos, ReleaseRepository releases,
                    GithubInstallationRepository installations,
                    ClickHouseEventReader events, IAiFixQuota quota, IAiFixSpend spend,
                    IFixRequestPublisher publisher, ILoggerFactory loggerFactory) =>
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
                        AiFixBudget.EffectiveCapUsd(org.AiFixCostCapUsd, limits),
                        await spend.MonthToDateUsdAsync(org.Id, DateTimeOffset.UtcNow, http.RequestAborted)))
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fix_cost_cap_exceeded"));
                    }

                    // Allowance (#100): atomically reserve one run from the org's monthly counter (the
                    // lifetime path is dormant, ADR-0035). Reserving at request time counts
                    // in-flight runs too, so rapid requests cannot over-consume; a failed run is refunded.
                    // Checked last so a rejected request (no repo) never burns allowance.
                    var reserved = onLifetimeGrant
                        ? await quota.TryConsumeLifetimeAsync(
                            org.Id, limits.AiFixesLifetime, DateTimeOffset.UtcNow, http.RequestAborted)
                        : await quota.TryConsumeAsync(
                            org.Id, limits.AiFixesPerMonth, DateTimeOffset.UtcNow, http.RequestAborted);
                    if (!reserved)
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fix_quota_exceeded"));
                    }

                    var actor = OrgAuthorization.CurrentUserId(http.User).ToString(CultureInfo.InvariantCulture);
                    // The org's GitHub App installation (0 when GitHub is not connected): a real provider
                    // mints a repo-scoped token from it, and the suspect-commit lookup below needs it too.
                    var installs = await installations.GetByOrgAsync(org.Id, http.RequestAborted);
                    var installationId = installs.Count > 0 ? installs[0].InstallationId : 0;

                    // Release attribution + suspect commit (#144): resolve the release the issue was first
                    // seen in → its commit (a bisect hint), then blame the culprit file at that commit for the
                    // change that most likely introduced the bug. Both best-effort — GitHub off or an
                    // unrecorded release simply drops the respective hint.
                    var logger = loggerFactory.CreateLogger(nameof(FixEndpoints));
                    var release = await ResolveReleaseAsync(
                        projectId, issue.Summary.FirstRelease, releases, http.RequestAborted);
                    var context = await AssembleContextAsync(
                        projectId, issue.InternalId, repo, release, installationId,
                        http.RequestServices.GetService<ISourceHostTokens>(),
                        http.RequestServices.GetService<ISourceHostClient>(),
                        events, repos, logger, http.RequestAborted);
                    var job = new FixJob(issue.InternalId, repo.RepoFullName, baseBranch, actor, DefaultModel)
                    {
                        Prompt = context.Prompt,
                        ScopedPaths = context.ScopedPaths,
                        InstallationId = installationId,
                        OrgId = org.Id,
                    };

                    // The reservation is already taken; if the job never reaches the topic the run will
                    // never happen, so refund it here (the orchestrator only refunds runs it actually
                    // started). A broker outage then surfaces as a clear 503, not a silently burnt fix.
                    try
                    {
                        await publisher.PublishAsync(job, http.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex, "fix enqueue failed org={OrgId} issue={IssueId}", org.Id, issue.InternalId);
                        if (onLifetimeGrant)
                        {
                            await quota.RefundLifetimeAsync(org.Id, http.RequestAborted);
                        }
                        else
                        {
                            await quota.RefundAsync(org.Id, DateTimeOffset.UtcNow, http.RequestAborted);
                        }
                        return TypedResults.Json(
                            new ErrorResponse("fix_enqueue_failed"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

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

    // Default options match the ClickHouse writer's JsonSerializer.Serialize(event) (PascalCase, integer
    // enums), so the stored payload round-trips back to an Event here.
    private static readonly JsonSerializerOptions PayloadJson = new();

    // The release the issue was first seen in, resolved to its recorded commit (#144). Null when the issue
    // has no first_release; a version with no recorded release still attributes the version (commit null).
    private static async Task<ReleaseContext?> ResolveReleaseAsync(
        long projectId, string? firstRelease, ReleaseRepository releases, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(firstRelease))
        {
            return null;
        }

        var release = await releases.GetByVersionAsync(projectId, firstRelease, cancellationToken);
        return new ReleaseContext(firstRelease, release?.CommitSha);
    }

    // Assemble the scoped, double-scrubbed fix context from the issue's latest stored event + the repo's
    // code mappings (#63) + the release attribution and suspect commit (#144). Best-effort: with no event
    // yet (or an unreadable payload), return an empty context so the run falls back to a minimal prompt.
    private static async Task<FixContext> AssembleContextAsync(
        long projectId, long issueId, RepoLink repo, ReleaseContext? release, long installationId,
        ISourceHostTokens? tokens, ISourceHostClient? github, ClickHouseEventReader events,
        RepoLinkRepository repos, ILogger logger, CancellationToken cancellationToken)
    {
        // Context assembly is best-effort by design (#63): no sampled event degrades to the minimal
        // prompt, and an unreachable event store must degrade the same way, not fail the request.
        IReadOnlyList<StoredEvent> recent;
        try
        {
            recent = await events.RecentByIssueAsync(
                projectId.ToString(CultureInfo.InvariantCulture), (ulong)issueId, 1, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "fix context assembly skipped: event store unreachable issue={IssueId}", issueId);
            recent = [];
        }

        if (recent.Count == 0
            || JsonSerializer.Deserialize<Event>(recent[0].Payload, PayloadJson) is not { } sample)
        {
            return new FixContext("", []);
        }

        var mappings = await repos.ListCodeMappingsAsync(repo.Id, cancellationToken);
        var suspect = await ResolveSuspectAsync(
            repo, release, installationId, tokens, github, sample, mappings, logger, cancellationToken);
        return FixContextAssembler.Assemble(sample, mappings, release, suspect);
    }

    // The commit that most likely introduced the error (#144): the last change to the culprit file as of the
    // first-seen release commit, via GitHub blame-by-file. Needs GitHub connected + a release commit + an
    // in-app culprit frame; best-effort, so any gap or a GitHub hiccup just drops the hint (never fails).
    private static async Task<SuspectCommitContext?> ResolveSuspectAsync(
        RepoLink repo, ReleaseContext? release, long installationId,
        ISourceHostTokens? tokens, ISourceHostClient? github,
        Event sample, IReadOnlyList<CodeMapping> mappings, ILogger logger, CancellationToken cancellationToken)
    {
        if (release?.CommitSha is not { } sha || tokens is null || github is null || installationId == 0
            || FixContextAssembler.CulpritPath(sample, mappings) is not { } culprit)
        {
            return null;
        }

        try
        {
            var token = await tokens.GetAsync(installationId, cancellationToken);
            if (await github.GetLatestCommitTouchingAsync(token, repo.RepoFullName, culprit, sha, cancellationToken)
                is { } commit)
            {
                return new SuspectCommitContext(commit.Sha, commit.Subject, commit.Author);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "suspect-commit lookup skipped repo={Repo}", repo.RepoFullName);
        }

        return null;
    }
}
