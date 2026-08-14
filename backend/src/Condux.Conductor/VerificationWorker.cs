using System.Globalization;
using System.Text.Json;
using Condux.Core.FixEngine;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Conductor;

/// <summary>
/// The fix-verification watcher (ADR-0019): periodically checks every merged, watching fix against
/// the issue's exact hourly stats. A silent 72h window resolves the issue and records the evidence;
/// any occurrence marks the fix as not holding, leaving the issue open. The evaluation itself is the
/// pure <see cref="FixVerification"/> policy; this worker is only the clock and the plumbing.
/// Resilient — a failing tick is logged and retried on the next interval.
/// </summary>
public sealed class VerificationWorker(
    IConfiguration config, ILogger<VerificationWorker> logger, IServiceScopeFactory scopes,
    PostgresFixVerification verification, IssueRepository issues, IFixStore fixes,
    ProjectEventNotifier projectEvents, ConduxSelfReporter selfReport)
    : BackgroundService
{
    private const int ResolvedStatus = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            int.TryParse(config["CONDUX_VERIFY_INTERVAL_SECONDS"], out var seconds) ? seconds : 600);
        using var timer = new PeriodicTimer(interval);
        logger.LogInformation("verification watcher started interval={Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "verification tick error");
                selfReport.Report(ex); // Condux on Condux (#75)
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var watching = await verification.ListWatchingAsync(ct);
        if (watching.Count == 0)
        {
            return;
        }

        // The stats reader is a typed HttpClient (transient); a per-tick scope avoids holding a
        // captive handler in this singleton.
        using var scope = scopes.CreateScope();
        var stats = scope.ServiceProvider.GetRequiredService<ClickHouseIssueStatsReader>();
        await ConcludeAsync(watching, stats, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>Evaluates each watching fix against its post-merge ClickHouse count and records the
    /// conclusion (held → resolve + evidence, did-not-hold → audit). The clock is a parameter so the
    /// 72h window is deterministic under test; internal for the integration test to drive a real tick.</summary>
    internal async Task ConcludeAsync(
        IReadOnlyList<WatchedFix> watching, ClickHouseIssueStatsReader stats, DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (var fix in watching)
        {
            var occurrences = await stats.CountSinceAsync(
                fix.ProjectId.ToString(CultureInfo.InvariantCulture), (ulong)fix.IssueId, fix.MergedAt, ct);
            switch (FixVerification.Evaluate(fix.MergedAt, now, occurrences))
            {
                case VerifyStatus.Held:
                    await ConcludeHeldAsync(fix, now, ct);
                    break;
                case VerifyStatus.DidNotHold:
                    await ConcludeDidNotHoldAsync(fix, occurrences, now, ct);
                    break;
            }
        }
    }

    private async Task ConcludeHeldAsync(WatchedFix fix, DateTimeOffset now, CancellationToken ct)
    {
        if (!await verification.SetVerifyStatusAsync(fix.FixId, VerifyStatus.Held, now, ct))
        {
            return;
        }

        var resolved = await issues.UpdateStatusAsync(fix.ProjectId, fix.IssuePublicId, ResolvedStatus, ct);
        var detail = JsonSerializer.Serialize(new
        {
            occurrences = 0,
            windowHours = (int)FixVerification.Window.TotalHours,
            mergedAt = fix.MergedAt,
            resolved,
        });
        await fixes.AppendAuditAsync(fix.FixId, "conductor", "fix_verified", detail, ct);
        logger.LogInformation("fix held id={FixId} issue={IssueId} resolved={Resolved}",
            fix.FixId, fix.IssueId, resolved);
        // The verdict resolved an issue and concluded a fix with nobody watching; nudge the project's
        // open dashboards so both surfaces refetch (ADR-0030).
        await ProjectEventNudge.TrySendAsync(projectEvents, logger, fix.ProjectId, ct);
    }

    private async Task ConcludeDidNotHoldAsync(
        WatchedFix fix, long occurrences, DateTimeOffset now, CancellationToken ct)
    {
        if (!await verification.SetVerifyStatusAsync(fix.FixId, VerifyStatus.DidNotHold, now, ct))
        {
            return;
        }

        var detail = JsonSerializer.Serialize(new { occurrences, mergedAt = fix.MergedAt });
        await fixes.AppendAuditAsync(fix.FixId, "conductor", "fix_did_not_hold", detail, ct);
        logger.LogInformation("fix did not hold id={FixId} issue={IssueId} occurrences={Occurrences}",
            fix.FixId, fix.IssueId, occurrences);
        await ProjectEventNudge.TrySendAsync(projectEvents, logger, fix.ProjectId, ct);
    }
}
