using Condux.Core.WeeklySummaries;

namespace Condux.Notifications;

/// <summary>Renders a <see cref="WeeklySummary"/> into the branded <see cref="EmailContent"/> (ADR-0031),
/// reusing the layout as-is: the shared metric rows plus the top issues become the facts table, one CTA opens
/// the dashboard. Title, numbers, and labels come from <see cref="WeeklySummaryFormat"/> — the same source the
/// plain-text body uses, so the two views never disagree.</summary>
public static class WeeklySummaryEmail
{
    private const int TitleMaxLength = 44;

    public static EmailContent Content(WeeklySummary summary, string? dashboardUrl)
    {
        var facts = new List<EmailFact>();
        if (summary.EventsChangePercent is { } percent)
        {
            facts.Add(new EmailFact("vs last week", WeeklySummaryFormat.ChangePercentText(summary), TrendColor(percent)));
        }
        facts.AddRange(WeeklySummaryFormat.Metrics(summary).Select(row => new EmailFact(row.Label, row.Value)));
        facts.AddRange(WeeklySummaryFormat.FixMetrics(summary).Select(row => new EmailFact(row.Label, row.Value)));

        var rank = 1;
        foreach (var issue in summary.TopIssues)
        {
            facts.Add(new EmailFact(
                $"{WeeklySummaryFormat.Number(rank)}. {Truncate(issue.Title)}",
                $"{WeeklySummaryFormat.Number(issue.Events)} events"));
            rank++;
        }

        return new EmailContent(
            Heading: WeeklySummaryFormat.Title(summary),
            Paragraphs:
            [
                WeeklySummaryFormat.DateRange(summary),
                $"{WeeklySummaryFormat.Number(summary.Events)} events this week.",
            ],
            Facts: facts,
            Button: string.IsNullOrEmpty(dashboardUrl) ? null : new EmailButton("Open Condux", dashboardUrl));
    }

    // For an error monitor, more events week-over-week is worse (red), fewer is better (green), flat is neutral.
    private static string? TrendColor(int percent) =>
        percent > 0 ? EmailTheme.TrendBad : percent < 0 ? EmailTheme.TrendGood : null;

    private static string Truncate(string value) =>
        value.Length <= TitleMaxLength ? value : value[..(TitleMaxLength - 1)] + "…";
}
