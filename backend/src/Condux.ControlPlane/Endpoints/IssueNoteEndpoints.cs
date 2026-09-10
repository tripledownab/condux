using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Issues;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Per-issue notes (collaborative triage): members read and add free-form notes on an issue; the author
/// or an admin can delete one. Scoped by the same project-membership filter as the rest of the issue
/// surface. The issue is addressed by its public UUID and resolved to the internal id; note ids are UUIDs
/// so they are non-enumerable.
/// </summary>
internal static class IssueNoteEndpoints
{
    public static void MapIssueNoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/issues/{issueId:guid}/notes",
                async Task<Results<Ok<NoteResponse[]>, NotFound>> (
                    long projectId, Guid issueId, IssueRepository issues, IssueNoteRepository notes) =>
                    await issues.GetByPublicIdAsync(projectId, issueId) is { } found
                        ? TypedResults.Ok((await notes.ListByIssueAsync(found.InternalId)).Select(ToResponse).ToArray())
                        : TypedResults.NotFound())
            .WithName("listIssueNotes").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPost("/api/projects/{projectId:long}/issues/{issueId:guid}/notes",
                async Task<Results<Ok<NoteResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid issueId, CreateNoteRequest request, HttpContext http,
                    IssueRepository issues, IssueNoteRepository notes) =>
                {
                    if (!IssueNoteText.TryNormalize(request.Body, out var body, out var error))
                    {
                        return TypedResults.BadRequest(new ErrorResponse(error));
                    }
                    if (await issues.GetByPublicIdAsync(projectId, issueId) is not { } found)
                    {
                        return TypedResults.NotFound();
                    }
                    var author = NoteAuthor.User(OrgAuthorization.CurrentUserId(http.User));
                    var note = await notes.AddAsync(found.InternalId, author, body);
                    return TypedResults.Ok(ToResponse(note));
                })
            .WithName("createIssueNote").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Delete: the author can always remove their own note; anyone else needs admin+ (moderation). A
        // missing issue or note is 404; a member deleting someone else's note is 403 (distinct, not masked).
        // A note written over MCP has no user author, so nobody matches and it takes admin+ to remove.
        app.MapDelete("/api/projects/{projectId:long}/issues/{issueId:guid}/notes/{noteId:guid}",
                async Task<Results<NoContent, NotFound, ForbidHttpResult>> (
                    long projectId, Guid issueId, Guid noteId, HttpContext http,
                    IssueRepository issues, ProjectRepository projects, OrgMemberRepository members,
                    IssueNoteRepository notes) =>
                {
                    if (await issues.GetByPublicIdAsync(projectId, issueId) is not { } found)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await notes.GetAsync(found.InternalId, noteId) is not { } note)
                    {
                        return TypedResults.NotFound();
                    }
                    var userId = OrgAuthorization.CurrentUserId(http.User);
                    if (note.AuthorUserId != userId && !await IsAdminAsync(projects, members, projectId, userId))
                    {
                        return TypedResults.Forbid();
                    }
                    await notes.DeleteAsync(found.InternalId, noteId);
                    return TypedResults.NoContent();
                })
            .WithName("deleteIssueNote").WithTags("Issues")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    private static async Task<bool> IsAdminAsync(
        ProjectRepository projects, OrgMemberRepository members, long projectId, long userId)
    {
        var project = await projects.GetAsync(projectId);
        return project is not null && await members.GetRoleAsync(project.OrgId, userId) is >= OrgRole.Admin;
    }

    private static NoteResponse ToResponse(IssueNote n) =>
        new(n.Id, n.Body, n.AuthorUserId, n.AuthorEmail, n.AuthorTokenName, n.CreatedAt);
}
