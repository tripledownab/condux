using System.Net;
using System.Net.Http.Json;
using Condux.Core.DebugFiles;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// Calls the symbolizer service (ADR-0049). It hands the symbolizer a file's SHA-256, the file's bytes when
/// the symbolizer does not hold them, and offsets to resolve, and nothing else: no project, no credential and
/// no storage key, since the service parses files a stranger uploaded. An answer this caller does not expect
/// throws, and the read around it then shows the frames as they were. Each answer received in full is
/// counted (<see cref="SymbolizerMetrics"/>).
/// </summary>
public sealed class SymbolizerClient(HttpClient http, SymbolizerMetrics metrics)
{
    /// <summary>The named client registered for it, so a caller takes a fresh one from the factory.</summary>
    public const string HttpClientName = "symbolizer";

    /// <summary>Whether the symbolizer already holds the file with this hash.</summary>
    public async Task<bool> HoldsAsync(string sha256, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"files/{sha256}", cancellationToken);
        metrics.Record("holds", response.StatusCode);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent => true,
            HttpStatusCode.NotFound => false,
            _ => throw Unexpected(response),
        };
    }

    /// <summary>Hands the symbolizer a file. False when it refused the file as unusable.</summary>
    public async Task<bool> PutAsync(string sha256, byte[] content, CancellationToken cancellationToken)
    {
        using var body = new ByteArrayContent(content);
        using var response = await http.PutAsync($"files/{sha256}", body, cancellationToken);
        metrics.Record("put", response.StatusCode);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent => true,
            HttpStatusCode.BadRequest => false,
            _ => throw Unexpected(response),
        };
    }

    /// <summary>The frames for each address, or null when the file was evicted since it was checked.</summary>
    public async Task<ResolveResponse?> ResolveAsync(
        string sha256, IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(
            "resolve", new ResolveRequest(sha256, addresses), cancellationToken);
        metrics.Record("resolve", response.StatusCode);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => await response.Content.ReadFromJsonAsync<ResolveResponse>(cancellationToken),
            HttpStatusCode.NotFound => null,
            _ => throw Unexpected(response),
        };
    }

    // Busy (429), a failed run (502) or anything else: the read goes without this time.
    private static HttpRequestException Unexpected(HttpResponseMessage response) =>
        new($"the symbolizer answered {(int)response.StatusCode}", inner: null, response.StatusCode);
}
