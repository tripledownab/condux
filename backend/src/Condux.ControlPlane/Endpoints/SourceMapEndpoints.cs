using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Uploads;
using Condux.Core.SourceMaps;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Source-map upload (ADR-0028, slice 1): CI uploads a client source map so the control-plane can de-minify
/// stack frames at read time. Authenticated by the scoped release token (the same <c>Bearer</c> credential
/// that records a release), so there is no cookie or internal id. Opt-in: when object storage is not
/// configured (<c>CONDUX_S3_*</c>) the routes 404. The map bytes go to object storage under a derived key;
/// a Postgres row indexes them by debugId + (release, dist, filename). Metadata rides the query string, the
/// map itself is the raw request body.
/// </summary>
internal static class SourceMapEndpoints
{
    // A single map larger than this is a slice-4 (chunked upload) concern, not v1.
    private const int MaxBytes = 25 * 1024 * 1024;

    // Cap the metadata that flows into the S3 object key (S3 keys max 1024 bytes) and the DB row.
    private const int MaxFieldLength = 256;

    public static void MapSourceMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sourcemaps",
                async Task<Results<Created<SourceMapArtifactResponse>, UnauthorizedHttpResult, BadRequest<string>, NotFound,
                    StatusCodeHttpResult>> (
                    HttpContext http, string? release, string? filename, string? dist, string? debugId,
                    ObjectStoreConfig store, ReleaseTokenRepository tokens, SourceMapArtifactRepository artifacts) =>
                {
                    if (!store.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await ReleaseTokenAuth.ResolveProjectAsync(http, tokens) is not { } projectId)
                    {
                        return TypedResults.Unauthorized();
                    }
                    if (string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(filename))
                    {
                        return TypedResults.BadRequest("release and filename query parameters are required");
                    }
                    if (TooLong(release) || TooLong(filename) || TooLong(dist) || TooLong(debugId))
                    {
                        return TypedResults.BadRequest(
                            $"release, filename, dist and debugId must each be at most {MaxFieldLength} characters");
                    }

                    var read = await UploadBodies.ReadAsync(http, MaxBytes);
                    if (read.IsRefused)
                    {
                        return read.Refusal;
                    }
                    using var body = read.Body;
                    var content = body.Content;
                    if (content.Length == 0)
                    {
                        return TypedResults.BadRequest("source map body is empty");
                    }
                    if (!SourceMapContent.LooksLikeSourceMap(content))
                    {
                        return TypedResults.BadRequest(
                            "body is not a valid source map (expected JSON with version 3 and mappings or sections)");
                    }

                    var objectStore = http.RequestServices.GetRequiredService<IObjectStore>();
                    var key = SourceMapKeys.ForArtifact(projectId, release.Trim(), dist, debugId, filename.Trim());
                    // The row first, so every stored object has a row the quota counts and expiry reaches.
                    // A store write that then fails leaves a row with no object, which reads as a missing map.
                    var artifact = await artifacts.RecordAsync(
                        projectId, release.Trim(), Trim(dist), Trim(debugId), filename.Trim(), key,
                        content.Length, http.RequestAborted);
                    await objectStore.PutAsync(key, content, "application/json", http.RequestAborted);
                    await UploadEviction.TryEvictAsync(http, projectId);
                    return TypedResults.Created($"/api/sourcemaps/{artifact.Id}", ToResponse(artifact));
                })
            .WithName("uploadSourceMap").WithTags("SourceMaps");
    }

    private static bool TooLong(string? value) => value is { Length: > MaxFieldLength };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SourceMapArtifactResponse ToResponse(SourceMapArtifact a) =>
        new(a.Id, a.Release, a.Dist, a.DebugId, a.Filename, a.ByteSize, a.CreatedAt);
}
