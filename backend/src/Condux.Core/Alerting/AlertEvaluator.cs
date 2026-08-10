using Condux.Core.Events;

namespace Condux.Core.Alerting;

/// <summary>
/// Decides whether an issue event fires a rule. Pure (no I/O), so the alerting path is unit-tested in
/// isolation from Postgres and the notifiers.
/// </summary>
public static class AlertEvaluator
{
    /// <summary>
    /// True when <paramref name="rule"/> should fire for an issue event: the rule is enabled, the issue's
    /// <paramref name="level"/> is one of the rule's selected levels, and the rule subscribes to the
    /// <paramref name="eventType"/> that occurred (new issue, regression, resolved or assigned).
    /// </summary>
    public static bool Matches(AlertRule rule, AlertEventType eventType, Level level) =>
        rule.Enabled
        && rule.Levels.Contains(level)
        && rule.Events.Contains(eventType);
}
