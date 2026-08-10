using System.Text.RegularExpressions;

namespace Condux.Core.Alerting;

/// <summary>
/// Renders a user-authored alert message template: plain text with <c>{{token}}</c> placeholders
/// substituted from an event's values. Pure (no I/O), so it is unit-tested and runs identically in the
/// consumer and the control-plane. Substitution is **single-pass** — a substituted value that itself
/// contains <c>{{...}}</c> is never re-expanded, so an untrusted error title cannot inject another token.
/// Per-transport encoding (HTML for email, JSON for the rest) stays the notifier's job; this only produces
/// the plain-text message.
/// </summary>
public static class AlertTemplate
{
    // The placeholders a template may use; each maps to an AlertNotification field (see AlertText.Values).
    public static readonly IReadOnlySet<string> KnownTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "culprit", "level", "event", "project", "issue",
    };

    // The built-in default message, used when a channel has no custom template (users can restore it).
    public const string Default = "{{event}} [{{level}}] {{title}}\n{{culprit}}";

    private static readonly Regex TokenPattern = new(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Substitute every <c>{{token}}</c> from <paramref name="values"/> in one pass. An unknown
    /// token renders empty (the API rejects those at save via <see cref="UnknownTokens"/>, so this is only a
    /// defensive fallback).</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        TokenPattern.Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : "");

    /// <summary>The distinct tokens a template uses that are not <see cref="KnownTokens"/>; empty = valid.</summary>
    public static IReadOnlyList<string> UnknownTokens(string template) =>
    [
        .. TokenPattern.Matches(template)
            .Select(match => match.Groups[1].Value)
            .Where(name => !KnownTokens.Contains(name))
            .Distinct(StringComparer.Ordinal),
    ];
}
