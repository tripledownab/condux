using System.Net;
using System.Text;

namespace Condux.Sdk;

// POSTs a serialized event to the relay, retrying transient failures with backoff. Never throws (except
// on caller cancellation) — returns a SendResult describing the outcome. Mirrors the JS/Python SDK transports.
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

    // Honor Retry-After (seconds) on a 429; otherwise capped exponential backoff.
    private static TimeSpan Backoff(int attempt, HttpResponseMessage? response)
    {
        if (response?.StatusCode == HttpStatusCode.TooManyRequests
            && response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter;
        }

        var milliseconds = BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxBackoff.TotalMilliseconds));
    }
}
