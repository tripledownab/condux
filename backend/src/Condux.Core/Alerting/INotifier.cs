namespace Condux.Core.Alerting;

/// <summary>
/// Delivers a fired alert to one channel. One implementation per <see cref="NotificationChannel"/>
/// lives in Condux.Notifications; the dispatcher routes each target to the notifier whose
/// <see cref="Channel"/> matches. Implementations must be best-effort — a delivery failure is logged,
/// never thrown into the consumer loop.
/// </summary>
public interface INotifier
{
    /// <summary>The channel this notifier delivers to.</summary>
    NotificationChannel Channel { get; }

    Task SendAsync(
        AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default);

    /// <summary>Deliver a plain subject + body message to a target string, for org-level notices that are
    /// not a fired issue alert (e.g. a Conductor pause, #130). Best-effort like <see cref="SendAsync"/>.</summary>
    Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default);
}
