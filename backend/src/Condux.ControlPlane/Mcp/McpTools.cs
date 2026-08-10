using System.Globalization;
using System.Text.Json;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Issues;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Mcp;

/// <summary>Raised by a tool for a caller error (unknown tool, bad id, not found); the endpoint reports it
/// as an MCP tool error (result with <c>isError</c>), not a JSON-RPC protocol error.</summary>
internal sealed class McpToolException(string message) : Exception(message);

/// <summary>
/// The read-only tools the MCP endpoint (ADR-0029) exposes over a project's already-scrubbed issue store.
/// The catalog (name + JSON schema) and the handlers live together so tools/list can't drift from
/// tools/call. Every tool reuses the existing read paths and is scoped to the token's project; there is no
/// mutate path and no cross-project reach.
/// </summary>
internal static class McpTools
{
    private const string LevelHelp = "1 debug, 2 info, 3 warning, 4 error, 5 fatal";
    private const int DefaultPageSize = 25;
    private const string DefaultSort = "lastSeen";

    /// <summary>The tool catalog advertised by tools/list (data-driven, the single source for both calls).</summary>
    public static IReadOnlyList<object> Definitions { get; } =
    [
        new
        {
            name = "list_issues",
            description =
                "List the project's issues (grouped, deduplicated errors), newest-first by default. Optional "
                + "`query` filters with the dashboard search grammar: free text over title/culprit, plus tokens "
                + "is:unresolved|resolved|ignored, level:<name>, is:assigned, is:unassigned. Each issue's `level` "
                + "is " + LevelHelp + "; `status` is 1 unresolved, 2 resolved, 3 ignored.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search + filter tokens (optional)." },
                    sort = new { type = "string", @enum = new[] { "lastSeen", "firstSeen", "events", "severity" } },
                    limit = new { type = "integer", description = "1..100, default 25." },
                },
            },
        },
        new
        {
            name = "get_issue",
            description =
                "Get one issue by its id (the UUID from list_issues) plus its most recent sampled event "
                + "(stack trace, message, metadata) for debugging.",
            inputSchema = new
            {
                type = "object",
                properties = new { issueId = new { type = "string", description = "The issue's UUID." } },
                required = new[] { "issueId" },
            },
        },
        new
        {
            name = "list_issue_events",
            description =
                "List an issue's sampled events (occurrences), newest-first and paginated, to see how an error "
                + "varies across occurrences.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    issueId = new { type = "string", description = "The issue's UUID." },
                    limit = new { type = "integer", description = "1..100, default 25." },
                    offset = new { type = "integer", description = "0-based, default 0." },
                },
                required = new[] { "issueId" },
            },
        },
    ];

    /// <summary>Run a tool by name; returns the structured result data serialized into the tool response.
    /// Event-returning tools de-minify frames through the same read-time symbolication the dashboard uses.</summary>
    public static Task<object> CallAsync(
        string name, JsonElement args, long projectId, IssueRepository issues,
        ClickHouseEventReader events, EventSymbolication symbolication, CancellationToken ct) => name switch
        {
            "list_issues" => ListIssuesAsync(args, projectId, issues, ct),
            "get_issue" => GetIssueAsync(args, projectId, issues, events, symbolication, ct),
            "list_issue_events" => ListIssueEventsAsync(args, projectId, issues, events, symbolication, ct),
            _ => throw new McpToolException($"unknown tool: {name}"),
        };

    private static async Task<object> ListIssuesAsync(
        JsonElement args, long projectId, IssueRepository issues, CancellationToken ct)
    {
        var paging = ResolvePaging(Int(args, "limit"), offset: null);
        var sort = Str(args, "sort") ?? DefaultSort;
        var (page, hasMore) = await issues.ListPageAsync(
            projectId, IssueQuery.Parse(Str(args, "query")), currentUserId: 0, sort, paging.Limit, paging.Offset, ct);
        return new { issues = page, hasMore };
    }

    private static async Task<object> GetIssueAsync(
        JsonElement args, long projectId, IssueRepository issues, ClickHouseEventReader events,
        EventSymbolication symbolication, CancellationToken ct)
    {
        var found = await FindAsync(args, projectId, issues, ct);
        var recent = await symbolication.SymbolicateAsync(
            projectId, await events.RecentByIssueAsync(Project(projectId), (ulong)found.InternalId, limit: 1, ct), ct);
        return new { issue = found.Summary, latestEvent = recent.Count > 0 ? recent[0] : null };
    }

    private static async Task<object> ListIssueEventsAsync(
        JsonElement args, long projectId, IssueRepository issues, ClickHouseEventReader events,
        EventSymbolication symbolication, CancellationToken ct)
    {
        var paging = ResolvePaging(Int(args, "limit"), Int(args, "offset"));
        var found = await FindAsync(args, projectId, issues, ct);
        var page = await events.PageByIssueAsync(
            Project(projectId), (ulong)found.InternalId, paging.Limit + 1, paging.Offset, ct);
        var hasMore = page.Count > paging.Limit;
        var window = hasMore ? page.Take(paging.Limit).ToList() : page;
        return new { events = await symbolication.SymbolicateAsync(projectId, window, ct), hasMore };
    }

    // Resolve optional tool paging args (an omitted limit uses the default) to a bounded window, or a tool
    // error for an out-of-range request — the agent's input is surfaced, never silently clamped.
    private static PageParams ResolvePaging(int? limit, int? offset) =>
        PageParams.Resolve(limit, offset, DefaultPageSize)
            ?? throw new McpToolException($"limit must be 1..{PageParams.MaxLimit} and offset must be >= 0");

    // Resolve the required issueId argument to a record in the token's project, or a tool error.
    private static async Task<IssueRecord> FindAsync(
        JsonElement args, long projectId, IssueRepository issues, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(args, "issueId"), out var issueId))
        {
            throw new McpToolException("issueId must be a UUID");
        }
        return await issues.GetByPublicIdAsync(projectId, issueId, ct)
            ?? throw new McpToolException("issue not found");
    }

    private static string Project(long projectId) => projectId.ToString(CultureInfo.InvariantCulture);

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;
}
