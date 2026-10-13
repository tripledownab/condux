using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.Core.FixEngine;

namespace Condux.Agent.ManagedAgents;

/// <summary>The vendor rejected or throttled a call; carries the status so the gateway can treat a
/// 429 as "still running" on the poll path instead of failing the run.</summary>
public sealed class ManagedAgentsApiException(HttpStatusCode statusCode, string message)
    : FixFailedException(FixFailures.ToModelReason(statusCode), message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool IsRateLimit => StatusCode == HttpStatusCode.TooManyRequests;
}

/// <summary>
/// A thin HTTP layer over the Anthropic Managed Agents beta (ADR-0038) — the same plain-HttpClient
/// shape as <see cref="AnthropicMessagesClient"/>, no SDK. Every call carries the beta header; errors
/// surface as <see cref="ManagedAgentsApiException"/> carrying the status and the vendor's message,
/// reduced by <see cref="ModelApiError"/> and bounded by it. Retry/backoff is the caller's job (the fix
/// provider's poll interval is the backoff), and it keys on the status, never the text.
/// </summary>
public sealed class ManagedAgentsClient(HttpClient http, ManagedAgentsOptions options)
{
    private const string Beta = "managed-agents-2026-04-01";

    internal async Task<WireAgent> CreateAgentAsync(
        string name, string model, string system, IReadOnlyList<WireToolRef> tools,
        CancellationToken ct = default) =>
        await PostAsync<WireAgent>("/v1/agents", new WireAgentCreate(name, model, system, tools), ct);

    /// <summary>Every agent under the key, archived ones included. A page holds 20 by default, so one page
    /// would miss ours on a key with more.</summary>
    internal Task<IReadOnlyList<WireAgent>> ListAgentsAsync(CancellationToken ct = default) =>
        ReadAllPagesAsync<WireAgent>("/v1/agents?limit=100&include_archived=true", ct);

    internal async Task<WireEnvironment> CreateEnvironmentAsync(
        string name, WireEnvironmentConfig config, CancellationToken ct = default) =>
        await PostAsync<WireEnvironment>("/v1/environments", new WireEnvironmentCreate(name, config), ct);

    internal async Task<WireEnvironment> UpdateEnvironmentAsync(
        string environmentId, WireEnvironmentConfig config, CancellationToken ct = default) =>
        await PostAsync<WireEnvironment>($"/v1/environments/{environmentId}", new WireEnvironmentUpdate(config), ct);

    /// <summary>Every unarchived environment under the key, for the same reason as the agents: a page holds
    /// 20 by default, and an environment past the first page would be created again on every start.</summary>
    internal Task<IReadOnlyList<WireEnvironment>> ListEnvironmentsAsync(CancellationToken ct = default) =>
        ReadAllPagesAsync<WireEnvironment>("/v1/environments?limit=100", ct);

    internal async Task<WireSession> CreateSessionAsync(
        WireSessionCreate request, CancellationToken ct = default) =>
        await PostAsync<WireSession>("/v1/sessions", request, ct);

    internal async Task<WireSession> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
        await GetAsync<WireSession>($"/v1/sessions/{sessionId}", ct);

    /// <summary>All of a session's events, NEWEST first (the API defaults to oldest first), following
    /// pagination up to a runaway cap. The caller needs the latest of them, so reading newest first means
    /// the cap can only drop the oldest.</summary>
    internal Task<IReadOnlyList<WireEvent>> GetSessionEventsAsync(
        string sessionId, CancellationToken ct = default) =>
        ReadAllPagesAsync<WireEvent>($"/v1/sessions/{sessionId}/events?limit=100&order=desc", ct);

    // The most pages one list read follows.
    private const int MaxPages = 50;

    /// <summary>Every item of a paged list, following the vendor's <c>next_page</c> cursor. The page cap is a
    /// runaway bound against a cursor that never ends, past which the later pages are dropped.</summary>
    private async Task<IReadOnlyList<T>> ReadAllPagesAsync<T>(string pathWithQuery, CancellationToken ct)
    {
        var items = new List<T>();
        string? page = null;
        for (var i = 0; i < MaxPages; i++)
        {
            var list = await GetAsync<WirePage<T>>(
                pathWithQuery + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}"), ct);
            items.AddRange(list.Data);
            if (list.NextPage is null or "")
            {
                break;
            }
            page = list.NextPage;
        }
        return items;
    }

    /// <summary>One page of one agent's sessions in the given statuses (none given: every status) created
    /// before an instant, oldest first, and the cursor for the next page (null on the last).</summary>
    internal async Task<WirePage<WireSessionSummary>> ListSessionsAsync(
        string agentId, IReadOnlyList<string> statuses, DateTimeOffset createdBefore, string? page,
        CancellationToken ct = default)
    {
        // The API takes `statuses[]`, the form the vendor's own SDKs send, and refuses a bare `statuses`
        // with a 400, although its reference names the parameter without the brackets.
        var query = string.Concat(statuses.Select(s => $"&statuses%5B%5D={Uri.EscapeDataString(s)}"));
        // Invariant, or a server whose culture counts years differently sends a cutoff in another century.
        var before = Uri.EscapeDataString(
            createdBefore.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var path = $"/v1/sessions?agent_id={Uri.EscapeDataString(agentId)}&limit=100&order=asc"
            + $"&created_at%5Blt%5D={before}{query}"
            + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}");
        return await GetAsync<WirePage<WireSessionSummary>>(path, ct);
    }

    /// <summary>Stop a running session. The vendor lets it reach a safe boundary and then go idle, which
    /// ends its spend. A running session cannot be deleted, so this is the only way to stop one.</summary>
    internal async Task InterruptSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Post, $"/v1/sessions/{sessionId}/events");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new WireSessionEvents([new WireInterrupt()])), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Best-effort cleanup of a finalized session; a failure is the caller's to swallow.</summary>
    internal async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Delete, $"/v1/sessions/{sessionId}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Dollars to the wire's integer minor-unit amount ("100" = $1.00, probed live).</summary>
    internal static string MinorUnits(decimal usd) =>
        ((long)decimal.Round(usd * 100m, 0, MidpointRounding.ToZero)).ToString();

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, body.GetType()), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{options.BaseUrl}{path}");
        request.Headers.Add("x-api-key", options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", Beta);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new InvalidOperationException("Managed Agents API returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new ManagedAgentsApiException(response.StatusCode,
            $"Managed Agents API returned {(int)response.StatusCode}: {ModelApiError.Describe(detail)}");
    }
}
