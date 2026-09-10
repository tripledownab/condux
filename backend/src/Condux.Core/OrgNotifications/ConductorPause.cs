namespace Condux.Core.OrgNotifications;

/// <summary>Why the Conductor stopped auto-requesting fixes for an org (#130). Values match
/// <c>org_pause_notifications.reason</c>.</summary>
public enum ConductorPauseReason
{
    CostCapReached = 1,
    AllowanceExhausted = 2,
}

/// <summary>Renders the org-facing subject + body for a Conductor-pause notice. Pure and side-effect
/// free (the org-notice counterpart of <c>AlertText</c>), so it is unit-testable and the same text
/// goes to every channel. House style: no em-dashes.</summary>
public static class ConductorPauseText
{
    public static string Subject(ConductorPauseReason reason) => reason switch
    {
        ConductorPauseReason.CostCapReached => "Condux: auto-fix paused (monthly AI-fix compute limit reached)",
        ConductorPauseReason.AllowanceExhausted => "Condux: auto-fix paused (fix allowance used up)",
        _ => "Condux: auto-fix paused",
    };

    public static string Body(ConductorPauseReason reason, string orgName)
    {
        var why = reason switch
        {
            ConductorPauseReason.CostCapReached =>
                "this month's AI-fix compute has reached its limit (your plan's fair-use ceiling, or your "
                + "own budget on a bring-your-own key)",
            ConductorPauseReason.AllowanceExhausted =>
                "this month's included AI-fix runs are used up",
            _ => "auto-fix is paused",
        };
        return $"Auto-fix for {orgName} is paused because {why}. New errors will not get an automatic "
            + "fix PR until that clears (the cap is raised, the plan is upgraded, or the month rolls "
            + "over). You can still request fixes manually from the dashboard.";
    }
}

/// <summary>
/// Throttles the pause notices so a persistent condition (every dropped auto-fix while capped) notifies
/// at most once per org per reason per window, instead of on every event. Backed by a small state row
/// upserted atomically; returns true only when a notice should actually be sent now.
/// </summary>
public interface IPauseNotifyThrottle
{
    /// <summary>Records intent to notify and returns true when the last notice for this (org, reason) is
    /// older than <paramref name="window"/> (or none exists) — i.e. send now. False means throttled.</summary>
    Task<bool> TryAcquireAsync(
        long orgId, ConductorPauseReason reason, DateTimeOffset now, TimeSpan window,
        CancellationToken cancellationToken = default);
}
