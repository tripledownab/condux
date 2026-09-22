using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>Renders an <see cref="AlertNotification"/> into the fragments the notifiers share: a subject
/// plus headline, and the channel **message**, which is the channel's custom template, or the built-in
/// default when unset, with this event's values substituted (<see cref="AlertTemplate"/>). Pure, no I/O.
/// Each caller supplies the escape its own message format needs for a value it did not write.</summary>
internal static class AlertText
{
    public static string Headline(AlertNotification n) =>
        $"{EventLabel(n.EventType)} [{n.Level}] {n.Title}";

    public static string Subject(AlertNotification n) => $"[Condux] {Headline(n)}";

    // The channel's message: its custom template, or the built-in default when unset, rendered from this
    // event. escapeValue is the transport's own encoding for the substituted values, applied to each one
    // rather than to the result, so the admin's template keeps whatever markup it meant.
    public static string Message(AlertNotification n, string? template, Func<string, string> escapeValue) =>
        AlertTemplate.Render(
            string.IsNullOrWhiteSpace(template) ? AlertTemplate.Default : template, Values(n), escapeValue);

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
