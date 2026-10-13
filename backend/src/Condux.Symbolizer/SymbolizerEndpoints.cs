using Condux.Core.DebugFiles;
using System.Globalization;
using System.Text.Json;
using Condux.Core.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Condux.Symbolizer;

/// <summary>
/// The three calls a caller makes: whether a file is held, hand it over, and resolve offsets in it. They are
/// for the services that run read-time symbolication, over the internal network; no port of the service
/// is published. Every input is bounded before it is held: the hash by shape, the file by size, and a batch by
/// its body size and then its address count.
/// </summary>
internal static class SymbolizerEndpoints
{
    // Enough for a batch at the address cap with room to spare, read before the JSON is parsed, so a large
    // body cannot become millions of strings before the address count refuses it.
    internal const int MaxResolveBodyBytes = 64 * 1024;

    public static void MapSymbolizerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/files/{hash}", (string hash, DebugFileCache cache) =>
            !DebugFileCache.IsHash(hash) ? Results.BadRequest("not a SHA-256")
            : cache.Find(hash) is null ? Results.NotFound()
            : Results.NoContent());

        app.MapPut("/files/{hash}", async (string hash, HttpContext http, DebugFileCache cache,
            SymbolizerOptions options, FileUploadGate gate) =>
        {
            if (!DebugFileCache.IsHash(hash))
            {
                return Results.BadRequest("not a SHA-256");
            }
            if (!gate.TryEnter())
            {
                return Busy(http);
            }
            try
            {
                return await StoreAsync(hash, http, cache, options);
            }
            finally
            {
                gate.Leave();
            }
        });

        app.MapPost("/resolve", async (HttpContext http, DebugFileCache cache, LlvmSymbolizerRunner runner,
            SymbolizerOptions options, ResolveGate gate) =>
        {
            var (body, failure) = await ReadAsync(http, MaxResolveBodyBytes);
            if (failure is not null)
            {
                return failure;
            }
            if (ToRequest(body!) is not { } request || !DebugFileCache.IsHash(request.File))
            {
                return Results.BadRequest("send {\"file\": a SHA-256, \"addresses\": [...]}");
            }
            // A field the body leaves out arrives as null whatever the record says, so the count is read
            // through a pattern rather than off the property.
            if (request.Addresses is not { Count: > 0 } addresses || addresses.Count > options.MaxAddresses)
            {
                return Results.BadRequest($"send between 1 and {options.MaxAddresses} addresses");
            }
            if (ToOffsets(addresses) is not { } offsets)
            {
                return Results.BadRequest("addresses are 0x-prefixed hex");
            }
            if (cache.Find(request.File) is not { } file)
            {
                return Results.NotFound();
            }
            if (!gate.TryEnter())
            {
                return Busy(http);
            }

            IReadOnlyList<string>? lines;
            try
            {
                lines = await runner.RunAsync(file, offsets, http.RequestAborted);
            }
            finally
            {
                gate.Leave();
            }
            if (lines is null)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            var results = addresses
                .Select((address, i) => new ResolvedAddress(address, SymbolizerOutput.ToFrames(lines[i])))
                .ToList();
            return Results.Ok(new ResolveResponse(results));
        });

        app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));
    }

    private static async Task<IResult> StoreAsync(
        string hash, HttpContext http, DebugFileCache cache, SymbolizerOptions options)
    {
        // The cap sits above Kestrel's default request limit, so the limit is raised to it for this route
        // alone where the host allows, and the read is bounded as well for hosts that do not.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = options.MaxFileBytes;
        }

        var (content, failure) = await ReadAsync(http, options.MaxFileBytes);
        return failure
            ?? (cache.Store(hash, content!) is { } refusal ? Results.BadRequest(refusal) : Results.NoContent());
    }

    // Full: the caller is to try later, and a reader of symbolicated frames simply goes without this time.
    private static IResult Busy(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    // The body, or the answer for one that cannot be read: past the cap, or refused by Kestrel itself, as
    // too large, cut short or too slow. Each of those is the caller's to fix, so it gets Kestrel's status.
    private static async Task<(byte[]? Body, IResult? Failure)> ReadAsync(HttpContext http, long cap)
    {
        try
        {
            return await RequestBodies.ReadBoundedAsync(http.Request.Body, cap, http.RequestAborted) is { } body
                ? (body, null)
                : (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
        catch (BadHttpRequestException e)
        {
            return (null, Results.StatusCode(e.StatusCode));
        }
    }

    private static ResolveRequest? ToRequest(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<ResolveRequest>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ulong>? ToOffsets(IReadOnlyList<string> addresses)
    {
        var offsets = new List<ulong>(addresses.Count);
        foreach (var address in addresses)
        {
            if (address is null
                || !address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !ulong.TryParse(address.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset))
            {
                return null;
            }
            offsets.Add(offset);
        }
        return offsets;
    }
}
