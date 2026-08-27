using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.Core.Alerting;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>The triage mutations on an issue: its status, and who owns it. Separate from the read
/// endpoints because these are the two that have a side effect beyond the row, firing the project's
/// alert rules, and that firing is best-effort by design.</summary>
internal static class IssueTriageEndpoints
{
    public static void MapIssueTriageEndpoints(this IEndpointRouteBuilder app)
    {
        // Set the issue's status (1 unresolved, 2 resolved, 3 ignored). Member+, because triage is
        // day-to-day operations rather than configuration, so every member of the org can do it.
        app.MapPatch("/api/projects/{projectId:long}/issues/{issueId:guid}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, UpdateIssueStatusRequest request,
                    IssueRepository issues, AlertDispatcher alerts, ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    if (request.Status is not (1 or 2 or 3))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_status"));
                    }
                    if (!await issues.UpdateStatusAsync(projectId, issueId, request.Status))
                    {
                        return TypedResults.NotFound();
                    }
                    // A manual resolve (status 2) fires the project's Resolved alert rules, best-effort.
                    if (request.Status == 2)
                    {
                        await FireIssueAlertAsync(
                            alerts, issues, loggerFactory, projectId, issueId, AlertEventType.Resolved,
                            cancellationToken);
                    }
                    return TypedResults.NoContent();
                })
            .WithName("updateIssueStatus").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Assign the issue to an org member (null unassigns). Member+, like status. The assignee must
        // belong to the project's org.
        app.MapPatch("/api/projects/{projectId:long}/issues/{issueId:guid}/assignee",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, AssignIssueRequest request,
                    IssueRepository issues, ProjectRepository projects, OrgMemberRepository members,
                    AlertDispatcher alerts, ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken) =>
                {
                    if (request.UserId is { } userId)
                    {
                        var record = await projects.GetAsync(projectId);
                        if (record is null || await members.GetRoleAsync(record.OrgId, userId) is null)
                        {
                            return TypedResults.BadRequest(new ErrorResponse("assignee_not_a_member"));
                        }
                    }
                    if (!await issues.UpdateAssigneeAsync(projectId, issueId, request.UserId))
                    {
                        return TypedResults.NotFound();
                    }
                    // Assigning to a user (not unassigning) fires the project's Assigned alert rules,
                    // best-effort.
                    if (request.UserId is not null)
                    {
                        await FireIssueAlertAsync(
                            alerts, issues, loggerFactory, projectId, issueId, AlertEventType.Assigned,
                            cancellationToken);
                    }
                    return TypedResults.NoContent();
                })
            .WithName("assignIssue").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    // Best-effort: load the issue summary and fire the project's alert rules for a triage event
    // (Resolved/Assigned). Guarded so a load or delivery failure never fails the triage request that
    // already committed; the dispatcher additionally swallows its own per-channel delivery errors.
    private static async Task FireIssueAlertAsync(
        AlertDispatcher alerts, IssueRepository issues, ILoggerFactory loggerFactory,
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
            loggerFactory.CreateLogger("Condux.ControlPlane.IssueAlerts").LogWarning(
                ex, "issue alert dispatch failed project={ProjectId} event={Event}", projectId, eventType);
        }
    }
}
