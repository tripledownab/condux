using Condux.Core.Events;

namespace Condux.Core.Alerting;

/// <summary>The kind of issue event a rule can fire on. Values match the wire/DB representation.</summary>
public enum AlertEventType
{
    NewIssue = 1,
    Regression = 2,
    Resolved = 3,
    Assigned = 4,
}

/// <summary>Where a fired alert is delivered. Values match <c>alert_channels.channel</c>.</summary>
public enum NotificationChannel
{
    Email = 1,
    Slack = 2,
    Webhook = 3,
    Discord = 4,
}

/// <summary>An alert rule for a project: fires on any of its selected <see cref="Events"/> (new issue,
/// regression, resolved, assigned) whose level is one of the rule's selected <see cref="Levels"/> (an
/// exact set, not a >= threshold).</summary>
public sealed record AlertRule(
    Guid Id, long ProjectId, string Name, IReadOnlyList<AlertEventType> Events,
    IReadOnlyList<Level> Levels, bool Enabled);

/// <summary>A delivery target for a rule (an email address or a Slack/webhook/Discord URL) plus an optional
/// custom message template (<see cref="AlertTemplate"/>); null uses the built-in default.</summary>
public sealed record AlertChannel(
    Guid Id, Guid RuleId, NotificationChannel Channel, string Target, string? Template = null);

/// <summary>A rule together with its channels, as the engine loads it to dispatch a fired alert.</summary>
public sealed record AlertRuleWithChannels(AlertRule Rule, IReadOnlyList<AlertChannel> Channels);

/// <summary>A fired alert: the issue that changed and why, ready to render into a notification. ProjectId is
/// the internal bigint (used only to load rules + for server logs, never shown to a recipient); ProjectName
/// is the display name the dispatcher fills in and the notifiers render (the bigint never leaves the server).</summary>
public sealed record AlertNotification(
    long ProjectId, Guid IssuePublicId, string Title, string Culprit, Level Level, AlertEventType EventType,
    string ProjectName = "");
