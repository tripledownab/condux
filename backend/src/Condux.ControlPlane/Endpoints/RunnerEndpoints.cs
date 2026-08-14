using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.Quotas;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Customer-hosted runners (ADR-0033 slice 4, #68). Two surfaces that share a credential:
///
/// Management is cookie-authed from the dashboard, admin mints and revokes, member reads, and the raw
/// token is returned once on mint. Mirrors <see cref="McpTokenEndpoints"/>, but scoped to an org rather
/// than a project, because a runner serves whatever work its org produces.
///
/// The lease surface is what the runner itself calls, authenticated by that token alone with no cookie.
/// It is deliberately tiny and additive-only: a customer's runner lags our deploys, so a field added here
/// must never be required of an older runner, and one removed breaks every runner at once.
/// </summary>
internal static class RunnerEndpoints
{
    /// <summary>Who the audit trail credits a runner-executed step to. Distinct from a user, because a
    /// customer's runner acted rather than a person, and an operator reading the history should see that.
    /// </summary>
    private const string RunnerActor = "runner";

    public static void MapRunnerEndpoints(this IEndpointRouteBuilder app)
    {
        MapTokenManagement(app);
        MapLeaseProtocol(app);
    }

    private static void MapTokenManagement(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orgs/{orgId:long}/runner-tokens",
                async Task<Ok<MintedRunnerTokenResponse>> (
                    long orgId, CreateRunnerTokenRequest req, RunnerTokenRepository tokens) =>
                {
                    var label = string.IsNullOrWhiteSpace(req.Label) ? "Runner" : req.Label.Trim();
                    var (raw, hash) = RunnerTokens.Create();
                    var token = await tokens.CreateAsync(orgId, hash, label);
                    return TypedResults.Ok(
                        new MintedRunnerTokenResponse(token.Id, token.Name, raw, token.CreatedAt));
                })
            .WithName("createRunnerToken").WithTags("RunnerTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapGet("/api/orgs/{orgId:long}/runner-tokens",
                async (long orgId, RunnerTokenRepository tokens) =>
                    TypedResults.Ok((await tokens.ListByOrgAsync(orgId)).Select(ToResponse).ToArray()))
            .WithName("listRunnerTokens").WithTags("RunnerTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapDelete("/api/orgs/{orgId:long}/runner-tokens/{tokenId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long orgId, Guid tokenId, RunnerTokenRepository tokens) =>
                    await tokens.RevokeAsync(orgId, tokenId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("revokeRunnerToken").WithTags("RunnerTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }

    private static void MapLeaseProtocol(IEndpointRouteBuilder app)
    {
        // Take the next job, or 204 when there is none. 204 is the ordinary answer: a runner polls
        // continuously and most polls find nothing, so it must not read as an error.
        app.MapPost("/api/runner/lease",
                async Task<Results<Ok<LeasedJobResponse>, NoContent, UnauthorizedHttpResult>> (
                    HttpContext http, RunnerTokenRepository tokens, PostgresJobLeaseStore leases,
                    IFixStore fixes, ProjectEventNotifier projectEvents, ILoggerFactory log) =>
                {
                    if (await ResolveOrgAsync(http, tokens) is not { } orgId)
                    {
                        return TypedResults.Unauthorized();
                    }

                    var job = await leases.TryClaimAsync(orgId, DateTimeOffset.UtcNow, http.RequestAborted);
                    if (job is not null)
                    {
                        // The orchestrator writes this trail when we run the fix ourselves. A run executed
                        // on the customer's compute has to write it here, or self-hosting silently loses
                        // the record ADR-0033 requires of both deployments. Issue fixes only: a CVE bump
                        // has no audit table — its run row is the whole record (ADR-0023).
                        //
                        // Best effort, because the claim has already committed: failing the request now
                        // would answer an error for a job the caller holds regardless, and nobody would
                        // work it until the lease lapsed. A missing audit line beats a stalled fix.
                        if (job.Kind == JobKind.IssueFix)
                        {
                            await TryAuditAsync(
                                fixes, log, job.FixId, "leased",
                                new { expiresAt = job.LeaseExpiresAt }, http.RequestAborted);
                        }
                        // The run just went Running, and the change happened outside any browser, so the
                        // dashboard learns of it only through this nudge (ADR-0030).
                        await TryNotifyAsync(projectEvents, log, job.ProjectId, http.RequestAborted);
                    }

                    return job is null
                        ? TypedResults.NoContent()
                        : TypedResults.Ok(new LeasedJobResponse(
                            job.FixId, job.IssueId, job.RepoFullName, job.BaseBranch,
                            job.Prompt, job.ScopedPaths, job.LeaseExpiresAt, job.LeaseId, job.Ref));
                })
            .WithName("leaseJob").ExcludeFromDescription();

        // Extend a held lease. 409 rather than 404 when it is gone: the job exists, it is simply no longer
        // this runner's, and the runner should stop working rather than retry.
        app.MapPost("/api/runner/jobs/{fixId:guid}/heartbeat",
                async Task<Results<NoContent, Conflict, UnauthorizedHttpResult>> (
                    Guid fixId, HeartbeatRequest req, HttpContext http,
                    RunnerTokenRepository tokens, PostgresJobLeaseStore leases) =>
                {
                    if (await ResolveOrgAsync(http, tokens) is null)
                    {
                        return TypedResults.Unauthorized();
                    }

                    return await leases.TryHeartbeatAsync(
                        fixId, req.LeaseId ?? "", DateTimeOffset.UtcNow, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.Conflict();
                })
            .WithName("heartbeatJob").ExcludeFromDescription();

        // Report the outcome. Refused with the same guard, because a late report from a superseded runner
        // would otherwise overwrite what the runner that actually finished the work wrote.
        app.MapPost("/api/runner/jobs/{fixId:guid}/result",
                async Task<Results<NoContent, Conflict, UnauthorizedHttpResult>> (
                    Guid fixId, ReportRequest req, HttpContext http,
                    RunnerTokenRepository tokens, PostgresJobLeaseStore leases, IFixStore fixes,
                    IAiFixQuota quota, ProjectEventNotifier projectEvents, ILoggerFactory log) =>
                {
                    if (await ResolveOrgAsync(http, tokens) is not { } orgId)
                    {
                        return TypedResults.Unauthorized();
                    }

                    // Only a terminal status ends a run. Reporting Pending or Running would clear the
                    // lease and leave the row matching the claim again, so a runner could hand a job back
                    // to itself indefinitely, writing an audit entry each time round.
                    var reported = (FixStatus)req.Status;
                    var status = reported is FixStatus.Succeeded or FixStatus.Failed or FixStatus.Cancelled
                        ? reported
                        : FixStatus.Failed;

                    if (await leases.TryReportAsync(
                        fixId, req.LeaseId ?? "", status, req.Branch ?? "", req.PrUrl ?? "",
                        req.Summary ?? "", req.Model ?? "", req.InputTokens, req.OutputTokens,
                        DateTimeOffset.UtcNow, http.RequestAborted) is not { } concluded)
                    {
                        return TypedResults.Conflict();
                    }

                    // A run that delivered no PR stays free (ADR-0017), exactly as the hosted orchestrator
                    // refunds its failures. The lease guard above makes this once-only: a second report of
                    // the same run is refused before reaching here, so the refund cannot double. The first
                    // real runner failure burned its reservation because this was missing.
                    if (status != FixStatus.Succeeded)
                    {
                        await quota.RefundAsync(orgId, DateTimeOffset.UtcNow, http.RequestAborted);
                    }

                    // Same event names the orchestrator uses, so one issue's history reads the same whether
                    // the fix ran on our compute or the customer's. Token usage rides along, because spend
                    // metering is ours to keep even when the model call was not. Issue fixes only — a CVE
                    // bump's run row is its whole record (ADR-0023).
                    if (concluded.Kind == JobKind.IssueFix)
                    {
                        await TryAuditAsync(
                            fixes, log, fixId,
                            status == FixStatus.Succeeded ? "draft_pr_opened" : "failed",
                            new
                            {
                                prUrl = req.PrUrl ?? "",
                                summary = req.Summary ?? "",
                                inputTokens = req.InputTokens,
                                outputTokens = req.OutputTokens,
                            },
                            http.RequestAborted);
                    }

                    // The outcome landed from a machine, not a browser, so the dashboard's fixes surface
                    // only learns of it through this nudge (ADR-0030).
                    await TryNotifyAsync(projectEvents, log, concluded.ProjectId, http.RequestAborted);

                    return TypedResults.NoContent();
                })
            .WithName("reportJobResult").ExcludeFromDescription();
    }

    /// <summary>
    /// The org a presented runner token belongs to, or null. Shape is checked first, so a token of another
    /// kind sent here is refused without a database round trip.
    /// </summary>
    private static async Task<long?> ResolveOrgAsync(HttpContext http, RunnerTokenRepository tokens)
    {
        if (BearerToken.From(http) is not { } raw || !RunnerTokens.LooksLikeToken(raw))
        {
            return null;
        }

        return await tokens.ResolveOrgAsync(RunnerTokens.HashToken(raw), http.RequestAborted);
    }

    private static Task TryNotifyAsync(
        ProjectEventNotifier projectEvents, ILoggerFactory log, long projectId, CancellationToken ct) =>
        ProjectEventNudge.TrySendAsync(
            projectEvents, log.CreateLogger(nameof(RunnerEndpoints)), projectId, ct);

    /// <summary>Credited to the runner rather than a person, since a customer's machine acted. Shares the
    /// one best-effort audit write with the request path, so both trails are appended the same way.</summary>
    private static Task TryAuditAsync(
        IFixStore fixes, ILoggerFactory log, Guid fixId, string eventName, object detail, CancellationToken ct) =>
        FixAudit.TryWriteAsync(
            fixes, log.CreateLogger(nameof(RunnerEndpoints)), fixId, RunnerActor, eventName, detail, ct);

    private static RunnerTokenResponse ToResponse(ScopedTokenRow t) =>
        new(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.RevokedAt is not null);
}
