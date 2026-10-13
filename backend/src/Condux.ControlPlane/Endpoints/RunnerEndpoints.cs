using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.Quotas;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Customer-hosted runners (ADR-0033 slice 4, #68): the lease surface a runner itself calls, authenticated
/// by its token alone with no cookie. The dashboard's management of those tokens is
/// <see cref="RunnerTokenEndpoints"/>. This surface is deliberately tiny and additive-only: a customer's
/// runner lags our deploys, so a field added here must never be required of an older runner, and one
/// removed breaks every runner at once.
/// </summary>
internal static class RunnerEndpoints
{
    /// <summary>Who the audit trail credits a runner-executed step to. Distinct from a user, because a
    /// customer's runner acted rather than a person, and an operator reading the history should see that.
    /// </summary>
    private const string RunnerActor = "runner";

    public static void MapRunnerEndpoints(this IEndpointRouteBuilder app)
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
                    ProjectEventNotifier projectEvents, ILoggerFactory log) =>
                {
                    // The report is tied to its run by the lease id, which TryReportAsync checks.
                    if (await ResolveOrgAsync(http, tokens) is null)
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

                    // The reason comes from outside, so it is narrowed like the status: only a failure
                    // carries one, and a number this version does not know reads as Unknown. An older
                    // runner sends none, which is Unknown too.
                    FixFailureReason? reason = status != FixStatus.Failed ? null
                        : req.FailureReason is { } sent && Enum.IsDefined((FixFailureReason)sent)
                            ? (FixFailureReason)sent
                            : FixFailureReason.Unknown;

                    // A failure's summary is the runner's error text, and the run's summary is shown to
                    // every member, so it is kept for the audit trail below and not stored on the run. The
                    // reason is what the dashboard explains. A hosted failure writes no summary either.
                    var summary = status == FixStatus.Succeeded ? req.Summary ?? "" : "";
                    if (status != FixStatus.Succeeded)
                    {
                        // A CVE bump writes no audit, so this log line is the only place its runner's
                        // error text is kept.
                        log.CreateLogger("Condux.Runner").LogWarning(
                            "runner reported {FixId} {Status}: {Summary}", fixId, status, req.Summary);
                    }

                    if (await leases.TryReportAsync(
                        fixId, req.LeaseId ?? "", status, req.Branch ?? "", req.PrUrl ?? "",
                        summary, req.Model ?? "", req.InputTokens, req.OutputTokens,
                        DateTimeOffset.UtcNow, http.RequestAborted, reason) is not { } concluded)
                    {
                        return TypedResults.Conflict();
                    }

                    // A run that delivered no PR stays free (ADR-0017): the report refunded it in the same
                    // transaction, and the lease guard refuses a second report of the same run, so the refund
                    // cannot double.

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
}
