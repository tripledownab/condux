using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Speaking the MCP endpoint's JSON-RPC over HTTP, for the tests that drive it. Shared rather than copied
/// per test class (the way <c>ProvisionAsync</c> is) because this encodes the wire protocol: how a request
/// is framed, and where a tool's data sits inside a result. Two classes restating that would be free to
/// drift from each other and from the endpoint.
/// </summary>
internal static class McpRpc
{
    /// <summary>A client that presents an MCP token as its bearer. There is no cookie path.</summary>
    public static HttpClient BearerClient(HttpClient client, string rawToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    /// <summary>One JSON-RPC request, asserting the transport succeeded so a caller can read the body.</summary>
    public static async Task<JsonElement> CallMethodAsync(
        HttpClient client, string method, object? prms = null)
    {
        var resp = await client.PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method, @params = prms });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A tools/call for one tool by name.</summary>
    public static Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments) =>
        CallMethodAsync(client, "tools/call", new { name, arguments });

    /// <summary>The names <c>tools/list</c> published.</summary>
    public static string[] ToolNames(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).ToArray();

    /// <summary>Whether the tool reported a tool-level failure (MCP puts these inside the result).</summary>
    public static bool IsError(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("isError").GetBoolean();

    /// <summary>The result's text content, which is the message on a failure and JSON on a success.</summary>
    public static string TextOf(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>A successful tool's structured data, which it serializes into that text content.</summary>
    public static JsonElement ToolJson(JsonElement rpc) => JsonDocument.Parse(TextOf(rpc)).RootElement;
}
