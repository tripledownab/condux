using System.Text.Json;
using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.Http;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Mcp;

/// <summary>
/// The MCP server (ADR-0029): one JSON-RPC 2.0 endpoint an AI agent connects to, authed by a per-project
/// MCP token (<c>Authorization: Bearer</c>). It exposes the <see cref="McpToolCatalog"/> over the token's
/// project and nothing else. Streamable-HTTP shaped: a POST of one request returns one JSON response (no
/// SSE/streaming in v1); a notification (no id) is acknowledged with 202.
///
/// The token resolves to a project AND a capability (ADR-0046), which is what decides whether the caller
/// sees the triage tools at all. A token minted before that change reads as <c>read</c>, so it behaves
/// exactly as it did.
///
/// Two transport rules. Before the token is read, a request carrying a browser <c>Origin</c> that is
/// not this deployment's own is refused with 403, since MCP 2025-06-18 (basic/transports, "Security
/// Warning") requires the server to validate it. An agent outside a browser sends none, so absence
/// passes. And a 401 carries a <c>Bearer</c> challenge, which
/// <see cref="Condux.ControlPlane.Auth.AuthChallenge"/> adds for every bearer route; it names no OAuth
/// metadata because this server does not do OAuth.
/// </summary>
internal static class McpEndpoints
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/mcp", HandleAsync).WithName("mcp").ExcludeFromDescription();
        app.MapMcpServerCard();
    }

    private static async Task HandleAsync(
        HttpContext http, McpTokenRepository tokens, McpTools tools, IConfiguration config)
    {
        if (!IsAllowedOrigin(http.Request.Headers.Origin, config))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        // Auth is the tenant boundary: a live per-project MCP token presented as a bearer resolves to the
        // one project every tool then reads. No/invalid token → 401, before any method runs.
        if (BearerToken.From(http) is not { } raw ||
            await tokens.ResolveIdentityAsync(McpTokens.HashToken(raw), http.RequestAborted) is not { } token)
        {
            // The Bearer challenge, with error="invalid_token" for a refused token, comes from AuthChallenge.
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var context = new McpCallContext(
            token.ScopeId, token.TokenId, (McpCapability)token.Capability);

        using var doc = await TryParseAsync(http);
        if (doc is null)
        {
            await WriteError(http, null, -32700, "Parse error");
            return;
        }
        var root = doc.RootElement;
        // A request is an object (JSON-RPC 2.0 section 4); anything else has no member to read.
        if (root.ValueKind != JsonValueKind.Object)
        {
            await WriteError(http, null, -32600, "Invalid Request");
            return;
        }
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
                    serverInfo = McpServerCard.ServerInfo,
                });
                break;
            case "ping":
                await WriteResult(http, id, new { });
                break;
            case "tools/list":
                await WriteResult(http, id, new { tools = McpToolCatalog.DefinitionsFor(context.Capability) });
                break;
            case "tools/call":
                await CallToolAsync(http, id.Value, root, context, tools);
                break;
            default:
                await WriteError(http, id, -32601, "Method not found");
                break;
        }
    }

    private static async Task CallToolAsync(
        HttpContext http, JsonElement id, JsonElement root, McpCallContext context, McpTools tools)
    {
        var prms = root.TryGetProperty("params", out var p) ? p : default;
        var name = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("name", out var n)
            && n.ValueKind == JsonValueKind.String
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
            var data = await tools.CallAsync(name, args, context, http.RequestAborted);
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

    /// <summary>
    /// True when the request names no origin, or names one this deployment serves the dashboard from.
    /// Compared as scheme, host and port, which is all an <c>Origin</c> header carries, with a domain in
    /// the punycode form a browser sends. IdnHost alone would drop an IPv6 address's brackets.
    /// </summary>
    private static bool IsAllowedOrigin(string? origin, IConfiguration config)
    {
        if (string.IsNullOrEmpty(origin))
            return true;
        var own = AppOrigins.Parse(config["CONDUX_CORS_ORIGINS"])
            .Append(config["CONDUX_APP_BASE_URL"] ?? string.Empty);
        return own.Any(url => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            && string.Equals(ToOrigin(uri), origin, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToOrigin(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : uri.Host;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    // A request whose strings are not text is a parse error like any other malformed body: the document
    // would parse and the reads after it throw, answering 500 (JsonStrings says why). The body is read
    // whole first because the check needs its bytes. A leading UTF-8 byte order mark is dropped, as the
    // stream parse this replaced did; parsing bytes refuses one.
    private static async Task<JsonDocument?> TryParseAsync(HttpContext http)
    {
        using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body, http.RequestAborted);
        ReadOnlyMemory<byte> bytes = body.ToArray();
        if (bytes.Span.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }
        try
        {
            var doc = JsonDocument.Parse(bytes);
            if (JsonStrings.AreText(bytes.Span))
            {
                return doc;
            }
            doc.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

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
