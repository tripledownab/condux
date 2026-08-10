using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
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
    // Cap an upload well under Kestrel's default request-body limit; a single map larger than this is a
    // slice-4 (chunked upload) concern, not v1.
    private const int MaxBytes = 25 * 1024 * 1024;

    // Cap the metadata that flows into the S3 object key (S3 keys max 1024 bytes) and the DB row.
    private const int MaxFieldLength = 256;

    public static void MapSourceMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sourcemaps",
                async Task<Results<Created<SourceMapArtifactResponse>, UnauthorizedHttpResult, BadRequest<string>, NotFound>> (
                    HttpContext http, string? release, string? filename, string? dist, string? debugId,
                    ObjectStoreConfig store, ReleaseTokenRepository tokens, SourceMapArtifactRepository artifacts) =>
                {
                    if (!store.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await ResolveProject(http, tokens) is not { } projectId)
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

                    var content = await ReadBodyAsync(http);
                    if (content is null)
                    {
                        return TypedResults.BadRequest($"source map body is empty or exceeds {MaxBytes} bytes");
                    }
                    if (!SourceMapContent.LooksLikeSourceMap(content))
                    {
                        return TypedResults.BadRequest(
                            "body is not a valid source map (expected JSON with version 3 and mappings or sections)");
                    }

                    var objectStore = http.RequestServices.GetRequiredService<IObjectStore>();
                    var key = SourceMapKeys.ForArtifact(projectId, release.Trim(), dist, debugId, filename.Trim());
                    await objectStore.PutAsync(key, content, "application/json", http.RequestAborted);
                    var artifact = await artifacts.RecordAsync(
                        projectId, release.Trim(), Trim(dist), Trim(debugId), filename.Trim(), key,
                        content.Length, http.RequestAborted);
                    return TypedResults.Created($"/api/sourcemaps/{artifact.Id}", ToResponse(artifact));
                })
            .WithName("uploadSourceMap").WithTags("SourceMaps");
    }

    private static async Task<long?> ResolveProject(HttpContext http, ReleaseTokenRepository tokens)
    {
        if (BearerToken.From(http) is not { } raw)
        {
            return null;
        }

        return await tokens.ResolveProjectAsync(ReleaseTokens.HashToken(raw));
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext http)
    {
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        return buffer.Length is > 0 and <= MaxBytes ? buffer.ToArray() : null;
    }

    private static bool TooLong(string? value) => value is { Length: > MaxFieldLength };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SourceMapArtifactResponse ToResponse(SourceMapArtifact a) =>
        new(a.Id, a.Release, a.Dist, a.DebugId, a.Filename, a.ByteSize, a.CreatedAt);
}
