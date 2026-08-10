using Condux.Core.Alerting;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;

namespace Condux.Notifications;

/// <summary>
/// The alert engine (#55). For an issue event it loads the project's enabled
/// rules, fires the ones that match (<see cref="AlertEvaluator"/>), and delivers to each matching rule's
/// channels via the <see cref="INotifier"/> registered for that channel. Wholly best-effort: a failure
/// to load rules or deliver a single notification is logged, never thrown — alerting must never disrupt
/// the ingest consumer that drives it.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly AlertRuleRepository _rules;
    private readonly ProjectRepository _projects;
    private readonly IReadOnlyDictionary<NotificationChannel, INotifier> _notifiers;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        AlertRuleRepository rules, ProjectRepository projects, IEnumerable<INotifier> notifiers,
        ILogger<AlertDispatcher> logger)
    {
        _rules = rules;
        _projects = projects;
        // One notifier per channel; a channel with no registered notifier is skipped (logged) at delivery.
        _notifiers = notifiers.ToDictionary(notifier => notifier.Channel);
        _logger = logger;
    }

    public async Task DispatchAsync(
        long projectId, AlertEventType eventType, AlertNotification notification,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rules = await _rules.ListEnabledWithChannelsByProjectAsync(projectId, cancellationToken);
            // Enrich with the project's display name; the internal bigint id is never shown to a recipient.
            var enriched = notification with
            {
                ProjectName = (await _projects.GetAsync(projectId, cancellationToken))?.Name ?? "",
            };
            // Dedupe per destination: several matching rules can share a channel (same transport + target),
            // and overlapping level sets mean a destination must never get the same alert twice per event.
            var delivered = new HashSet<(NotificationChannel Channel, string Target)>();
            foreach (var ruleWithChannels in rules)
            {
                if (!AlertEvaluator.Matches(ruleWithChannels.Rule, eventType, notification.Level))
                {
                    continue;
                }
                foreach (var channel in ruleWithChannels.Channels)
                {
                    if (delivered.Add((channel.Channel, channel.Target)))
                    {
                        await DeliverAsync(channel, enriched, cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "alert dispatch failed for project {ProjectId}", projectId);
        }
    }

    private async Task DeliverAsync(
        AlertChannel channel, AlertNotification notification, CancellationToken cancellationToken)
    {
        if (!_notifiers.TryGetValue(channel.Channel, out var notifier))
        {
            _logger.LogWarning("no notifier registered for channel {Channel}", channel.Channel);
            return;
        }
        try
        {
            await notifier.SendAsync(channel, notification, cancellationToken);
            _logger.LogInformation(
                "alert delivered project={ProjectId} channel={Channel}", notification.ProjectId, channel.Channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "alert delivery failed channel={Channel}", channel.Channel);
        }
    }
}
