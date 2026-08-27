using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Saved issue views (#108): a user's named search + ordering presets for one project. Every
/// route is scoped to the calling user as well as the project, so one member cannot read, rewrite or
/// delete another's view, and an id outside their own set is simply not found.</summary>
internal static class SavedViewEndpoints
{
    private static readonly HashSet<string> SavedViewSorts =
        ["lastSeen", "firstSeen", "events", "severity"];

    // Bounded free-form fields; the sort must be one the dashboard can apply.
    private static ErrorResponse? ValidateSavedView(SavedViewRequest request) =>
        string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80
            ? new ErrorResponse("invalid_view_name")
            : request.Query.Length > 500
                ? new ErrorResponse("invalid_view_query")
                : !SavedViewSorts.Contains(request.Sort)
                    ? new ErrorResponse("invalid_view_sort")
                    : null;

    public static void MapSavedViewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/views",
                async (long projectId, HttpContext http, SavedViewRepository views) =>
                    TypedResults.Ok(await views.ListAsync(
                        OrgAuthorization.CurrentUserId(http.User), projectId)))
            .WithName("listSavedViews").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Create is an upsert on (user, project, name), so saving over an existing name re-points that
        // view instead of failing. That is the save-as behaviour the rail offers, not an accident.
        app.MapPost("/api/projects/{projectId:long}/views",
                async Task<Results<Ok<SavedView>, BadRequest<ErrorResponse>>> (
                    long projectId, SavedViewRequest request, HttpContext http,
                    SavedViewRepository views) =>
                    ValidateSavedView(request) is { } error
                        ? TypedResults.BadRequest(error)
                        : TypedResults.Ok(await views.UpsertAsync(
                            OrgAuthorization.CurrentUserId(http.User), projectId,
                            request.Name.Trim(), request.Query.Trim(), request.Sort)))
            .WithName("createSavedView").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPatch("/api/projects/{projectId:long}/views/{viewId:long}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, long viewId, SavedViewRequest request, HttpContext http,
                    SavedViewRepository views) =>
                    ValidateSavedView(request) is { } error
                        ? TypedResults.BadRequest(error)
                        : await views.UpdateAsync(
                            viewId, OrgAuthorization.CurrentUserId(http.User), projectId,
                            request.Name.Trim(), request.Query.Trim(), request.Sort)
                            ? TypedResults.NoContent()
                            : TypedResults.NotFound())
            .WithName("updateSavedView").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapDelete("/api/projects/{projectId:long}/views/{viewId:long}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, long viewId, HttpContext http, SavedViewRepository views) =>
                    await views.DeleteAsync(viewId, OrgAuthorization.CurrentUserId(http.User), projectId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteSavedView").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
