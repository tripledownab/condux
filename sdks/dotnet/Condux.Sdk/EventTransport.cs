using System.Globalization;
using System.Net;
using System.Text;

namespace Condux.Sdk;

// POSTs a serialized event to the relay, retrying transient failures with backoff. Never throws (except
// on caller cancellation): it returns a SendResult describing the outcome.
//
// The retry schedule is identical to every other Condux SDK's, pinned by sdks/conformance/backoff.tsv,
// which BackoffConformanceTests drives through the public send path. The citation is the point: the
// claim is otherwise made in a comment and checked nowhere.
internal static class EventTransport
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public static async Task<SendResult> SendAsync(
        HttpClient http, string url, string publicKey, string body, int maxRetries,
        Func<TimeSpan, CancellationToken, Task> sleep, CancellationToken cancellationToken)
    {
        int? lastStatus = null;
        string? lastError = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("x-condux-auth", publicKey);
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex.Message; // a request timeout, not caller cancellation
            }

            if (response is not null)
            {
                lastStatus = (int)response.StatusCode;
                lastError = null;
                if (response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    return new SendResult(true, attempt + 1, lastStatus);
                }

                if (!IsRetriable(response.StatusCode))
                {
                    response.Dispose();
                    return new SendResult(false, attempt + 1, lastStatus);
                }
            }

            if (attempt == maxRetries)
            {
                response?.Dispose();
                break;
            }

            await sleep(Backoff(attempt, response), cancellationToken);
            response?.Dispose();
        }

        return new SendResult(false, maxRetries + 1, lastStatus, lastError);
    }

    // 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN, bad payload) are not.
    private static bool IsRetriable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    // Honor Retry-After (seconds) on a 429, else exponential backoff. Both paths are capped at
    // MaxBackoff, at ONE return so a later branch cannot route past it: the SDK holds the caller's
    // thread while it waits, so bounding that wait is its own obligation and not the relay's to set.
    private static TimeSpan Backoff(int attempt, HttpResponseMessage? response)
    {
        var requested = response?.StatusCode == HttpStatusCode.TooManyRequests
            ? ParseRetryAfter(response)
            : null;
        var milliseconds = requested?.TotalMilliseconds
                           ?? BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxBackoff.TotalMilliseconds));
    }

    // Read Retry-After as the string it arrived as, not through Headers.RetryAfter. That typed property
    // exposes a Delta only for RFC 7231 delta-seconds, a non-negative INTEGER, so it cannot represent a
    // fractional value at all, and a proxy or a CDN in front of the relay is where fractional values come
    // from. Negative is clamped rather than discarded, so a sender merely wrong about the sign is read as
    // asking to retry now.
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        // IsFinite is load-bearing, not defensive. TryParse accepts "Infinity" and "NaN", and
        // TimeSpan.FromSeconds throws OverflowException on the first and ArgumentException on the
        // second, out of a transport whose whole contract is that it never throws. Not finite is not
        // an instruction.
        var raw = values.FirstOrDefault();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(Math.Max(0, seconds))
            : null;
    }
}
