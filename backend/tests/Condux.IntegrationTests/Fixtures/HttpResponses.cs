using System.Net;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// A failed response's status AND body in the failure message. An unhandled exception in a host built with
/// <see cref="ServerExceptions"/> reaches the test on its own, so the body here is that of an answer: the
/// error code of a 4xx or a 503, or the error text ClickHouse puts in its body.
/// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> throws that away, and
/// <c>HttpResponsesScanTest</c> keeps it out of these tests.
/// </summary>
public static class HttpResponses
{
    // Enough for an exception and its innermost frames; a page of HTML from somewhere else is cut short.
    private const int MaxBodyChars = 8_000;

    /// <summary>Throws with the status, the request and the body when the response failed. A successful
    /// response's body is left unread, so a streamed one stays open for the caller.</summary>
    public static async Task EnsureSuccessAsync(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowAsync(response, "a success");
        }
    }

    /// <summary>The same for one expected status, for a shared helper whose callers read the body next.
    /// The body is read only when the status is wrong.</summary>
    public static async Task EnsureStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            await ThrowAsync(response, $"{(int)expected}");
        }
    }

    private static async Task ThrowAsync(HttpResponseMessage response, string expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > MaxBodyChars)
        {
            body = body[..MaxBodyChars] + "\n...";
        }
        var request = response.RequestMessage;
        throw new HttpRequestException(
            $"expected {expected}, got {(int)response.StatusCode} {response.ReasonPhrase} from {request?.Method} "
            + $"{request?.RequestUri?.PathAndQuery}:\n{body}",
            inner: null,
            response.StatusCode);
    }
}
