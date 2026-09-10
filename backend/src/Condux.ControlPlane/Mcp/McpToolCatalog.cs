using Condux.Core.Auth;

namespace Condux.ControlPlane.Mcp;

/// <summary>
/// One tool: what it is called, what a token must be able to do to reach it, and the pieces
/// <c>tools/list</c> publishes.
///
/// The published definition is BUILT from the name rather than repeating it, so the name a client reads
/// and the name <c>tools/call</c> dispatches on cannot drift apart.
/// </summary>
internal sealed record McpToolSpec(
    string Name, McpCapability Required, string Description, object InputSchema, object Annotations)
{
    public object Definition => new
    {
        name = Name,
        description = Description,
        inputSchema = InputSchema,
        annotations = Annotations,
    };
}

/// <summary>
/// The tools the MCP endpoint publishes, and the capability each one needs (ADR-0029, ADR-0046). One list,
/// read by both <c>tools/list</c> and the capability check in <c>tools/call</c>, because filtering the
/// published list alone is not access control: a client that hardcodes a tool name never reads the list.
///
/// Every entry declares MCP tool annotations. The protocol version this server advertises defines
/// <c>readOnlyHint</c> and friends, and that is how a client knows to ask its human before a write. The
/// read tools state <c>readOnlyHint: true</c> explicitly rather than leave it out, so a client never has
/// to decide what a missing annotation means.
/// </summary>
internal static class McpToolCatalog
{
    private const string LevelHelp = "1 debug, 2 info, 3 warning, 4 error, 5 fatal";

    private static readonly object ReadOnly = new { readOnlyHint = true, openWorldHint = false };

    // Setting the same status twice lands the same state, so it is idempotent; it is reversible by
    // setting another, so it is not destructive.
    private static readonly object ReversibleWrite = new
    {
        readOnlyHint = false,
        destructiveHint = false,
        idempotentHint = true,
        openWorldHint = false,
    };

    // Each call adds another note, so it is not idempotent. It creates rather than overwrites, so
    // nothing is destroyed.
    private static readonly object AppendingWrite = new
    {
        readOnlyHint = false,
        destructiveHint = false,
        idempotentHint = false,
        openWorldHint = false,
    };

    private static readonly IReadOnlyList<McpToolSpec> All =
    [
        new(
            "list_issues",
            McpCapability.Read,
            "List the project's issues (grouped, deduplicated errors), newest-first by default. Optional "
            + "`query` filters with the dashboard search grammar: free text over title/culprit, plus tokens "
            + "is:unresolved|resolved|ignored, level:<name>, is:assigned, is:unassigned. Each issue's `level` "
            + "is " + LevelHelp + "; `status` is 1 unresolved, 2 resolved, 3 ignored.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search + filter tokens (optional)." },
                    sort = new { type = "string", @enum = new[] { "lastSeen", "firstSeen", "events", "severity" } },
                    limit = new { type = "integer", description = "1..100, default 25." },
                },
            },
            ReadOnly),
        new(
            "get_issue",
            McpCapability.Read,
            "Get one issue by its id (the UUID from list_issues) plus its most recent sampled event "
            + "(stack trace, message, metadata) for debugging.",
            new
            {
                type = "object",
                properties = new { issueId = new { type = "string", description = "The issue's UUID." } },
                required = new[] { "issueId" },
            },
            ReadOnly),
        new(
            "list_issue_events",
            McpCapability.Read,
            "List an issue's sampled events (occurrences), newest-first and paginated, to see how an error "
            + "varies across occurrences.",
            new
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
            ReadOnly),
        // Named states rather than the stored 1/2/3. The read tools have to explain those numbers in prose,
        // which is the sign that they are a poor contract for a model. Reopening is offered because it is
        // how an agent undoes its own wrong call.
        new(
            "set_issue_status",
            McpCapability.Triage,
            "Set an issue's triage status. Use `resolved` when you have fixed the underlying bug, "
            + "`ignored` for a known or acceptable error, and `unresolved` to reopen one. Leave an "
            + "add_issue_note explaining the change: the status alone does not record who changed it or why.",
            new
            {
                type = "object",
                properties = new
                {
                    issueId = new { type = "string", description = "The issue's UUID." },
                    status = new
                    {
                        type = "string",
                        @enum = new[] { "resolved", "ignored", "unresolved" },
                        description = "The state to set.",
                    },
                },
                required = new[] { "issueId", "status" },
            },
            ReversibleWrite),
        new(
            "add_issue_note",
            McpCapability.Triage,
            "Leave a note on an issue for the team, such as the root cause you found or the change you "
            + "made. Notes are shown on the issue in the dashboard and are attributed to this MCP token.",
            new
            {
                type = "object",
                properties = new
                {
                    issueId = new { type = "string", description = "The issue's UUID." },
                    body = new
                    {
                        type = "string",
                        description = $"The note text, up to {Issues.IssueNoteText.MaxLength} characters.",
                    },
                },
                required = new[] { "issueId", "body" },
            },
            AppendingWrite),
    ];

    /// <summary>The tool definitions a token of this capability may see.</summary>
    public static IReadOnlyList<object> DefinitionsFor(McpCapability capability) =>
        All.Where(spec => capability >= spec.Required).Select(spec => spec.Definition).ToList();

    /// <summary>The capability a tool needs, or null when no such tool exists.</summary>
    public static McpCapability? RequiredFor(string name) =>
        All.FirstOrDefault(spec => spec.Name == name)?.Required;
}
