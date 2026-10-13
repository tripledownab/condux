using Condux.ControlPlane.Auth;
using Condux.Core.DebugFiles;
using Condux.Core.Http;
using Condux.Core.SourceMaps;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Native debug file upload: CI sends each DWARF symbols file a native build left behind (a Flutter build's
/// <c>--split-debug-info</c> output is the case this exists for), so frames that carry only an address can
/// later be resolved to code. Authenticated by the scoped release token, like source maps, and opt-in the
/// same way: with no object storage configured the route answers 404.
///
/// The request carries the file and nothing else. Which build it belongs to is read from the file's own
/// GNU build ID, so CI cannot attach a file to the wrong build by naming it wrongly, and one command can
/// upload every file in the directory without knowing what each one is.
/// </summary>
internal static class DebugFileEndpoints
{
    // A ceiling, not an estimate of a symbols file's size. The body is held in memory while it is checked
    // and stored, and growing that buffer can briefly take a few times this, which is why
    // DebugFileUploadGate also bounds how many uploads are held at once.
    private const int MaxBytes = 64 * 1024 * 1024;

    public static void MapDebugFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/debug-files",
                async Task<Results<Created<DebugFileResponse>, UnauthorizedHttpResult, BadRequest<string>, NotFound,
                    StatusCodeHttpResult>> (
                    HttpContext http, ObjectStoreConfig store, ReleaseTokenRepository tokens, DebugFileRepository files) =>
                {
                    if (!store.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await ReleaseTokenAuth.ResolveProjectAsync(http, tokens) is not { } projectId)
                    {
                        return TypedResults.Unauthorized();
                    }

                    var gate = http.RequestServices.GetRequiredService<DebugFileUploadGate>();
                    if (!gate.TryEnter())
                    {
                        http.Response.Headers.RetryAfter = "5";
                        return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
                    }

                    try
                    {
                        return await StoreAsync(http, projectId, files);
                    }
                    finally
                    {
                        gate.Leave();
                    }
                })
            .WithName("uploadDebugFile").WithTags("DebugFiles");
    }

    private static async Task<Results<Created<DebugFileResponse>, UnauthorizedHttpResult, BadRequest<string>,
        NotFound, StatusCodeHttpResult>> StoreAsync(HttpContext http, long projectId, DebugFileRepository files)
    {
        // Kestrel's default request limit sits below this cap, so it is raised to the cap for this request
        // where the host allows it. The read is bounded as well, since some hosts offer no such feature.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBytes;
        }

        byte[]? content;
        try
        {
            content = await RequestBodies.ReadBoundedAsync(http.Request.Body, MaxBytes, http.RequestAborted);
        }
        catch (BadHttpRequestException e)
        {
            // Kestrel says the body itself was wrong: past its limit (413, possibly before any byte is read,
            // when a declared length is too large), cut short (400) or sent too slowly (408). Each is the
            // caller's to fix, so it is answered with Kestrel's own status rather than raised as ours.
            return TypedResults.StatusCode(e.StatusCode);
        }
        if (content is null)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var (build, rejection) = ElfDebugFile.ToBuild(content);
        if (build is null)
        {
            return TypedResults.BadRequest(ToMessage(rejection));
        }

        // The build ID is lowercase hex from the parser, so it is safe as a key segment as it stands.
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + build.BuildId;
        var objectStore = http.RequestServices.GetRequiredService<IObjectStore>();
        // The row first, so the quota counts and expiry reaches what is stored. The hash only after the store
        // write succeeds: the hash names the bytes the bucket holds, and a write that failed, on a re-upload,
        // leaves the previous upload's bytes there. A row with no hash is what the read path already handles,
        // by hashing the file it reads and recording that. A read in between may have hashed the bytes this
        // upload replaced and recorded that, so this write replaces any hash the row holds by now, and does
        // not take the request's token: a client that hangs up once the store write has returned would
        // otherwise leave that read's hash on the row.
        var file = await files.RecordAsync(projectId, build, key, content.Length, sha256: null, http.RequestAborted);
        await objectStore.PutAsync(key, content, "application/octet-stream", http.RequestAborted);
        await files.RecordStoredSha256Async(file.Id, DebugFileHashes.ToSha256(content), CancellationToken.None);
        await Uploads.UploadEviction.TryEvictAsync(http, projectId);
        return TypedResults.Created($"/api/debug-files/{file.Id}",
            new DebugFileResponse(file.Id, file.BuildId, file.DebugId, file.ByteSize, file.CreatedAt));
    }

    // Each reason says what to send instead, since the caller is a CI log someone reads after a failed build.
    private static string ToMessage(ElfRejection? rejection) => rejection switch
    {
        ElfRejection.NotElf => "body is not an ELF file; send a .symbols file from --split-debug-info",
        ElfRejection.Unsupported => "only little-endian 32- or 64-bit ELF files are supported",
        ElfRejection.NoBuildId => "the file carries no GNU build ID, so no crash could be matched to it",
        ElfRejection.NoDebugInfo =>
            "the file has no DWARF debug info; send the .symbols file, not the stripped library that shipped",
        _ => "the ELF file is malformed",
    };
}
