using System.Text.Json;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Auth;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Mcp;

/// <summary>
/// The MCP server (ADR-0029): one JSON-RPC 2.0 endpoint an AI agent connects to, authed by a per-project
/// MCP token (<c>Authorization: Bearer</c>). Read-only — it exposes the <see cref="McpTools"/> over the
/// token's project and nothing else. Streamable-HTTP shaped: a POST of one request returns one JSON
/// response (no SSE/streaming in v1); a notification (no id) is acknowledged with 202.
/// </summary>
internal static class McpEndpoints
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    public static void MapMcpEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/mcp", HandleAsync).WithName("mcp").ExcludeFromDescription();

    private static async Task HandleAsync(
        HttpContext http, McpTokenRepository tokens, IssueRepository issues, ClickHouseEventReader events,
        EventSymbolication symbolication)
    {
        // Auth is the tenant boundary: a live per-project MCP token presented as a bearer resolves to the
        // one project every tool then reads. No/invalid token → 401, before any method runs.
        if (BearerToken.From(http) is not { } raw ||
            await tokens.ResolveProjectAsync(McpTokens.HashToken(raw)) is not { } projectId)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var doc = await TryParseAsync(http);
        if (doc is null)
        {
            await WriteError(http, null, -32700, "Parse error");
            return;
        }
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : (JsonElement?)null;
        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;

        // A JSON-RPC notification (no id) — e.g. notifications/initialized — is acknowledged, no body.
        if (id is null)
        {
            http.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }
        if (method is null)
        {
            await WriteError(http, id, -32600, "Invalid Request");
            return;
        }

        switch (method)
        {
            case "initialize":
                await WriteResult(http, id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "condux", title = "Condux", version = "1.0.0" },
                });
                break;
            case "ping":
                await WriteResult(http, id, new { });
                break;
            case "tools/list":
                await WriteResult(http, id, new { tools = McpTools.Definitions });
                break;
            case "tools/call":
                await CallToolAsync(http, id.Value, root, projectId, issues, events, symbolication);
                break;
            default:
                await WriteError(http, id, -32601, "Method not found");
                break;
        }
    }

    private static async Task CallToolAsync(
        HttpContext http, JsonElement id, JsonElement root, long projectId,
        IssueRepository issues, ClickHouseEventReader events, EventSymbolication symbolication)
    {
        var prms = root.TryGetProperty("params", out var p) ? p : default;
        var name = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("name", out var n)
            ? n.GetString()
            : null;
        if (string.IsNullOrEmpty(name))
        {
            await WriteError(http, id, -32602, "Invalid params: name is required");
            return;
        }
        var args = prms.TryGetProperty("arguments", out var a) ? a : default;
        try
        {
            var data = await McpTools.CallAsync(
                name, args, projectId, issues, events, symbolication, http.RequestAborted);
            await WriteResult(http, id, new
            {
                content = new[] { Text(JsonSerializer.Serialize(data, JsonOpts)) },
                isError = false,
            });
        }
        catch (McpToolException ex)
        {
            // A tool-level failure is reported inside the result (isError), per MCP, not as a protocol error.
            await WriteResult(http, id, new { content = new[] { Text(ex.Message) }, isError = true });
        }
    }

    private static async Task<JsonDocument?> TryParseAsync(HttpContext http)
    {
        try
        {
            return await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object Text(string text) => new { type = "text", text };

    private static Task WriteResult(HttpContext http, JsonElement? id, object result) =>
        WriteAsync(http, new { jsonrpc = "2.0", id, result });

    private static Task WriteError(HttpContext http, JsonElement? id, int code, string message) =>
        WriteAsync(http, new { jsonrpc = "2.0", id, error = new { code, message } });

    private static Task WriteAsync(HttpContext http, object body)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOpts));
    }
}
