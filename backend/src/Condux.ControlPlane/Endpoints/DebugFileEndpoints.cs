using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Uploads;
using Condux.Core.DebugFiles;
using Condux.Core.SourceMaps;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Native debug file upload: CI sends each DWARF file a native build left behind (a Flutter build's
/// <c>--split-debug-info</c> output on Android, the app's dSYM on iOS), so frames that carry only an address
/// can later be resolved to code. Authenticated by the scoped release token, like source maps, and opt-in
/// the same way: with no object storage configured the route answers 404.
///
/// The request carries the file and nothing else. Which build it belongs to is read from the file itself
/// (an ELF GNU build ID, a Mach-O LC_UUID), so CI cannot attach a file to the wrong build by naming it
/// wrongly. A universal Mach-O file is stored one architecture per build, so each crash's image finds its
/// own slice and the symbolizer is never handed a file it must be told the architecture of.
/// </summary>
internal static class DebugFileEndpoints
{
    // A ceiling, not an estimate of a symbols file's size. The body is held in memory while it is checked
    // and stored, which the shared UploadBodyBudget counts.
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

                    var read = await UploadBodies.ReadAsync(http, MaxBytes);
                    if (read.IsRefused)
                    {
                        return read.Refusal;
                    }
                    using var body = read.Body;
                    return await StoreAsync(http, projectId, files, body.Content);
                })
            .WithName("uploadDebugFile").WithTags("DebugFiles");
    }

    private static async Task<Results<Created<DebugFileResponse>, UnauthorizedHttpResult, BadRequest<string>,
        NotFound, StatusCodeHttpResult>> StoreAsync(
        HttpContext http, long projectId, DebugFileRepository files, byte[] content)
    {
        var (slices, rejection) = DebugFileFormats.Read(content);
        if (slices is null)
        {
            return TypedResults.BadRequest(rejection);
        }

        var objectStore = http.RequestServices.GetRequiredService<IObjectStore>();
        var stored = new List<(DebugFile File, string? Architecture)>(slices.Count);
        foreach (var slice in slices)
        {
            stored.Add((await StoreSliceAsync(http, projectId, files, objectStore, content, slice), slice.Architecture));
        }
        await UploadEviction.TryEvictAsync(http, projectId);

        // The top-level fields name the first build, as they did when a file held only one; Files names all.
        var first = stored[0].File;
        var all = stored.Select(s => new DebugFileSliceResponse(
            s.File.Id, s.File.BuildId, s.File.DebugId, s.Architecture, s.File.ByteSize)).ToList();
        return TypedResults.Created($"/api/debug-files/{first.Id}",
            new DebugFileResponse(first.Id, first.BuildId, first.DebugId, first.ByteSize, first.CreatedAt, all));
    }

    private static async Task<DebugFile> StoreSliceAsync(
        HttpContext http, long projectId, DebugFileRepository files, IObjectStore objectStore, byte[] content,
        DebugFileSlice slice)
    {
        // A slice of the body as it lies, so a universal file's architectures are stored without copies.
        var bytes = content.AsMemory(slice.Offset, slice.Length);
        // The build ID is lowercase hex from the parser, so it is safe as a key segment as it stands.
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + slice.Build.BuildId;
        // The row first, so the quota counts and expiry reaches what is stored. The hash only after the store
        // write succeeds: the hash names the bytes the bucket holds, and a write that failed, on a re-upload,
        // leaves the previous upload's bytes there. A row with no hash is what the read path already handles,
        // by hashing the file it reads and recording that. A read in between may have hashed the bytes this
        // upload replaced and recorded that, so this write replaces any hash the row holds by now, and does
        // not take the request's token: a client that hangs up once the store write has returned would
        // otherwise leave that read's hash on the row.
        var file = await files.RecordAsync(projectId, slice.Build, key, bytes.Length, sha256: null, http.RequestAborted);
        await objectStore.PutAsync(key, bytes, "application/octet-stream", http.RequestAborted);
        await files.RecordStoredSha256Async(file.Id, DebugFileHashes.ToSha256(bytes.Span), CancellationToken.None);
        return file;
    }
}
