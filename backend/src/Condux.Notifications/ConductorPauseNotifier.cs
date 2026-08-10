using Condux.Core.OrgNotifications;
using Microsoft.Extensions.Logging;

namespace Condux.Notifications;

/// <summary>
/// Tells an org, once, that the Conductor stopped auto-requesting fixes because its cost cap or monthly
/// allowance is reached (#130). The single collaborator the auto-fix path calls at each pause: it applies
/// the throttle (at most once per org per reason per window), renders the notice, and dispatches to the
/// org's channels. Wholly best-effort — a failure here must never disrupt the ingest consumer.
/// </summary>
public sealed class ConductorPauseNotifier(
    IPauseNotifyThrottle throttle, OrgNotificationDispatcher dispatcher, ILogger<ConductorPauseNotifier> logger)
{
    // Notify at most once per org per reason per day, so a persistent pause (dropped on every new error
    // while capped) doesn't spam. The reset (cap raised / month rollover) starts the window fresh.
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public async Task NotifyAsync(
        long orgId, string orgName, ConductorPauseReason reason, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await throttle.TryAcquireAsync(orgId, reason, DateTimeOffset.UtcNow, Window, cancellationToken))
            {
                return; // already notified for this reason within the window
            }
            var delivered = await dispatcher.DispatchAsync(
                orgId, ConductorPauseText.Subject(reason), ConductorPauseText.Body(reason, orgName),
                cancellationToken);
            if (delivered > 0)
            {
                logger.LogInformation(
                    "conductor pause notice delivered org={OrgId} reason={Reason} channels={Channels}",
                    orgId, reason, delivered);
            }
            else
            {
                // The throttle window is already consumed, so this reason won't retry for 24h even though it
                // reached nobody — surface that honestly instead of the old misleading "sent" line.
                logger.LogWarning(
                    "conductor pause notice reached no channels org={OrgId} reason={Reason}", orgId, reason);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "conductor pause notify failed org={OrgId} reason={Reason}", orgId, reason);
        }
    }
}
