using Condux.Core.Http;

namespace Condux.Core.Llm;

/// <summary>
/// What an org may name as its model provider's base URL.
///
/// The value is supplied by an org admin and then fetched by the control plane, and later by the
/// conductor, both of which sit inside the deployment alongside Postgres, ClickHouse and the broker. So
/// an unchecked value is a request this platform makes on the caller's behalf, from the inside.
///
/// It lives in Core because the two consumers are in different deployments: the control plane validates
/// on write, and the agent core validates before use (<c>Condux.Agent</c> references Core and nothing
/// else, deliberately). See <see cref="HttpUrls"/> for what the check does and does not buy.
/// </summary>
public static class LlmUrls
{
    /// <summary>
    /// True when the value is safe to use as a provider base URL: absent, or an absolute http(s) URL.
    ///
    /// Absent passes because a provider whose endpoint is fixed (Anthropic) ignores it entirely. Whether
    /// a provider REQUIRES one is a separate question the caller already answers, and keeping the two
    /// apart is what stops this becoming a second, drifting copy of the provider table.
    /// </summary>
    public static bool IsValidBaseUrl(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) || HttpUrls.IsAbsoluteHttp(baseUrl);
}
