using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The supply-chain CVE-fix surface (#117 slice 2): trigger a Conductor draft PR that bumps a vulnerable
/// dependency, and read a repo's bump runs. Triggering re-fetches the advisory from GitHub (never trusts
/// the client), reserves from the same AI-fix allowance + cost cap as an issue fix, then publishes a job
/// the Conductor's CVE worker drains — the run is async and always ends in a human-reviewed draft PR.
/// Tenancy enforced: trigger needs admin, reads member.
/// </summary>
internal static class CveFixEndpoints
{

    public static void MapCveFixEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/repos/{repoId:guid}/cve-fixes",
                async Task<Results<Accepted, NotFound, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>>> (
                    long projectId, Guid repoId, StartCveFixRequest req, HttpContext http,
                    RepoLinkRepository repos, ProjectRepository projects, OrgRepository orgs,
                    GithubInstallationRepository installations, ICveFixStore cveFixes, ICveFixPublisher publisher,
                    PostgresJobLeaseStore leases, ProjectEventNotifier projectEvents,
                    IAiFixQuota quota, IAiFixSpend spend, ILoggerFactory loggerFactory) =>
                {
                    var ct = http.RequestAborted;
                    if (await repos.GetAsync(projectId, repoId, ct) is not { } repo
                        || await projects.GetAsync(projectId, ct) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }

                    // Same plan gate as an issue fix. Every tier now includes runs (ADR-0035), so a CVE bump
                    // draws on the same allowance an issue fix does — on Free that is one shared pool of 3.
                    var org = await orgs.GetAsync(project.OrgId, ct);
                    if (org is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var limits = PlanCatalog.For((Tier)org.Tier);
                    // Dormant lifetime grant, same as the issue-fix path (ADR-0035).
                    var onLifetimeGrant = limits.AiFixesLifetime > 0;

                    // A bump needs GitHub connected (to read the authoritative advisory and open the PR).
                    var tokens = http.RequestServices.GetService<GitHubInstallationTokens>();
                    var repoClient = http.RequestServices.GetService<GitHubRepoClient>();
                    var installs = await installations.GetByOrgAsync(org.Id, ct);
                    if (tokens is null || repoClient is null || installs.Count == 0)
                    {
                        return TypedResults.Conflict(new ErrorResponse("github_not_connected"));
                    }

                    // Re-fetch the advisory from GitHub and match by id — the client only supplies the GHSA id,
                    // so a stale/forged package or version can never drive the bump. One advisory can raise an
                    // alert per monorepo workspace member (#41), so keep every match: the run bumps them all in
                    // one draft PR, with the fixable one (it names the patched version) leading.
                    IReadOnlyList<DependabotAlert> matching;
                    try
                    {
                        var token = await tokens.GetAsync(installs[0].InstallationId, ct);
                        matching =
                        [
                            .. (await repoClient.ListDependabotAlertsAsync(token, repo.RepoFullName, ct))
                                .Where(a => a.GhsaId == req.GhsaId)
                                .OrderByDescending(a => !string.IsNullOrEmpty(a.FixedVersion)),
                        ];
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        loggerFactory.CreateLogger(nameof(CveFixEndpoints))
                            .LogWarning(ex, "cve-fix advisory fetch failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Conflict(new ErrorResponse("github_not_connected"));
                    }

                    if (matching.Count == 0)
                    {
                        return TypedResults.NotFound();
                    }
                    var alert = matching[0];
                    if (string.IsNullOrEmpty(alert.FixedVersion))
                    {
                        return TypedResults.Conflict(new ErrorResponse("cve_not_fixable"));
                    }
                    // Manifests from the chosen PACKAGE's alerts only: one advisory can affect several
                    // packages, and scoping another package's manifest into a run whose prompt bumps
                    // this one would touch a file the instruction does not cover.
                    var context = CveFixContextAssembler.Assemble(
                        alert.Package, alert.Ecosystem, alert.VulnerableRange, alert.FixedVersion,
                        alert.GhsaId, alert.CveId, alert.Summary,
                        [.. matching
                            .Where(a => a.Package == alert.Package)
                            .Select(a => a.ManifestPath)
                            .OfType<string>()]);
                    if (context.ScopedPaths.Count == 0)
                    {
                        return TypedResults.Conflict(new ErrorResponse("cve_ecosystem_unsupported"));
                    }

                    // One in-flight bump per repo + advisory: stops a double-click opening two PRs / burning
                    // two allowance slots. A concluded run leaves the slot free to re-run.
                    if (await cveFixes.HasActiveRunAsync(repoId, alert.GhsaId, ct))
                    {
                        return TypedResults.Conflict(new ErrorResponse("cve_fix_in_progress"));
                    }

                    // Cost cap (ADR-0020/0027) then allowance (#100/#112) — the same budget an issue fix
                    // draws from, so a capped/exhausted org burns nothing here. The cap is the org override
                    // else the tier's fair-use compute default (waived for runner execution, whose model
                    // spend is the customer's own). Checked before reserving.
                    if (AiFixBudget.IsOverCap(
                        AiFixBudget.EffectiveCapUsd(
                            org.AiFixCostCapUsd, limits, (FixExecution)org.FixExecution),
                        await spend.MonthToDateUsdAsync(org.Id, DateTimeOffset.UtcNow, ct)))
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fix_cost_cap_exceeded"));
                    }

                    var reserved = onLifetimeGrant
                        ? await quota.TryConsumeLifetimeAsync(org.Id, limits.AiFixesLifetime, DateTimeOffset.UtcNow, ct)
                        : await quota.TryConsumeAsync(org.Id, limits.AiFixesPerMonth, DateTimeOffset.UtcNow, ct);
                    if (!reserved)
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fix_quota_exceeded"));
                    }

                    var actor = OrgAuthorization.CurrentUserId(http.User).ToString(CultureInfo.InvariantCulture);
                    var job = new CveFixJob(
                        repo.Id, repo.RepoFullName, repo.DefaultBranch, alert.GhsaId, alert.CveId,
                        alert.Package, alert.Ecosystem, alert.VulnerableRange, alert.FixedVersion,
                        alert.HtmlUrl, actor, ModelDefaults.Fix)
                    {
                        Prompt = context.Prompt,
                        ScopedPaths = context.ScopedPaths,
                        InstallationId = installs[0].InstallationId,
                        OrgId = org.Id,
                    };

                    // The reservation is taken; if the job never reaches the topic the run never happens, so
                    // refund here (the orchestrator only refunds runs it actually started).
                    try
                    {
                        // Same routing as an issue fix (ADR-0033 slice 4c): a self-hosting org gets a
                        // leasable row its own runner takes; everyone else gets the Kafka topic. The
                        // advisory re-fetch, the cost cap and the allowance above are identical either way.
                        if ((FixExecution)org.FixExecution == FixExecution.Runner)
                        {
                            var now = DateTimeOffset.UtcNow;
                            await leases.EnqueueCveAsync(
                                new CveFixRun(
                                    Guid.CreateVersion7(), repo.Id, alert.GhsaId, alert.CveId,
                                    alert.Package, alert.Ecosystem, alert.VulnerableRange, alert.FixedVersion,
                                    alert.HtmlUrl, FixStatus.Pending, PostgresJobLeaseStore.RunnerProvider,
                                    Model: "", Branch: "", PrUrl: "", Summary: "", actor, now, now),
                                new RunnerJobContext(repo.DefaultBranch, context.Prompt, context.ScopedPaths),
                                ct);
                        }
                        else
                        {
                            await publisher.PublishAsync(job, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        loggerFactory.CreateLogger(nameof(CveFixEndpoints))
                            .LogError(ex, "cve-fix enqueue failed org={OrgId} ghsa={Ghsa}", org.Id, alert.GhsaId);
                        if (onLifetimeGrant)
                        {
                            await quota.RefundLifetimeAsync(org.Id, ct);
                        }
                        else
                        {
                            await quota.RefundAsync(org.Id, DateTimeOffset.UtcNow, ct);
                        }
                        return TypedResults.Json(
                            new ErrorResponse("cve_fix_enqueue_failed"),
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    // A new run exists; nudge every open dashboard for this project (ADR-0030), same as
                    // the issue-fix request path — any other viewer's CVE panel would otherwise sit on
                    // its slow poll.
                    await ProjectEventNudge.TrySendAsync(
                        projectEvents, loggerFactory.CreateLogger(nameof(CveFixEndpoints)), projectId, ct);

                    return TypedResults.Accepted($"/api/projects/{projectId}/repos/{repoId}/cve-fixes");
                })
            .WithName("startCveFix").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/cve-fixes",
                async Task<Results<Ok<IReadOnlyList<CveFixRun>>, NotFound>> (
                    long projectId, Guid repoId, HttpContext http, RepoLinkRepository repos,
                    ICveFixStore cveFixes) =>
                    await repos.GetAsync(projectId, repoId, http.RequestAborted) is null
                        ? TypedResults.NotFound()
                        : TypedResults.Ok(await cveFixes.ListByRepoAsync(repoId, http.RequestAborted)))
            .WithName("listCveFixes").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
