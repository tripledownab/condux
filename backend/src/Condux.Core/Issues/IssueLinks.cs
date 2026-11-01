namespace Condux.Core.Issues;

/// <summary>Links into the dashboard for an issue, built once so every place that sends one out (the weekly
/// summary's two bodies, a tracker ticket) points at the same page.</summary>
public static class IssueLinks
{
    /// <summary>Absolute link to one issue, or null when no dashboard base URL is configured. Null means the
    /// caller omits the link rather than emitting a relative path, which is useless outside the dashboard.</summary>
    public static string? ToIssueUrl(string? dashboardBaseUrl, Guid issueId) =>
        string.IsNullOrWhiteSpace(dashboardBaseUrl)
            ? null
            : $"{dashboardBaseUrl.TrimEnd('/')}/issues/{issueId}";
}
