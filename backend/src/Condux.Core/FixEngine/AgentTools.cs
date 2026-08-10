namespace Condux.Core.FixEngine;

/// <summary>The tools an agentic fix run may call. Deliberately tiny: the agent explores the scoped
/// checkout, edits files, and says when it is done. Running commands arrives with the sandboxed
/// workspace, so it is absent here rather than declared and unimplemented.</summary>
public static class AgentToolNames
{
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string Finish = "finish";
}

/// <summary>One argument of a tool, described to the model. Every argument is a string in v1 (paths,
/// file contents, a summary), which keeps the per-provider schema mapping trivial.</summary>
public sealed record AgentToolParameter(string Name, string Description, bool Required = true);

/// <summary>A provider-neutral tool description. Each provider adapter maps this to its own wire shape
/// (Anthropic tool use, OpenAI function calling, Gemini function declarations), so adding a provider
/// never means redefining the tools.</summary>
public sealed record AgentToolSchema(string Name, string Description, IReadOnlyList<AgentToolParameter> Parameters);

/// <summary>The model asking us to run a tool. <see cref="Id"/> is the provider's correlation id, echoed
/// back on the matching <see cref="AgentToolResult"/>.</summary>
public sealed record AgentToolCall(string Id, string Name, IReadOnlyDictionary<string, string> Arguments)
{
    /// <summary>An argument, or empty when the model omitted it. Callers validate what they require.</summary>
    public string Argument(string name) => Arguments.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>What a tool produced, fed back into the conversation. A failure is reported to the model as a
/// result with <see cref="IsError"/> set, not thrown, so the agent can correct itself (a wrong path is a
/// normal step in exploring a repo, not a run-ending fault).</summary>
public sealed record AgentToolResult(string Id, string Content, bool IsError = false);

/// <summary>The tool set offered to the model, as the neutral schema each provider adapter maps.</summary>
public static class AgentToolCatalog
{
    public static IReadOnlyList<AgentToolSchema> All { get; } =
    [
        new(AgentToolNames.ListFiles, "List the repo-relative paths available in the workspace.", []),
        new(AgentToolNames.ReadFile, "Read one file's full contents.",
            [new("path", "Repo-relative path of the file to read.")]),
        new(AgentToolNames.WriteFile, "Write a file's full new contents, creating it if needed.",
            [new("path", "Repo-relative path of the file to write."),
             new("contents", "The complete new contents of the file, not a diff.")]),
        new(AgentToolNames.Finish, "Finish the run once the fix is complete.",
            [new("summary", "A short description of the fix, used as the pull request body.")]),
    ];
}
