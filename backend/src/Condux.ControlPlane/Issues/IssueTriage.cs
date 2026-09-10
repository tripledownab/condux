using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Notifications;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Issues;

/// <summary>
/// Changing an issue's triage state, with the side effects that must not depend on who asked for it.
///
/// There are two callers: the dashboard's REST endpoints and the MCP triage tools (ADR-0046). The side
/// effects live here rather than in either of them because a second caller would otherwise duplicate them
/// or, worse, silently drop one. A resolve that skipped the alert rules would look like it worked.
///
/// Both effects are best-effort and run after the row is committed. Failing them would report an error for
/// a change that already happened, which is the one outcome a caller cannot act on.
/// </summary>
internal sealed class IssueTriage(
    IssueRepository issues, AlertDispatcher alerts, ProjectEventNotifier projectEvents,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<IssueTriage>();

    /// <summary>
    /// Sets the issue's status (1 unresolved, 2 resolved, 3 ignored). False when no such issue is in the
    /// project. A resolve fires the project's Resolved alert rules.
    /// </summary>
    public async Task<bool> SetStatusAsync(
        long projectId, Guid issueId, int status, CancellationToken cancellationToken)
    {
        if (!await issues.UpdateStatusAsync(projectId, issueId, status, cancellationToken))
        {
            return false;
        }
        if (status == 2)
        {
            await FireAsync(projectId, issueId, AlertEventType.Resolved, cancellationToken);
        }
        await NudgeAsync(projectId, cancellationToken);
        return true;
    }

    /// <summary>
    /// Assigns the issue to a user, or unassigns it with null. False when no such issue is in the project.
    /// The caller checks that the user belongs to the project's org; that is authorization, not triage.
    /// Assigning fires the project's Assigned alert rules, unassigning fires nothing.
    /// </summary>
    public async Task<bool> SetAssigneeAsync(
        long projectId, Guid issueId, long? userId, CancellationToken cancellationToken)
    {
        if (!await issues.UpdateAssigneeAsync(projectId, issueId, userId, cancellationToken))
        {
            return false;
        }
        if (userId is not null)
        {
            await FireAsync(projectId, issueId, AlertEventType.Assigned, cancellationToken);
        }
        await NudgeAsync(projectId, cancellationToken);
        return true;
    }

    // Tell every dashboard open on this project that something moved (ADR-0030). The caller's own tab
    // already knows from its mutation, but a triage change made over MCP has no tab at all, so without
    // this every open view holds the old status until its slow safety poll comes round.
    private Task NudgeAsync(long projectId, CancellationToken cancellationToken) =>
        ProjectEventNudge.TrySendAsync(projectEvents, logger, projectId, cancellationToken);

    // Load the issue summary and fire the project's alert rules for a triage event. Guarded so a load or
    // delivery failure never fails the change that already committed; the dispatcher additionally swallows
    // its own per-channel delivery errors.
    private async Task FireAsync(
        long projectId, Guid issueId, AlertEventType eventType, CancellationToken cancellationToken)
    {
        try
        {
            if (await issues.GetByPublicIdAsync(projectId, issueId, cancellationToken) is not { } found)
            {
                return;
            }
            var summary = found.Summary;
            var notification = new AlertNotification(
                projectId, summary.Id, summary.Title, summary.Culprit, (Level)summary.Level, eventType);
            await alerts.DispatchAsync(projectId, eventType, notification, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "issue alert dispatch failed project={ProjectId} event={Event}", projectId, eventType);
        }
    }
}
