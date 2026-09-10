using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.Issues;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>The triage mutations on an issue: its status, and who owns it. Separate from the read
/// endpoints because these are the two that have a side effect beyond the row, firing the project's
/// alert rules and nudging every open dashboard. Both effects live in <see cref="IssueTriage"/>, which
/// the MCP triage tools call too (ADR-0046), so a change made by an agent behaves identically.</summary>
internal static class IssueTriageEndpoints
{
    public static void MapIssueTriageEndpoints(this IEndpointRouteBuilder app)
    {
        // Set the issue's status (1 unresolved, 2 resolved, 3 ignored). Member+, because triage is
        // day-to-day operations rather than configuration, so every member of the org can do it.
        app.MapPatch("/api/projects/{projectId:long}/issues/{issueId:guid}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, UpdateIssueStatusRequest request, IssueTriage triage,
                    CancellationToken cancellationToken) =>
                {
                    if (request.Status is not (1 or 2 or 3))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_status"));
                    }
                    return await triage.SetStatusAsync(projectId, issueId, request.Status, cancellationToken)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("updateIssueStatus").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Assign the issue to an org member (null unassigns). Member+, like status. The assignee must
        // belong to the project's org.
        app.MapPatch("/api/projects/{projectId:long}/issues/{issueId:guid}/assignee",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, AssignIssueRequest request, IssueTriage triage,
                    ProjectRepository projects, OrgMemberRepository members,
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
                    return await triage.SetAssigneeAsync(
                        projectId, issueId, request.UserId, cancellationToken)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("assignIssue").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
