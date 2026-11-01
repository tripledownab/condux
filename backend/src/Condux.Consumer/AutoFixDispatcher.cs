using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.OrgNotifications;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.Core.Repos;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;
using Condux.Storage.SourceMaps;

namespace Condux.Consumer;

/// <summary>
/// Auto-fix mode (#101): when an org is in <see cref="AiFixMode.Auto"/>, a new or regressed
/// error-or-worse issue publishes a <see cref="FixJob"/> to the Conductor topic — the same context
/// assembly + quota reservation + routing the manual RequestFix does, minus the human click — including
/// handing the work to the org's own runner when that is where it runs its fixes (ADR-0033 slice 4c). The
/// run still ends in a human-reviewed <b>draft</b> PR (auto means auto-<i>request</i>, never auto-merge).
/// Best-effort: any failure is logged and swallowed, never disrupting ingest. Built like AlertDispatcher.
/// </summary>
public sealed class AutoFixDispatcher(
    ProjectRepository projects, OrgRepository orgs, RepoLinkRepository repos, ReleaseRepository releases,
    GithubInstallationRepository installations, IAiFixQuota quota, IAiFixSpend spend,
    IFixRequestPublisher publisher, PostgresJobLeaseStore leases, IFixStore fixes,
    ConductorPauseNotifier pauseNotifier, EventSymbolication symbolication, ILogger<AutoFixDispatcher> logger)
{
    private const string AutoActor = "auto"; // distinguishes an auto-requested run from a user id in the audit

    // How long a new issue's fix context waits for symbols. The drain handles one event at a time, so every
    // second here holds up ingest for every project; a fix built from the stored frames is still a fix, and
    // the issue page, which has no such hurry, keeps its own longer wait.
    private static readonly TimeSpan SymbolicationBudget = TimeSpan.FromSeconds(2);

    public async Task DispatchAsync(
        long projectId, UpsertResult upsert, Event sampleEvent, CancellationToken cancellationToken = default)
    {
        // Same triggers as alerts: a brand-new issue (occurrence 1) or a regression (a resolved issue
        // reopened). Every other occurrence is a no-op, so auto-fix fires at most once per new issue.
        if (upsert.Occurrence != 1 && !upsert.Reopened)
        {
            return;
        }

        try
        {
            if (await projects.GetAsync(projectId, cancellationToken) is not { } project
                || await orgs.GetAsync(project.OrgId, cancellationToken) is not { } org)
            {
                return;
            }

            var linked = await repos.ListByProjectAsync(projectId, cancellationToken);
            var tier = (Tier)org.Tier;
            var limits = PlanCatalog.For(tier);
            if (!AutoFixPolicy.ShouldTrigger((AiFixMode)org.AiFixMode, tier, linked.Count > 0, sampleEvent.Level))
            {
                return;
            }

            // Cost cap (ADR-0020/0027): the effective ceiling is the org's own override, else the tier's
            // fair-use compute default (Team/Business ceiling; Enterprise/BYO has no default). A capped
            // org that has spent it this month skips auto-fix, like being out of allowance.
            if (AiFixBudget.IsOverCap(
                AiFixBudget.EffectiveCapUsd(org.AiFixCostCapUsd, limits, (FixExecution)org.FixExecution),
                await spend.MonthToDateUsdAsync(org.Id, DateTimeOffset.UtcNow, cancellationToken)))
            {
                logger.LogInformation(
                    "auto-fix skipped: cost cap reached org={OrgId} issue={IssueId}", org.Id, upsert.Id);
                // Tell the org (once per window) that auto-fix is paused, so a reached cap isn't silent.
                await pauseNotifier.NotifyAsync(
                    org.Id, org.Name, ConductorPauseReason.CostCapReached, cancellationToken);
                return;
            }

            // Atomically reserve one run from the org's monthly allowance (#100). Out of allowance is a
            // silent skip: auto-fix is best-effort and a user can still request manually.
            var reservedAt = DateTimeOffset.UtcNow;
            if (!await quota.TryConsumeAsync(org.Id, limits.AiFixesPerMonth, reservedAt, cancellationToken))
            {
                logger.LogInformation(
                    "auto-fix skipped: allowance exhausted org={OrgId} issue={IssueId}", org.Id, upsert.Id);
                await pauseNotifier.NotifyAsync(
                    org.Id, org.Name, ConductorPauseReason.AllowanceExhausted, cancellationToken);
                return;
            }

            // The run is reserved before the context is built, so an org with none left costs no source-map
            // fetch. No job is sent while the context is built, so any failure there hands the run back.
            var repo = linked[0];
            (FixContext Context, FixJob Job) built;
            try
            {
                built = await BuildJobAsync(projectId, upsert, sampleEvent, org.Id, reservedAt, repo, cancellationToken);
            }
            catch
            {
                await quota.RefundAsync(org.Id, reservedAt, CancellationToken.None);
                throw;
            }

            var (context, job) = built;
            // No app frame resolved to a repo-relative path (a failed agent span carries none, a library's frames
            // are not the app's, and an absolute build path needs a code mapping), so an unattended run would have
            // nothing to start from: the in-process gateway fails such a run before calling the model, and the
            // managed agents one searches the whole repository on its own budget. A person can still ask for one.
            // A repo-relative path is only a path's shape, so a run can still find no such file and fail.
            if (context.ScopedPaths.Count == 0)
            {
                logger.LogInformation(
                    "auto-fix skipped: no app frame with a repo-relative path org={OrgId} issue={IssueId}", org.Id, upsert.Id);
                await quota.RefundAsync(org.Id, reservedAt, CancellationToken.None);
                return;
            }

            // Routed by the org's execution setting, exactly as the manual request is (ADR-0033 slice 4c): a
            // self-hosting org's auto-fix must land on its runner too, or turning auto on would quietly send
            // the work somewhere the customer chose not to use. Either way the run is written first, so an
            // issue with a run in progress gets no second one.
            var enqueued = await FixEnqueue.EnqueueAsync(
                (FixExecution)org.FixExecution, job, context, leases, fixes, publisher, logger, cancellationToken);
            if (enqueued.Failure is not null)
            {
                // Logged even when the run went ahead: a publish can fail after the job arrived.
                logger.LogWarning(
                    enqueued.Failure, "auto-fix enqueue failed org={OrgId} issue={IssueId} outcome={Outcome}",
                    org.Id, upsert.Id, enqueued.Outcome);
            }
            switch (enqueued.Outcome)
            {
                case FixQueueOutcome.Queued:
                    logger.LogInformation(
                        "auto-fix requested org={OrgId} issue={IssueId} repo={Repo}",
                        org.Id, upsert.Id, repo.RepoFullName);
                    break;
                case FixQueueOutcome.InProgress:
                    // A run is already queued or running for this issue, as when it regresses again before
                    // the first fix concludes. Not a pause: the work is already in hand.
                    logger.LogInformation(
                        "auto-fix skipped: a run is in progress org={OrgId} issue={IssueId}", org.Id, upsert.Id);
                    await quota.RefundAsync(org.Id, reservedAt, CancellationToken.None);
                    break;
                case FixQueueOutcome.Withdrawn:
                    await quota.RefundAsync(org.Id, reservedAt, CancellationToken.None);
                    break;
                default:
                    // An unsettled reservation is kept; FixQueueOutcome.Unsettled says how it is settled.
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "auto-fix dispatch error project={ProjectId} issue={IssueId}", projectId, upsert.Id);
        }
    }

    // The job for one issue: its repo's code mappings, the release the event carries and the event itself,
    // symbolicated so a bundled frame names the source file and not the minified chunk no repository holds.
    private async Task<(FixContext Context, FixJob Job)> BuildJobAsync(
        long projectId, UpsertResult upsert, Event sampleEvent, long orgId, DateTimeOffset reservedAt, RepoLink repo,
        CancellationToken cancellationToken)
    {
        var mappings = await repos.ListCodeMappingsAsync(repo.Id, cancellationToken);
        // Release attribution (#144): the release this event carries is a bisect hint for the fix. On a
        // brand-new issue that is exactly the issue's first_release; on a regression it is the release the
        // bug returned in, and both are the right point in history to reason from.
        var release = await ResolveReleaseAsync(projectId, sampleEvent.Release, cancellationToken);
        var symbolicated = await symbolication.SymbolicateAsync(
            projectId, sampleEvent, SymbolicationBudget, cancellationToken);
        var context = FixContextAssembler.Assemble(symbolicated, mappings, release);
        var installs = await installations.GetByOrgAsync(orgId, cancellationToken);
        var job = new FixJob(upsert.Id, repo.RepoFullName, repo.DefaultBranch, AutoActor, ModelDefaults.Fix)
        {
            Prompt = context.Prompt,
            ScopedPaths = context.ScopedPaths,
            InstallationId = installs.Count > 0 ? installs[0].InstallationId : 0,
            OrgId = orgId,
            JobId = Guid.CreateVersion7(),
            ReservedAt = reservedAt,
        };
        return (context, job);
    }

    // The release the event carries, resolved to its recorded commit (#144). Null when the event has no
    // release; a version with no recorded release still attributes the version (commit null).
    private async Task<ReleaseContext?> ResolveReleaseAsync(
        long projectId, string? version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var release = await releases.GetByVersionAsync(projectId, version, cancellationToken);
        return new ReleaseContext(version, release?.CommitSha);
    }
}
