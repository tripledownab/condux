using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.Setup;
using Condux.ControlPlane.Tickets;
using Condux.Core.Auth;
using Condux.Core.Tickets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// An issue's tracker ticket (ADR-0053 decision 5). These routes only reserve, retry or forget the row and
/// never call the tracker: the worker is the one caller, which is what keeps a click, a second tab and an
/// alert from opening three tickets. Member+, because opening a ticket costs nothing and is triage, like
/// setting an issue's status. The ticket shown is the one in the org's current workspace.
/// </summary>
internal static class IssueTicketEndpoints
{
    private const string Route = "/api/projects/{projectId:long}/issues/{issueId:guid}/ticket";

    public static void MapIssueTicketEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route,
                async Task<Results<Ok<IssueTicketResponse>, NoContent, NotFound>> (
                    long projectId, Guid issueId, LinearConfig config, Lookups lookups, HttpContext http) =>
                {
                    if (!config.Enabled || await lookups.ResolveAsync(projectId, issueId, http.RequestAborted) is not { } at)
                    {
                        return TypedResults.NotFound();
                    }
                    return at.Workspace is { } workspace
                           && await lookups.Tickets.GetAsync(at.IssueId, TicketProvider.Linear, workspace, http.RequestAborted) is { } ticket
                        ? TypedResults.Ok(ToResponse(ticket))
                        : TypedResults.NoContent();
                })
            .WithName("getIssueTicket").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Reserve, or return what is already reserved. 202 either way, because the ticket is made by the
        // worker after this answers.
        app.MapPost(Route,
                async Task<Results<Accepted<IssueTicketResponse>, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, Guid issueId, LinearConfig config, Lookups lookups,
                    ProjectTrackerMappingRepository mappings, TicketWorkerSignal signal, HttpContext http) =>
                {
                    if (!config.Enabled || await lookups.ResolveAsync(projectId, issueId, http.RequestAborted) is not { } at)
                    {
                        return TypedResults.NotFound();
                    }
                    if (at.Workspace is not { } workspace)
                    {
                        return TypedResults.Conflict(new ErrorResponse("linear_not_connected"));
                    }
                    if (await mappings.GetAsync(projectId, TicketProvider.Linear, http.RequestAborted) is null)
                    {
                        return TypedResults.Conflict(new ErrorResponse("linear_not_mapped"));
                    }
                    var (ticket, reserved) = await lookups.Tickets.ReserveAsync(
                        at.IssueId, TicketProvider.Linear, workspace, OrgAuthorization.CurrentUserId(http.User),
                        http.RequestAborted);
                    if (reserved)
                    {
                        signal.Wake();
                    }
                    return TypedResults.Accepted(Route, ToResponse(ticket));
                })
            .WithName("createIssueTicket").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Send a failed or uncertain ticket again, under the same id, so Linear is asked for the same issue.
        app.MapPost($"{Route}/retry",
                async Task<Results<Accepted<IssueTicketResponse>, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, Guid issueId, LinearConfig config, Lookups lookups, TicketWorkerSignal signal,
                    HttpContext http) =>
                {
                    if (await lookups.CurrentTicketAsync(config, projectId, issueId, http.RequestAborted) is not { } ticket)
                    {
                        return TypedResults.NotFound();
                    }
                    if (!await lookups.Tickets.RetryAsync(ticket.Id, http.RequestAborted))
                    {
                        return TypedResults.Conflict(new ErrorResponse("ticket_not_retryable"));
                    }
                    signal.Wake();
                    return TypedResults.Accepted(Route, ToResponse(ticket with { State = TicketState.Pending, Failure = null }));
                })
            .WithName("retryIssueTicket").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Forget the link, which is the answer to a ticket deleted in Linear: the issue can then have a new one.
        app.MapDelete(Route,
                async Task<Results<NoContent, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, Guid issueId, LinearConfig config, Lookups lookups, HttpContext http) =>
                {
                    if (await lookups.CurrentTicketAsync(config, projectId, issueId, http.RequestAborted) is not { } ticket)
                    {
                        return TypedResults.NotFound();
                    }
                    return await lookups.Tickets.UnlinkAsync(ticket.Id, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.Conflict(new ErrorResponse("ticket_in_flight"));
                })
            .WithName("unlinkIssueTicket").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    private static IssueTicketResponse ToResponse(IssueTicket t) =>
        new(t.Id, (int)t.Provider, (int)t.State, t.Failure is { } f ? (int)f : null, t.Key, t.Url, t.Automatic);

    /// <summary>The issue, its internal id, and the org's current workspace, which null means not connected.</summary>
    internal sealed record Resolved(long IssueId, string? Workspace);

    /// <summary>The reads every route here starts with, gathered so each one resolves the same way.</summary>
    internal sealed class Lookups(
        IssueRepository issues, ProjectRepository projects, TrackerConnectionRepository connections,
        IssueTicketRepository tickets)
    {
        public IssueTicketRepository Tickets => tickets;

        public async Task<Resolved?> ResolveAsync(long projectId, Guid issueId, CancellationToken ct)
        {
            if (await issues.GetByPublicIdAsync(projectId, issueId, ct) is not { } issue
                || await projects.GetAsync(projectId, ct) is not { } project)
            {
                return null;
            }
            var connection = await connections.GetAsync(project.OrgId, TicketProvider.Linear, ct);
            return new Resolved(issue.InternalId, connection?.WorkspaceId);
        }

        public async Task<IssueTicket?> CurrentTicketAsync(LinearConfig config, long projectId, Guid issueId, CancellationToken ct) =>
            config.Enabled && await ResolveAsync(projectId, issueId, ct) is { Workspace: { } workspace } at
                ? await tickets.GetAsync(at.IssueId, TicketProvider.Linear, workspace, ct)
                : null;
    }
}
