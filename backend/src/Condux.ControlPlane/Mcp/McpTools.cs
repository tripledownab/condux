using System.Globalization;
using System.Text.Json;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.Issues;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Auth;
using Condux.Core.Issues;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Mcp;

/// <summary>Raised by a tool for a caller error (unknown tool, bad id, not found, refused); the endpoint
/// reports it as an MCP tool error (result with <c>isError</c>), not a JSON-RPC protocol error.</summary>
internal sealed class McpToolException(string message) : Exception(message);

/// <summary>Who is calling and what they may do: one live MCP token, resolved once per request.</summary>
internal readonly record struct McpCallContext(long ProjectId, Guid TokenId, McpCapability Capability);

/// <summary>
/// The handlers behind <see cref="McpToolCatalog"/> (ADR-0029, ADR-0046). Every tool reuses an existing
/// path (the issue read paths, <see cref="IssueTriage"/>, the notes repository), so there is one source of
/// truth for filtering, paging, the already-scrubbed payload and the side effects of a triage change.
///
/// Everything is scoped to the token's project and there is no cross-project reach. A write additionally
/// needs the token's capability and a slot from <see cref="McpWriteLimiter"/>, checked here rather than at
/// the endpoint so no dispatch path can skip them.
/// </summary>
internal sealed class McpTools(
    IssueRepository issues, ClickHouseEventReader events, EventSymbolication symbolication,
    IssueTriage triage, IssueNoteRepository notes, McpWriteLimiter writeLimiter)
{
    private const int DefaultPageSize = 25;
    private const string DefaultSort = "lastSeen";

    /// <summary>Run a tool by name; returns the structured result data serialized into the tool response.
    /// Event-returning tools de-minify frames through the same read-time symbolication the dashboard uses.</summary>
    public async Task<object> CallAsync(
        string name, JsonElement args, McpCallContext context, CancellationToken ct)
    {
        // The catalog decides what exists and what it costs, so an unpublished tool cannot be reached by
        // guessing its name, and a tool the caller's list omitted cannot be reached by hardcoding it.
        var required = McpToolCatalog.RequiredFor(name)
            ?? throw new McpToolException($"unknown tool: {name}");
        if (context.Capability < required)
        {
            throw new McpToolException(
                $"this token cannot call {name}: it needs the "
                + $"{McpCapabilities.Name(required)} capability, and has {McpCapabilities.Name(context.Capability)}. "
                + "Mint a new token with that capability in the project's MCP settings.");
        }
        if (required > McpCapability.Read && !await writeLimiter.TryWriteAsync(context.TokenId, ct))
        {
            throw new McpToolException("too many writes from this token; wait a moment and retry");
        }

        return name switch
        {
            "list_issues" => await ListIssuesAsync(args, context.ProjectId, ct),
            "get_issue" => await GetIssueAsync(args, context.ProjectId, ct),
            "list_issue_events" => await ListIssueEventsAsync(args, context.ProjectId, ct),
            "set_issue_status" => await SetIssueStatusAsync(args, context.ProjectId, ct),
            "add_issue_note" => await AddIssueNoteAsync(args, context, ct),
            // Unreachable while the catalog and this switch agree, which a test pins by calling every
            // catalog entry. It stays because a handler-less catalog entry must not fall through as success.
            _ => throw new McpToolException($"unknown tool: {name}"),
        };
    }

    private async Task<object> ListIssuesAsync(JsonElement args, long projectId, CancellationToken ct)
    {
        var paging = ResolvePaging(Int(args, "limit"), offset: null);
        var sort = Str(args, "sort") ?? DefaultSort;
        var (page, hasMore) = await issues.ListPageAsync(
            projectId, IssueQuery.Parse(Str(args, "query")), currentUserId: 0, sort, paging.Limit, paging.Offset, ct);
        return new { issues = page, hasMore };
    }

    private async Task<object> GetIssueAsync(JsonElement args, long projectId, CancellationToken ct)
    {
        var found = await FindAsync(args, projectId, ct);
        var recent = await symbolication.SymbolicateAsync(
            projectId, await events.RecentByIssueAsync(Project(projectId), (ulong)found.InternalId, limit: 1, ct), ct);
        return new { issue = found.Summary, latestEvent = recent.Count > 0 ? recent[0] : null };
    }

    private async Task<object> ListIssueEventsAsync(JsonElement args, long projectId, CancellationToken ct)
    {
        var paging = ResolvePaging(Int(args, "limit"), Int(args, "offset"));
        var found = await FindAsync(args, projectId, ct);
        var page = await events.PageByIssueAsync(
            Project(projectId), (ulong)found.InternalId, paging.Limit + 1, paging.Offset, ct);
        var hasMore = page.Count > paging.Limit;
        var window = hasMore ? page.Take(paging.Limit).ToList() : page;
        return new { events = await symbolication.SymbolicateAsync(projectId, window, ct), hasMore };
    }

    // Set the status through the shared triage service, so an agent's resolve fires the project's alert
    // rules and reaches open dashboards exactly as a dashboard resolve does.
    private async Task<object> SetIssueStatusAsync(JsonElement args, long projectId, CancellationToken ct)
    {
        var issueId = IssueIdArg(args);
        var name = Str(args, "status")?.Trim().ToLowerInvariant();
        var status = name switch
        {
            "unresolved" => 1,
            "resolved" => 2,
            "ignored" => 3,
            _ => throw new McpToolException("status must be one of: resolved, ignored, unresolved"),
        };
        if (!await triage.SetStatusAsync(projectId, issueId, status, ct))
        {
            throw new McpToolException("issue not found");
        }
        // Echo the NAME, not the stored 1/2/3. The tool takes named states precisely because the numbers
        // are a poor contract for a model, and this tool's description never explains them, so returning
        // one would hand back a value the caller has no key for.
        return new { issueId, status = name };
    }

    // Attributed to the token rather than a user: an MCP caller has no account, and a note whose author is
    // simply blank would read as one written by someone who left.
    private async Task<object> AddIssueNoteAsync(
        JsonElement args, McpCallContext context, CancellationToken ct)
    {
        if (!IssueNoteText.TryNormalize(Str(args, "body"), out var body, out var error))
        {
            // The default surfaces the code rather than guessing. A two-way ternary here would render a
            // future third code as "body is required", which is a wrong instruction, not a vague one.
            throw new McpToolException(error switch
            {
                "note_too_long" => $"body must be {IssueNoteText.MaxLength} characters or fewer",
                "note_body_required" => "body is required",
                _ => error,
            });
        }
        var found = await FindAsync(args, context.ProjectId, ct);
        var note = await notes.AddAsync(
            found.InternalId, NoteAuthor.McpToken(context.TokenId), body, ct);
        return new { noteId = note.Id, createdAt = note.CreatedAt };
    }

    // Resolve optional tool paging args (an omitted limit uses the default) to a bounded window, or a tool
    // error for an out-of-range request — the agent's input is surfaced, never silently clamped.
    private static PageParams ResolvePaging(int? limit, int? offset) =>
        PageParams.Resolve(limit, offset, DefaultPageSize)
            ?? throw new McpToolException($"limit must be 1..{PageParams.MaxLimit} and offset must be >= 0");

    // Resolve the required issueId argument to a record in the token's project, or a tool error.
    private async Task<IssueRecord> FindAsync(JsonElement args, long projectId, CancellationToken ct) =>
        await issues.GetByPublicIdAsync(projectId, IssueIdArg(args), ct)
        ?? throw new McpToolException("issue not found");

    private static Guid IssueIdArg(JsonElement args) =>
        Guid.TryParse(Str(args, "issueId"), out var issueId)
            ? issueId
            : throw new McpToolException("issueId must be a UUID");

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
