namespace Condux.Core.WeeklySummaries;

/// <summary>One of an org's most active issues over the week, for the report's top-issues list. Carries the
/// issue's PUBLIC id, because the digest links each one and the internal bigint never leaves the server.
/// </summary>
public sealed record TopIssue(Guid Id, string Title, long Events);

/// <summary>The Conductor's activity for an org over the week — the fix-engine section of the report that
/// Sentry has no equivalent to. Counts of runs proposed, draft PRs opened, PRs merged, and issues the
/// verification loop auto-resolved.</summary>
public sealed record WeeklyFixActivity(long Proposed, long PrsOpened, long PrsMerged, long AutoResolved)
{
    public static readonly WeeklyFixActivity None = new(0, 0, 0, 0);

    public bool Any => Proposed > 0 || PrsOpened > 0 || PrsMerged > 0 || AutoResolved > 0;
}

/// <summary>A computed weekly digest for one org: the numbers a Sentry-style summary email shows. Pure data
/// (the composer fills it from the stores; the text/HTML builders render it), so it is trivially testable.</summary>
public sealed record WeeklySummary(
    long OrgId,
    string OrgName,
    DateTimeOffset WeekStart,
    DateTimeOffset WeekEnd,
    long Events,
    long PreviousEvents,
    long NewIssues,
    long Regressions,
    long Resolved,
    long OpenIssues,
    long UsersAffected,
    IReadOnlyList<TopIssue> TopIssues,
    WeeklyFixActivity Fixes)
{
    /// <summary>An org with no events all week is dormant; the worker skips its email (ADR-0031).</summary>
    public bool HadActivity => Events > 0;

    /// <summary>Week-over-week change in event volume as a rounded percentage, or null when there is no prior
    /// week to compare against (so the renderers can omit the comparison rather than divide by zero).</summary>
    public int? EventsChangePercent => PreviousEvents == 0
        ? null
        : (int)Math.Round((Events - PreviousEvents) * 100.0 / PreviousEvents);
}
