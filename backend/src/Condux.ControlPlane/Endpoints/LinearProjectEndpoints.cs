using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Tickets;
using Condux.Linear;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// A project's Linear tab (ADR-0053 decision 7): the org's connection as this project sees it, the teams
/// the app can reach, and which one this project's tickets land in. Members read; admins choose.
/// </summary>
internal static class LinearProjectEndpoints
{
    public static void MapLinearProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/linear",
                async Task<Results<Ok<LinearProjectStatusResponse>, NotFound>> (
                    long projectId, LinearConfig config, ProjectRepository projects,
                    TrackerConnectionRepository connections, ProjectTrackerMappingRepository mappings,
                    IssueTicketRepository tickets, HttpContext http) =>
                {
                    if (!config.Enabled || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }
                    var connection = await connections.GetAsync(project.OrgId, TicketProvider.Linear, http.RequestAborted);
                    var mapping = await mappings.GetAsync(projectId, TicketProvider.Linear, http.RequestAborted);
                    return TypedResults.Ok(new LinearProjectStatusResponse(
                        connection is not null, connection?.WorkspaceName, connection?.BrokenAt is not null,
                        mapping is null ? null : ToResponse(mapping),
                        await tickets.CountAutomaticTodayAsync(projectId, http.RequestAborted), AutoTicketPolicy.PerProjectPerDay));
                })
            .WithName("getLinearProject").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Live from Linear, because which teams the app can reach is Linear's admins' decision and changes
        // without telling us.
        app.MapGet("/api/projects/{projectId:long}/linear/teams",
                async Task<Results<Ok<LinearTeamResponse[]>, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, LinearConfig config, ProjectRepository projects, IServiceProvider services,
                    HttpContext http) =>
                {
                    if (!config.Enabled || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }
                    return await CallLinearAsync(services, project.OrgId, (api, token, ct) => api.ListTeamsAsync(token, ct), http.RequestAborted) switch
                    {
                        (var teams, null) => TypedResults.Ok(teams!.Select(t => new LinearTeamResponse(t.Id, t.Key, t.Name)).ToArray()),
                        (_, var error) => TypedResults.Conflict(new ErrorResponse(error)),
                    };
                })
            .WithName("listLinearTeams").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // The labels the mapped team's tickets can carry, live from Linear for the same reason as teams.
        app.MapGet("/api/projects/{projectId:long}/linear/labels",
                async Task<Results<Ok<LinearLabelResponse[]>, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, LinearConfig config, ProjectRepository projects,
                    ProjectTrackerMappingRepository mappings, IServiceProvider services, HttpContext http) =>
                {
                    if (!config.Enabled || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await mappings.GetAsync(projectId, TicketProvider.Linear, http.RequestAborted) is not { } mapping)
                    {
                        return TypedResults.Conflict(new ErrorResponse("linear_not_mapped"));
                    }
                    return await CallLinearAsync(services, project.OrgId,
                            (api, token, ct) => api.ListLabelsAsync(token, mapping.TargetId, ct), http.RequestAborted) switch
                    {
                        (var labels, null) => TypedResults.Ok(ToOffered(labels!, mapping)),
                        (_, var error) => TypedResults.Conflict(new ErrorResponse(error)),
                    };
                })
            .WithName("listLinearLabels").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Admin+: set the team, its labels and auto-create, all at once. Each id is checked against Linear's
        // live list, so a project can only name a team the app reaches and labels that team's issues can
        // carry, and the names stored are the ones Linear gave rather than ones the caller sent.
        app.MapPut("/api/projects/{projectId:long}/linear/mapping",
                async Task<Results<Ok<LinearMappingResponse>, NotFound, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long projectId, SetLinearMappingRequest req, LinearConfig config, ProjectRepository projects,
                    ProjectTrackerMappingRepository mappings, IServiceProvider services, HttpContext http) =>
                {
                    if (!config.Enabled || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }
                    var (teams, error) = await CallLinearAsync(
                        services, project.OrgId, (api, token, ct) => api.ListTeamsAsync(token, ct), http.RequestAborted);
                    if (error is not null)
                    {
                        return TypedResults.Conflict(new ErrorResponse(error));
                    }
                    if (teams!.FirstOrDefault(t => t.Id == req.TeamId) is not { } team)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("linear_team_unknown"));
                    }
                    IReadOnlyList<string?> wanted = req.LabelIds ?? [];
                    IReadOnlyList<TrackerLabel> labels = [];
                    if (wanted.Count > 0)
                    {
                        var (available, labelError) = await CallLinearAsync(services, project.OrgId,
                            (api, token, ct) => api.ListLabelsAsync(token, team.Id, ct), http.RequestAborted);
                        if (labelError is not null)
                        {
                            return TypedResults.Conflict(new ErrorResponse(labelError));
                        }
                        // Saved labels count only for the team they were saved with: another team's may not
                        // exist in this one, and on a list cut short Resolve would keep them unchecked.
                        var stored = await mappings.GetAsync(projectId, TicketProvider.Linear, http.RequestAborted);
                        var saved = stored?.TargetId == team.Id ? stored.Labels : [];
                        var choice = TicketLabels.Resolve(
                            wanted, [.. saved.Select(l => new TicketLabelOption(l.Id, l.Name, null))],
                            [.. available!.Labels.Select(l => new TicketLabelOption(l.Id, l.Name, l.GroupId))],
                            available.Complete);
                        if (choice.Error is { } refusal)
                        {
                            return TypedResults.BadRequest(new ErrorResponse(refusal));
                        }
                        labels = [.. choice.Kept.Select(l => new TrackerLabel(l.Id, l.Name))];
                    }
                    await mappings.SetAsync(projectId, TicketProvider.Linear, team.Id, team.Name, labels, req.AutoCreate, http.RequestAborted);
                    return TypedResults.Ok(ToResponse(new ProjectTrackerMapping(
                        projectId, TicketProvider.Linear, team.Id, team.Name, null, req.AutoCreate, labels)));
                })
            .WithName("setLinearMapping").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapDelete("/api/projects/{projectId:long}/linear/mapping",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, LinearConfig config, ProjectTrackerMappingRepository mappings, HttpContext http) =>
                    config.Enabled && await mappings.DeleteAsync(projectId, TicketProvider.Linear, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteLinearMapping").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));
    }

    // What the picker offers. A list cut short at its page ceiling says nothing about the labels past it, so
    // the project's own labels it did not reach are added back, or the dashboard would mark them as gone
    // from Linear when they are merely further down the list (a save to the same team keeps them too).
    private static LinearLabelResponse[] ToOffered(LinearLabelList list, ProjectTrackerMapping mapping)
    {
        var offered = list.Labels.Select(l => new LinearLabelResponse(l.Id, l.Name, l.GroupId, l.GroupName)).ToList();
        if (!list.Complete)
        {
            offered.AddRange(mapping.Labels
                .Where(saved => offered.All(l => l.Id != saved.Id))
                .Select(saved => new LinearLabelResponse(saved.Id, saved.Name, null, null)));
        }
        return [.. offered];
    }

    private static LinearMappingResponse ToResponse(ProjectTrackerMapping mapping) => new(
        mapping.TargetId, mapping.TargetName, mapping.NeedsAttentionAt is not null, mapping.AutoCreate,
        [.. mapping.Labels.Select(l => new LinearMappingLabelResponse(l.Id, l.Name))]);

    /// <summary>A read from Linear with the org's grant, or a closed reason why there is no answer.</summary>
    private static async Task<(T? Result, string? Error)> CallLinearAsync<T>(
        IServiceProvider services, long orgId, Func<LinearApi, string, CancellationToken, Task<T>> read, CancellationToken ct)
        where T : class
    {
        var sink = services.GetRequiredService<LinearTicketSink>();
        var (token, outcome) = await sink.GetAccessTokenAsync(orgId, forceRefresh: false, ct);
        if (token is null)
        {
            return (null, outcome?.Failure switch
            {
                TicketFailure.NotConnected => "linear_not_connected",
                TicketFailure.ConnectionBroken => "linear_connection_broken",
                _ => "linear_unavailable",
            });
        }
        try
        {
            return (await read(services.GetRequiredService<LinearApi>(), token, ct), null);
        }
        catch (Exception ex) when (ex is LinearUnauthorizedException || LinearHttp.IsCallFailure(ex, ct))
        {
            return (null, "linear_unavailable");
        }
    }
}
