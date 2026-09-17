using Condux.Core.Auth;
using Condux.Core.OrgNotifications;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Condux.Telemetry;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Re-reads the DNS record behind every proved SSO domain claim, and takes the claim away when the record
/// has been gone long enough (ADR-0043 slice 2). A claim true in March should not still route in December.
///
/// In the control-plane rather than the consumer, which already hosts the other timers: SSO config is a
/// control-plane domain, and splitting it across services to reuse a timer would be the wrong seam.
///
/// Wakes hourly and re-checks each claim about daily. The hour is not the schedule, the claim is: a row
/// carries when it was last checked and the store hands out one due row at a time, so a restart neither
/// loses a day nor re-reads everything, and every replica can run this loop without duplicating a check or
/// a notice.
///
/// It goes through <see cref="SsoDomainVerifier"/>, never the resolver, so a deployment that has answered
/// the check with CONDUX_SSO_SKIP_DOMAIN_VERIFICATION keeps its claims. Reaching for the resolver here
/// would take SSO away from exactly the self-hosted installation the opt-out exists for.
/// </summary>
public sealed class SsoVerificationWorker(
    IServiceScopeFactory scopes,
    PostgresSsoConfigStore store,
    OrgRepository orgs,
    OrgNotificationDispatcher notifications,
    ILogger<SsoVerificationWorker> logger,
    ConduxSelfReporter selfReport)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>How stale a claim's last check may be before it is re-read. Daily, per the ADR.</summary>
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(24);

    // A pass walks due claims until there are none, so this bounds one pass and not the work: what it does
    // not reach is due again an hour later, which at 24 passes a day is twelve thousand claims. It is
    // logged when reached, because a cap nobody sees reads as "everything was checked". It is also the
    // backstop that turned a claim which could re-select the same row into a slow test rather than a hang.
    private const int MaxPerPass = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        logger.LogInformation(
            "sso verification worker started interval={Interval} recheck={Recheck} grace={Grace}",
            Interval, RecheckAfter, DomainVerification.VerificationGrace);

        // Waits before the first tick, where the other workers in this tree work first. Two reasons, and
        // both are about the deploy rather than the check: replicas start together, so working first sends
        // every one of them at the resolver at the same moment, and the check is daily, so an hour's delay
        // costs nothing. It also keeps the worker out of a test's way, since an app that lives seconds
        // never reaches its first tick.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "sso verification tick error");
                selfReport.Report(ex); // Condux on Condux (#75)
            }
        }
    }

    /// <summary>
    /// One pass over the claims due a re-check. Public and taking the instant rather than reading the
    /// clock, because that is what makes the grace period testable: a test runs a pass eight days on
    /// without waiting eight days, and a single read per pass means the claim window and the lapse cutoff
    /// cannot be computed from two different instants.
    /// </summary>
    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        // No plan gate, unlike the Verify endpoint beside it. That one can turn routing ON, so an org that
        // dropped off an SSO tier must not reach it; this pass can only ever leave a claim alone or take
        // it away, so gating it would protect nothing and would freeze a downgraded org's stale claim.
        //
        // The verifier is scoped (it holds a typed HttpClient), so a per-pass scope keeps this singleton
        // from capturing one for the process lifetime, as WeeklySummaryWorker does for its composer.
        using var scope = scopes.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<SsoDomainVerifier>();

        var checkedCount = 0;
        while (checkedCount < MaxPerPass && !ct.IsCancellationRequested)
        {
            if (await store.ClaimDueRecheckAsync(now, RecheckAfter, ct) is not { } config)
            {
                return;
            }
            checkedCount++;

            try
            {
                await RecheckAsync(verifier, config, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One claim's failure must not stop the rest. The row is already stamped, so it simply
                // waits for tomorrow rather than being retried in a loop.
                logger.LogWarning(ex, "sso verification re-check failed org={OrgId}", config.OrgId);
                selfReport.Report(ex);
            }
        }

        if (checkedCount >= MaxPerPass)
        {
            // "Any remaining" rather than "the rest", because a pass that checked exactly this many may
            // have finished the work. Claiming otherwise would be a log line that is sometimes false.
            logger.LogWarning(
                "sso verification pass stopped at its ceiling of {Max}; any remaining claims are due next "
                + "pass", MaxPerPass);
        }
    }

    private async Task RecheckAsync(
        SsoDomainVerifier verifier, StoredSsoConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = await verifier.CheckAsync(config, ct);
        if (outcome == DomainCheckOutcome.ResolverUnavailable)
        {
            // Our failure, not theirs. It must not start or advance a grace period, or our own DNS outage
            // would take a customer's single sign-on away and tell them their records were wrong.
            logger.LogWarning(
                "sso domain re-check could not reach a resolver org={OrgId} domain={Domain}",
                config.OrgId, config.EmailDomain);
            return;
        }

        if (outcome == DomainCheckOutcome.Verified)
        {
            if (await store.ClearVerificationFailureAsync(config.OrgId, config.EmailDomain, ct))
            {
                logger.LogInformation(
                    "sso domain record is back org={OrgId} domain={Domain}", config.OrgId, config.EmailDomain);
            }
            return;
        }

        // The record is gone. Asked in this order so the two questions read as one: has the grace period
        // run out, and if not, is this where it starts. Either answer is a guarded UPDATE, so exactly one
        // replica gets it and the org is told once.
        //
        // The write goes BEFORE the notice, which is the opposite of the usual ordering, and on purpose:
        // that write IS the throttle. Notifying first would mean either telling an org repeatedly that
        // its SSO is about to lapse, or keeping a second piece of state to stop that. So a failed notice
        // loses a message rather than a fact, which the dashboard still shows and the log still records.
        if (await store.LapseVerificationAsync(config.OrgId, config.EmailDomain, now, ct) is { } lostAt)
        {
            logger.LogWarning(
                "sso domain verification lapsed org={OrgId} domain={Domain} missingSince={LostAt}",
                config.OrgId, config.EmailDomain, lostAt);
            await NotifyAsync(config, SsoDomainText.VerificationLost, lostAt, ct);
            return;
        }

        if (await store.RecordVerificationFailureAsync(config.OrgId, config.EmailDomain, now, ct) is { } startedAt)
        {
            logger.LogWarning(
                "sso domain record missing org={OrgId} domain={Domain} lapsesAt={LapsesAt}",
                config.OrgId, config.EmailDomain, DomainVerification.LapsesAt(startedAt));
            await NotifyAsync(config, SsoDomainText.RecordMissing, startedAt, ct);
        }

        // Neither matched: the claim is inside its grace period and the org has already been told.
    }

    private async Task NotifyAsync(
        StoredSsoConfig config, Func<string, string, DateTimeOffset, OrgNotice> text, DateTimeOffset lostAt,
        CancellationToken ct)
    {
        if (await orgs.GetAsync(config.OrgId, ct) is not { } org)
        {
            // The state change stands; only the notice is lost. Worth a line rather than a silent skip:
            // an sso_configs row whose org is gone should be impossible under the FK.
            logger.LogWarning("sso domain notice skipped, org not found org={OrgId}", config.OrgId);
            return;
        }

        var notice = text(org.Name, config.EmailDomain, lostAt);
        var delivered = await notifications.DispatchAsync(config.OrgId, notice.Subject, notice.Body, ct);
        if (delivered == 0)
        {
            // Said out loud because the state change is one-way from here: the transition that owed this
            // notice has already been recorded, so nothing will try again. An org with no notification
            // channels finds out from the dashboard instead.
            logger.LogWarning(
                "sso domain notice reached no channels org={OrgId} domain={Domain}",
                config.OrgId, config.EmailDomain);
        }
    }
}
