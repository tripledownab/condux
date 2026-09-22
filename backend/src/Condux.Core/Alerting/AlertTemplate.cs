using System.Text.RegularExpressions;

namespace Condux.Core.Alerting;

/// <summary>
/// Renders a user-authored alert message template: plain text with <c>{{token}}</c> placeholders
/// substituted from an event's values. Pure (no I/O), so it is unit-tested and runs identically in the
/// consumer and the control-plane. Substitution is **single-pass**: a substituted value that itself
/// contains <c>{{...}}</c> is never re-expanded, so an untrusted error title cannot inject another token.
///
/// The template and the values have different authors, and that difference is the whole reason the
/// escape belongs here. An admin wrote the template and may have meant its punctuation as markup, while
/// the values carry an error title chosen by whoever could send the event. Once the two are one string
/// nothing can tell them apart, so a transport escaping the rendered result has to choose between
/// breaking the admin's markup and passing the attacker's. Escaping each value as it is substituted is
/// the only point where both can be right.
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

    /// <summary>The escape for a transport whose message carries no markup of its own, so a value needs
    /// no encoding to be shown as written. Named rather than a lambda so a call site states the choice
    /// and the next reader can find every transport that made it.</summary>
    public static string NoEscape(string value) => value;

    /// <summary>Substitute every <c>{{token}}</c> from <paramref name="values"/> in one pass, passing each
    /// value through <paramref name="escapeValue"/> on the way in. An unknown token renders empty (the API
    /// rejects those at save via <see cref="UnknownTokens"/>, so this is only a defensive fallback).
    ///
    /// <paramref name="escapeValue"/> has no default on purpose. Every transport must say what its own
    /// message format does to a value it did not write, and a default would let the next one inherit an
    /// answer nobody chose for it.</summary>
    public static string Render(
        string template, IReadOnlyDictionary<string, string> values, Func<string, string> escapeValue) =>
        TokenPattern.Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? escapeValue(value) : "");

    /// <summary>The distinct tokens a template uses that are not <see cref="KnownTokens"/>; empty = valid.</summary>
    public static IReadOnlyList<string> UnknownTokens(string template) =>
    [
        .. TokenPattern.Matches(template)
            .Select(match => match.Groups[1].Value)
            .Where(name => !KnownTokens.Contains(name))
            .Distinct(StringComparer.Ordinal),
    ];
}
