using Condux.Core.Alerting;

namespace Condux.Core.OrgNotifications;

/// <summary>
/// An org-level delivery target for operational notices (#129) — an email address or a Slack/webhook
/// URL. Distinct from an <see cref="AlertChannel"/>, which is scoped to a project's alert rule (#59):
/// these belong to the org and carry platform notices that are not about a single issue (today: a
/// Conductor pause when the AI-fix cost cap or monthly allowance is reached in auto mode). Reuses the
/// same <see cref="NotificationChannel"/> transport enum so the same notifiers deliver both.
/// </summary>
public sealed record OrgNotificationChannel(
    Guid Id, long OrgId, NotificationChannel Channel, string Target, DateTimeOffset CreatedAt);
