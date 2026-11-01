using System.Globalization;

namespace Condux.Core.WeeklySummaries;

/// <summary>
/// Presentation-neutral formatting for the weekly digest (ADR-0031): the title, count formatting, date range,
/// the week-over-week suffix, and the ordered metric rows. This is the SINGLE source both the plain-text body
/// (<see cref="WeeklySummaryText"/>) and the HTML view render from, so the two MIME parts of the email can
/// never list different numbers or labels. Pure and unit-tested.
/// </summary>
public static class WeeklySummaryFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>The digest title, shared by the email subject and the HTML heading.</summary>
    public static string Title(WeeklySummary s) => $"Your Condux week: {s.OrgName}";

    /// <summary>A count with thousands separators, invariant-culture.</summary>
    public static string Number(long value) => value.ToString("N0", Culture);

    /// <summary>The week window as "MMM d to MMM d" (invariant), so both views render the date identically
    /// regardless of the host's current culture.</summary>
    public static string DateRange(WeeklySummary s) =>
        $"{s.WeekStart.ToString("MMM d", Culture)} to {s.WeekEnd.ToString("MMM d", Culture)}";

    /// <summary>The signed week-over-week percentage, e.g. "+12%" / "-8%", or empty when there is no prior
    /// week to compare against.</summary>
    public static string ChangePercentText(WeeklySummary s) => s.EventsChangePercent is { } percent
        ? $"{(percent >= 0 ? "+" : "")}{percent.ToString(Culture)}%"
        : string.Empty;

    /// <summary>" (+12% vs last week)" suffix, or empty when there is no prior week to compare.</summary>
    public static string ChangeSuffix(WeeklySummary s) =>
        ChangePercentText(s) is { Length: > 0 } text ? $" ({text} vs last week)" : string.Empty;

    /// <summary>The core issue-movement rows (label, value) in display order.</summary>
    public static IReadOnlyList<(string Label, string Value)> Metrics(WeeklySummary s) =>
    [
        ("New issues", Number(s.NewIssues)),
        ("Regressions", Number(s.Regressions)),
        ("Resolved", Number(s.Resolved)),
        ("Open issues", Number(s.OpenIssues)),
        ("Users affected", $"~{Number(s.UsersAffected)}"),
    ];

    /// <summary>The Conductor activity rows, or empty when there was no fix activity this week.</summary>
    public static IReadOnlyList<(string Label, string Value)> FixMetrics(WeeklySummary s) => s.Fixes.Any
        ?
        [
            ("Fixes proposed", Number(s.Fixes.Proposed)),
            ("Draft PRs opened", Number(s.Fixes.PrsOpened)),
            ("PRs merged", Number(s.Fixes.PrsMerged)),
            ("Issues auto-resolved", Number(s.Fixes.AutoResolved)),
        ]
        : [];
}
