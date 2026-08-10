using Condux.Core.WeeklySummaries;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Consumer;

/// <summary>
/// The weekly summary email worker (ADR-0031). Wakes hourly and, for each org whose digest is enabled, claims
/// the current week in the ledger (an atomic guarded insert). Winning the claim — which happens on the first
/// tick at or after the org's scheduled local send time, exactly once per week even across restarts and
/// replicas — it composes the digest and emails every member. A dormant org (zero events all week) is skipped.
/// Resilient: a failing org is logged and self-reported, never stopping the others or the tick.
/// </summary>
public sealed class WeeklySummaryWorker(
    ILogger<WeeklySummaryWorker> logger,
    IServiceScopeFactory scopes,
    OrgRepository orgs,
    OrgMemberRepository members,
    PostgresWeeklySummaryLedger ledger,
    WeeklySummaryMailer mailer,
    ConduxSelfReporter selfReport)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // How stale a scheduled send may be and still fire. Comfortably exceeds the tick interval (so a tick
    // landing shortly after the send hour still qualifies) while rejecting a days-old week — that keeps a
    // deploy or a mid-week enable from immediately blasting the previous week off-schedule (ADR-0031).
    private static readonly TimeSpan MaxSendDelay = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        logger.LogInformation("weekly summary worker started interval={Interval}", Interval);

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
                logger.LogWarning(ex, "weekly summary tick error");
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
        var enabled = await orgs.ListWeeklySummaryEnabledAsync(ct);
        if (enabled.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // The composer resolves transient ClickHouse readers; a per-tick scope avoids a captive dependency in
        // this singleton (mirrors VerificationWorker).
        using var scope = scopes.CreateScope();
        var composer = scope.ServiceProvider.GetRequiredService<WeeklySummaryComposer>();

        foreach (var org in enabled)
        {
            try
            {
                await SendIfDueAsync(composer, org, now, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "weekly summary failed org={OrgId}", org.Id);
                selfReport.Report(ex);
            }
        }
    }

    // Claim first (a cheap single upsert): only the winning tick composes and sends, so the heavy aggregation
    // runs at most once per org per week and concurrent ticks/replicas cannot double-send. A dormant org keeps
    // its claim (it was evaluated, nothing to send); a transient failure releases the claim so the next tick
    // retries the week rather than losing it.
    private async Task SendIfDueAsync(
        WeeklySummaryComposer composer, WeeklySummaryOrg org, DateTimeOffset now, CancellationToken ct)
    {
        if (WeeklySchedule.DueSendInstant(now, org.Dow, org.Hour, org.Tz, MaxSendDelay) is not { } sendInstant)
        {
            return; // no scheduled send is due within the catch-up window (e.g. a fresh deploy or mid-week enable)
        }

        var weekStart = DateOnly.FromDateTime(sendInstant.UtcDateTime);
        if (!await ledger.TryClaimAsync(org.Id, weekStart, now, ct))
        {
            return; // already handled this week (this tick, an earlier one, or another replica)
        }

        try
        {
            var summary = await composer.ComposeAsync(org.Id, org.Name, sendInstant, ct);
            if (!summary.HadActivity)
            {
                logger.LogInformation("weekly summary skipped (no activity) org={OrgId}", org.Id);
                return; // an evaluated empty week keeps its claim — do not recompute it every tick
            }

            var recipients = (await members.ListByOrgAsync(org.Id, ct)).Select(m => m.Email).ToList();
            var sent = await mailer.SendAsync(summary, recipients, ct);
            logger.LogInformation(
                "weekly summary sent org={OrgId} recipients={Recipients} events={Events}", org.Id, sent, summary.Events);
        }
        catch
        {
            // Release the claim so a transient error (e.g. ClickHouse blip before any email went out) is retried
            // next tick instead of silently dropping the week; then let the caller log + self-report.
            await ledger.ReleaseAsync(org.Id, weekStart, ct);
            throw;
        }
    }
}
