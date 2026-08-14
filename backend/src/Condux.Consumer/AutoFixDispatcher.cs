using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.OrgNotifications;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;

namespace Condux.Consumer;

/// <summary>
/// Auto-fix mode (#101): when an org is in <see cref="AiFixMode.Auto"/>, a new or regressed
/// error-or-worse issue publishes a <see cref="FixJob"/> to the Conductor topic — the same context
/// assembly + quota reservation + routing the manual RequestFix does, minus the human click — including
/// handing the work to the org's own runner when that is where it runs its fixes (ADR-0033 slice 4c). The
/// run still ends in a human-reviewed <b>draft</b> PR (auto means auto-<i>request</i>, never auto-merge).
/// Best-effort: any failure is logged and swallowed, never disrupting ingest. Mirrors AlertDispatcher.
/// </summary>
public sealed class AutoFixDispatcher(
    ProjectRepository projects, OrgRepository orgs, RepoLinkRepository repos, ReleaseRepository releases,
    GithubInstallationRepository installations, IAiFixQuota quota, IAiFixSpend spend,
    IFixRequestPublisher publisher, PostgresJobLeaseStore leases, IFixStore fixes,
    ConductorPauseNotifier pauseNotifier, ILogger<AutoFixDispatcher> logger)
{
    private const string AutoActor = "auto"; // distinguishes an auto-requested run from a user id in the audit

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
            // silent skip — auto-fix is best-effort and a user can still request manually.
            if (!await quota.TryConsumeAsync(org.Id, limits.AiFixesPerMonth, DateTimeOffset.UtcNow, cancellationToken))
            {
                logger.LogInformation(
                    "auto-fix skipped: allowance exhausted org={OrgId} issue={IssueId}", org.Id, upsert.Id);
                await pauseNotifier.NotifyAsync(
                    org.Id, org.Name, ConductorPauseReason.AllowanceExhausted, cancellationToken);
                return;
            }

            var repo = linked[0];
            var mappings = await repos.ListCodeMappingsAsync(repo.Id, cancellationToken);
            // Release attribution (#144): the release this event carries is a bisect hint for the fix. On a
            // brand-new issue that is exactly the issue's first_release; on a regression it is the release the
            // bug returned in — both are the right point in history to reason from. Resolve it to its commit.
            var release = await ResolveReleaseAsync(projectId, sampleEvent.Release, cancellationToken);
            var context = FixContextAssembler.Assemble(sampleEvent, mappings, release);
            var installs = await installations.GetByOrgAsync(org.Id, cancellationToken);
            var job = new FixJob(upsert.Id, repo.RepoFullName, repo.DefaultBranch, AutoActor, ModelDefaults.Fix)
            {
                Prompt = context.Prompt,
                ScopedPaths = context.ScopedPaths,
                InstallationId = installs.Count > 0 ? installs[0].InstallationId : 0,
                OrgId = org.Id,
            };

            try
            {
                // Routed by the org's execution setting, exactly as the manual request is (ADR-0033 slice
                // 4c): a self-hosting org's auto-fix must land on its runner too, or turning auto on would
                // quietly send the work somewhere the customer chose not to use.
                if ((FixExecution)org.FixExecution == FixExecution.Runner)
                {
                    var fixId = Guid.CreateVersion7();
                    await leases.EnqueueAsync(
                        fixId, upsert.Id, repo.RepoFullName,
                        new RunnerJobContext(repo.DefaultBranch, context.Prompt, context.ScopedPaths),
                        DateTimeOffset.UtcNow, cancellationToken);

                    // The hosted orchestrator writes this when it starts the run. A run handed to a runner
                    // never reaches it, so without this nothing records that the platform asked for the
                    // fix — fix_suggestions has no actor column.
                    //
                    // Caught here rather than left to the enclosing handler: the row is already committed,
                    // and that handler refunds the allowance. An audit failure would hand back a run that
                    // is still going to happen, so the counter would drift every time the write failed.
                    try
                    {
                        await fixes.AppendAuditAsync(
                            fixId, AutoActor, "requested",
                            JsonSerializer.Serialize(new
                            {
                                repo = repo.RepoFullName,
                                model = "",
                                provider = PostgresJobLeaseStore.RunnerProvider,
                                promptHash = FixOrchestrator.PromptHash(context.Prompt),
                            }),
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "auto-fix audit failed org={OrgId} fix={FixId}", org.Id, fixId);
                    }
                }
                else
                {
                    await publisher.PublishAsync(job, cancellationToken);
                }

                logger.LogInformation(
                    "auto-fix requested org={OrgId} issue={IssueId} repo={Repo}",
                    org.Id, upsert.Id, repo.RepoFullName);
            }
            catch (Exception ex)
            {
                // The reservation is taken; if the job never reached the topic the run won't happen, so
                // refund it (the orchestrator only refunds runs it actually started).
                logger.LogWarning(ex, "auto-fix enqueue failed org={OrgId} issue={IssueId}", org.Id, upsert.Id);
                await quota.RefundAsync(org.Id, DateTimeOffset.UtcNow, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "auto-fix dispatch error project={ProjectId} issue={IssueId}", projectId, upsert.Id);
        }
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
