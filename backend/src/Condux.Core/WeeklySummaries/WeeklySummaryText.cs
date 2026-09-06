using System.Text;

namespace Condux.Core.WeeklySummaries;

/// <summary>Builds the plain-text subject and body of the weekly summary email (ADR-0031). Pure (no HTML, no
/// I/O), the text/plain source of truth; the title, numbers, and metric rows come from the shared
/// <see cref="WeeklySummaryFormat"/> so this and the HTML view never diverge. Mirrors <c>ConductorPauseText</c>.</summary>
public static class WeeklySummaryText
{
    public static string Subject(WeeklySummary s) => WeeklySummaryFormat.Title(s);

    public static string Body(WeeklySummary s, string? dashboardUrl = null)
    {
        var sb = new StringBuilder();
        sb.Append($"Your Condux week for {s.OrgName}\n");
        sb.Append($"{WeeklySummaryFormat.DateRange(s)}\n\n");
        sb.Append($"Events this week: {WeeklySummaryFormat.Number(s.Events)}{WeeklySummaryFormat.ChangeSuffix(s)}\n");
        AppendRows(sb, WeeklySummaryFormat.Metrics(s));

        var fixes = WeeklySummaryFormat.FixMetrics(s);
        if (fixes.Count > 0)
        {
            sb.Append("\nConductor this week\n");
            AppendRows(sb, fixes);
        }

        if (s.TopIssues.Count > 0)
        {
            sb.Append("\nTop issues this week\n");
            var rank = 1;
            foreach (var issue in s.TopIssues)
            {
                sb.Append($"{WeeklySummaryFormat.Number(rank)}. {issue.Title} ({WeeklySummaryFormat.Number(issue.Events)})\n");
                // On its own line: a text/plain client turns a bare URL into a link, and appending it to the
                // title line would make the wrapped result hard to click.
                if (WeeklySummaryFormat.IssueUrl(dashboardUrl, issue.Id) is { } issueUrl)
                {
                    sb.Append($"   {issueUrl}\n");
                }
                rank++;
            }
        }

        if (!string.IsNullOrEmpty(dashboardUrl))
        {
            sb.Append($"\nOpen your dashboard: {dashboardUrl}\n");
        }

        return sb.ToString();
    }

    private static void AppendRows(StringBuilder sb, IReadOnlyList<(string Label, string Value)> rows)
    {
        foreach (var (label, value) in rows)
        {
            sb.Append($"{label}: {value}\n");
        }
    }
}
