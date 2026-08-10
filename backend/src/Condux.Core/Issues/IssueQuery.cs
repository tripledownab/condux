using System.Globalization;

namespace Condux.Core.Issues;

/// <summary>
/// The parsed issue-search filter. Null members are "not filtered". <see cref="Text"/> is free text
/// matched against title + culprit; <see cref="AssignedToMe"/> narrows to the current user's issues.
/// </summary>
public sealed record IssueFilter(
    string? Text,
    int? Level,
    int? Status,
    bool? Assigned,
    bool AssignedToMe);

/// <summary>
/// Parses the Sentry-style issue query language into an <see cref="IssueFilter"/> the list query applies
/// in SQL. This is the single source of the grammar (the server is authoritative for the list); the
/// dashboard keeps only a light parse for highlighting the active view. Tokens: <c>level:&lt;name&gt;</c>,
/// <c>is:&lt;status&gt;</c>, <c>is:assigned</c> / <c>is:unassigned</c>, <c>assigned:me</c>; the rest is text.
/// </summary>
public static class IssueQuery
{
    private static readonly Dictionary<string, int> LevelByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = 1,
        ["info"] = 2,
        ["warning"] = 3,
        ["error"] = 4,
        ["fatal"] = 5,
    };

    private static readonly Dictionary<string, int> StatusByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unresolved"] = 1,
        ["resolved"] = 2,
        ["ignored"] = 3,
    };

    public static IssueFilter Parse(string? raw)
    {
        string? text = null;
        int? level = null;
        int? status = null;
        bool? assigned = null;
        var assignedToMe = false;
        var terms = new List<string>();

        foreach (var token in (raw ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                terms.Add(token);
                continue;
            }
            var key = token[..colon];
            var value = token[(colon + 1)..];
            switch (key.ToLower(CultureInfo.InvariantCulture))
            {
                case "level" when LevelByName.TryGetValue(value, out var l):
                    level = l;
                    break;
                case "is" when StatusByName.TryGetValue(value, out var s):
                    status = s;
                    break;
                case "is" when value.Equals("assigned", StringComparison.OrdinalIgnoreCase):
                    assigned = true;
                    break;
                case "is" when value.Equals("unassigned", StringComparison.OrdinalIgnoreCase):
                    assigned = false;
                    break;
                case "assigned" when value.Equals("me", StringComparison.OrdinalIgnoreCase):
                    assignedToMe = true;
                    break;
                default:
                    terms.Add(token); // an unrecognized token is treated as free text, not silently dropped
                    break;
            }
        }

        if (terms.Count > 0)
        {
            text = string.Join(' ', terms);
        }
        return new IssueFilter(text, level, status, assigned, assignedToMe);
    }
}
