namespace Condux.Core.FixEngine;

/// <summary>The tools an agentic fix run may call. Deliberately tiny: the agent explores the scoped
/// checkout, edits files, optionally runs a build or test command, and says when it is done. The command
/// tool is offered only by a workspace that can execute, so it is never declared and unimplemented.</summary>
public static class AgentToolNames
{
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string RunCommand = "run_command";
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
/// <remarks>
/// <para>
/// Results come in two kinds and the difference matters, so <b>there is no way to build one without
/// saying which</b>. The constructor is private and the two factories are the whole surface:
/// <see cref="Status"/> for text we wrote, <see cref="FromWorkspace"/> for anything that came out of the
/// workspace. File contents, directory listings and command output are all authored by someone else and
/// land straight in the model's conversation, so they are fenced.
/// </para>
/// <para>
/// A public constructor plus a convention would read the same and behave differently: the next tool
/// added to the loop would default to unfenced by simply not knowing. Here the compiler asks.
/// </para>
/// </remarks>
public sealed record AgentToolResult
{
    private AgentToolResult(string id, string content, bool isError)
    {
        Id = id;
        Content = content;
        IsError = isError;
    }

    public string Id { get; }

    public string Content { get; }

    public bool IsError { get; }

    /// <summary>
    /// A result in our own words: what a tool did, or why it could not.
    /// </summary>
    /// <remarks>
    /// Neutralized even so, because "our own words" almost always quote something the other side chose:
    /// the path it asked to write, the tool name it invented, the command it was refused. That text sits
    /// outside any fence, so without this it could open a region of its own. Done here rather than at the
    /// call sites for the same reason as the label: five places that must remember is not a rule.
    /// </remarks>
    public static AgentToolResult Status(string id, string text, bool isError = false) =>
        new(id, UntrustedText.Neutralize(text), isError);

    /// <summary>
    /// A result carrying workspace content, fenced as untrusted. This is the agentic path's equivalent of
    /// the fencing the opening prompt gets, and the busier half: the strategy exists so the model can
    /// read a file it was not handed, so tool results are the channel it uses most, not an edge case.
    /// </summary>
    /// <param name="label">
    /// What the content is, in our words, placed outside the markers. It may quote something the other
    /// side chose, such as a path the model asked for, so <see cref="UntrustedText.Fence"/> neutralizes
    /// it as well.
    /// </param>
    public static AgentToolResult FromWorkspace(
        string id, string label, string content, bool isError = false) =>
        new(id, UntrustedText.Fence(label, content), isError);
}

/// <summary>The tool set offered to the model, as the neutral schema each provider adapter maps.</summary>
public static class AgentToolCatalog
{
    /// <summary>The tools every workspace supports.</summary>
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

    private static readonly AgentToolSchema RunCommand = new(
        AgentToolNames.RunCommand,
        "Run one build, test or scanning command in the workspace and read its output. Commands run "
        + "directly rather than through a shell, so pipes, redirects and chained commands are unavailable.",
        [new("command", "The command to run, for example 'dotnet test' or 'pnpm test'.")]);

    /// <summary>
    /// The tools this workspace can actually honour. A workspace that cannot execute never sees the
    /// command tool offered, so the model does not spend turns discovering that it always fails.
    /// </summary>
    public static IReadOnlyList<AgentToolSchema> For(IAgentWorkspace workspace) =>
        workspace.CanRunCommands ? [.. All, RunCommand] : All;
}
