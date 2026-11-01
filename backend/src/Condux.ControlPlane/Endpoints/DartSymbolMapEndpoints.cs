using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Uploads;
using Condux.Core.DebugFiles;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Flutter obfuscation map upload: CI sends the map <c>--save-obfuscation-map</c> wrote, naming the debug id
/// of a debug file of the same build it has already uploaded, so an obfuscated class name in a crash from
/// that build can be read as the real one (<see cref="DartObfuscationMaps"/>). Authenticated by the scoped
/// release token like the debug file, and off with it when no object storage is configured.
///
/// The map names no build of its own, so the build is the one the upload names, and it must already have a
/// debug file in the project: the map is held under that file's row, where the upload quota counts it and
/// eviction and expiry remove it.
/// </summary>
internal static class DartSymbolMapEndpoints
{
    public static void MapDartSymbolMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dart-symbol-maps",
                async Task<Results<Created<DartSymbolMapResponse>, UnauthorizedHttpResult, BadRequest<string>,
                    NotFound, StatusCodeHttpResult>> (
                    HttpContext http, string debugId, ObjectStoreConfig store, ReleaseTokenRepository tokens,
                    DartSymbolMapRepository maps) =>
                {
                    if (!store.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await ReleaseTokenAuth.ResolveProjectAsync(http, tokens) is not { } projectId)
                    {
                        return TypedResults.Unauthorized();
                    }
                    if (DebugFileFormats.ToStoredDebugId(debugId) is not { } stored)
                    {
                        return TypedResults.BadRequest(
                            "debugId is not a debug id; use the debugId the debug file upload answered with");
                    }

                    var read = await UploadBodies.ReadAsync(http, DartObfuscationMaps.MaxBytes);
                    if (read.IsRefused)
                    {
                        return read.Refusal;
                    }
                    using var body = read.Body;

                    // Entered with the body already read, so a place is held for the parse and the write and
                    // never for a sender's pace.
                    var gate = http.RequestServices.GetRequiredService<DartSymbolMapUploadGate>();
                    if (!gate.TryEnter())
                    {
                        http.Response.Headers.RetryAfter = "5";
                        return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
                    }
                    try
                    {
                        return await StoreAsync(http, projectId, stored, maps, body.Content);
                    }
                    finally
                    {
                        gate.Leave();
                    }
                })
            .WithName("uploadDartSymbolMap").WithTags("DebugFiles");
    }

    private static async Task<Results<Created<DartSymbolMapResponse>, UnauthorizedHttpResult, BadRequest<string>,
        NotFound, StatusCodeHttpResult>> StoreAsync(HttpContext http, long projectId, string debugId,
        DartSymbolMapRepository maps, byte[] body)
    {
        var (map, rejection) = DartObfuscationMaps.ToMap(body);
        if (map is null)
        {
            return TypedResults.BadRequest(rejection!);
        }

        var recorded = await maps.RecordAsync(projectId, debugId, map, http.RequestAborted);
        if (!recorded)
        {
            return TypedResults.BadRequest(
                "no debug file with that debugId in this project; upload the build's debug file first");
        }
        await UploadEviction.TryEvictAsync(http, projectId);
        return TypedResults.Created($"/api/dart-symbol-maps?debugId={debugId}",
            new DartSymbolMapResponse(debugId, map.Count));
    }
}
