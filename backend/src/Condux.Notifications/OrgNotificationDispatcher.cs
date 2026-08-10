using Condux.Core.Alerting;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;

namespace Condux.Notifications;

/// <summary>
/// Delivers a plain org-level notice (subject + body) to every channel an org configured (#129), routing
/// each to the <see cref="INotifier"/> for its channel via <see cref="INotifier.SendMessageAsync"/>. The
/// org analogue of <see cref="AlertDispatcher"/> — wholly best-effort: a load failure or a single failed
/// delivery is logged, never thrown into the caller (the ingest consumer).
/// </summary>
public sealed class OrgNotificationDispatcher
{
    private readonly OrgNotificationChannelRepository _channels;
    private readonly IReadOnlyDictionary<NotificationChannel, INotifier> _notifiers;
    private readonly ILogger<OrgNotificationDispatcher> _logger;

    public OrgNotificationDispatcher(
        OrgNotificationChannelRepository channels, IEnumerable<INotifier> notifiers,
        ILogger<OrgNotificationDispatcher> logger)
    {
        _channels = channels;
        _notifiers = notifiers.ToDictionary(notifier => notifier.Channel);
        _logger = logger;
    }

    /// <summary>Fans the notice to every channel the org configured; returns how many actually delivered
    /// (0 when the org has no channels, none has a registered notifier, or every send failed). Best-effort:
    /// individual failures are logged, never thrown.</summary>
    public async Task<int> DispatchAsync(
        long orgId, string subject, string body, CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        try
        {
            var channels = await _channels.ListByOrgAsync(orgId, cancellationToken);
            foreach (var channel in channels)
            {
                if (!_notifiers.TryGetValue(channel.Channel, out var notifier))
                {
                    _logger.LogWarning("no notifier registered for channel {Channel}", channel.Channel);
                    continue;
                }
                try
                {
                    await notifier.SendMessageAsync(channel.Target, subject, body, cancellationToken);
                    delivered++;
                    _logger.LogInformation(
                        "org notice delivered org={OrgId} channel={Channel}", orgId, channel.Channel);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "org notice delivery failed org={OrgId} channel={Channel}", orgId, channel.Channel);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "org notice dispatch failed org={OrgId}", orgId);
        }
        return delivered;
    }
}
