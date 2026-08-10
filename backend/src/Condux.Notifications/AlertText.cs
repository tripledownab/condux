using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>Renders an <see cref="AlertNotification"/> into the fragments the notifiers share: a subject +
/// headline, and the channel **message** — the channel's custom template, or the built-in default when
/// unset, with this event's values substituted (<see cref="AlertTemplate"/>). Pure — no I/O.
/// Per-transport encoding (HTML for email, JSON for the rest) stays the notifier's job.</summary>
internal static class AlertText
{
    public static string Headline(AlertNotification n) =>
        $"{EventLabel(n.EventType)} [{n.Level}] {n.Title}";

    public static string Subject(AlertNotification n) => $"[Condux] {Headline(n)}";

    // The channel's message: its custom template, or the built-in default when unset, rendered from this event.
    public static string Message(AlertNotification n, string? template) =>
        AlertTemplate.Render(
            string.IsNullOrWhiteSpace(template) ? AlertTemplate.Default : template, Values(n));

    public static string EventLabel(AlertEventType eventType) => eventType switch
    {
        AlertEventType.NewIssue => "New issue",
        AlertEventType.Regression => "Regression",
        AlertEventType.Resolved => "Resolved",
        AlertEventType.Assigned => "Assigned",
        _ => "Alert",
    };

    // The template token values for an event (keys are AlertTemplate.KnownTokens).
    private static IReadOnlyDictionary<string, string> Values(AlertNotification n) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = n.Title,
            ["culprit"] = n.Culprit,
            ["level"] = n.Level.ToString(),
            ["event"] = EventLabel(n.EventType),
            ["project"] = n.ProjectName,
            ["issue"] = n.IssuePublicId.ToString(),
        };
}
