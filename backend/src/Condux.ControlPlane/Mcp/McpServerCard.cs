using Condux.Core.Auth;

namespace Condux.ControlPlane.Mcp;

/// <summary>
/// Who the MCP server says it is, and the static server card that publishes it with no token.
///
/// <para>The card exists for directories that scan a server before listing it. Smithery's scan connects
/// without credentials, receives the 401 and stops, so it never sees a tool; it reads this file instead,
/// at the path its publishing docs name, and the shape is their subset of the SEP-1649 draft. The tools are
/// every tool at the highest capability, built from <see cref="McpToolCatalog"/>, so the card cannot
/// list a tool <c>tools/list</c> does not serve. A token sees only what its capability allows.</para>
/// </summary>
internal static class McpServerCard
{
    public const string Path = "/.well-known/mcp/server-card.json";

    /// <summary>The <c>serverInfo</c> both <c>initialize</c> and the card report.</summary>
    public static readonly object ServerInfo = new { name = "condux", title = "Condux", version = "1.0.0" };

    private static readonly object Card = new
    {
        serverInfo = ServerInfo,
        authentication = new { required = true, schemes = new[] { "bearer" } },
        tools = McpToolCatalog.DefinitionsFor(Enum.GetValues<McpCapability>().Max()),
        resources = Array.Empty<object>(),
        prompts = Array.Empty<object>(),
    };

    public static void MapMcpServerCard(this IEndpointRouteBuilder app) =>
        app.MapGet(Path, () => Results.Json(Card)).WithName("mcpServerCard").ExcludeFromDescription();
}
