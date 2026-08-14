using Condux.Core.Scrub;

namespace Condux.Core.FixEngine;

/// <summary>What the model did on one turn: either it asked for tools, or it answered. Token usage rides
/// every turn so the loop can enforce a budget and the run can be cost-metered.</summary>
public sealed record AgentTurn(IReadOnlyList<AgentToolCall> ToolCalls, string? FinalText, long InputTokens, long OutputTokens)
{
    public static AgentTurn Calls(IReadOnlyList<AgentToolCall> calls, long inputTokens = 0, long outputTokens = 0) =>
        new(calls, null, inputTokens, outputTokens);

    public static AgentTurn Answer(string text, long inputTokens = 0, long outputTokens = 0) =>
        new([], text, inputTokens, outputTokens);
}

/// <summary>
/// One provider's side of an agentic run, holding the conversation so the loop stays stateless. An
/// implementation maps the neutral tool schema and results to its own wire shape and back. Created per
/// run, so it is not thread safe by design.
/// </summary>
public interface IAgentConversation
{
    /// <summary>Send the previous turn's tool results (empty on the first call) and get the next turn.</summary>
    Task<AgentTurn> NextAsync(IReadOnlyList<AgentToolResult> results, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounds on a run. These are load bearing, not advisory: an agent that loops burns the customer's tokens,
/// so every run is capped in both steps and tokens. The third budget the design calls for, wall clock, is
/// the caller's cancellation token rather than a field here: the loop already honours one, and a per
/// command timeout belongs to the workspace that owns the process.
/// </summary>
public sealed record AgentLoopOptions(int MaxSteps = 16, long MaxTotalTokens = 400_000)
{
    public static AgentLoopOptions Default { get; } = new();
}

/// <summary>What a completed run produced: the summary the agent wrote and the files it staged.</summary>
public sealed record AgentLoopResult(
    string Summary, IReadOnlyDictionary<string, string> ChangedFiles,
    long InputTokens, long OutputTokens, int Steps);

/// <summary>
/// The provider-neutral agentic loop: offer the model a small tool set, execute what it asks for against
/// the workspace, feed the results back, and stop when it finishes or hits a budget. This is pure
/// orchestration with both sides injected, so it unit tests with a scripted conversation and no network.
///
/// A tool that fails reports the failure back to the model rather than ending the run, because a wrong
/// path is a normal step in exploring a repo. Run-ending faults are the ones the agent cannot recover
/// from: no progress, no edits, or an exhausted budget.
/// </summary>
public sealed class AgentLoop(
    IAgentConversation conversation, IAgentWorkspace workspace, AgentLoopOptions? options = null)
{
    private readonly AgentLoopOptions limits = options ?? AgentLoopOptions.Default;

    public async Task<AgentLoopResult> RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AgentToolResult> results = [];
        long inputTokens = 0;
        long outputTokens = 0;

        for (var step = 1; step <= limits.MaxSteps; step++)
        {
            var turn = await conversation.NextAsync(results, cancellationToken);
            inputTokens += turn.InputTokens;
            outputTokens += turn.OutputTokens;

            if (inputTokens + outputTokens > limits.MaxTotalTokens)
            {
                throw new InvalidOperationException(
                    $"Agent run exceeded its token budget of {limits.MaxTotalTokens}.");
            }

            if (turn.FinalText is { } answered)
            {
                return Complete(answered, inputTokens, outputTokens, step);
            }

            if (turn.ToolCalls.Count == 0)
            {
                throw new InvalidOperationException("Agent returned neither a tool call nor an answer.");
            }

            var executed = new List<AgentToolResult>(turn.ToolCalls.Count);
            foreach (var call in turn.ToolCalls)
            {
                if (call.Name == AgentToolNames.Finish)
                {
                    return Complete(call.Argument("summary"), inputTokens, outputTokens, step);
                }

                executed.Add(await ExecuteAsync(call, cancellationToken));
            }

            results = executed;
        }

        throw new InvalidOperationException($"Agent did not finish within {limits.MaxSteps} steps.");
    }

    private async Task<AgentToolResult> ExecuteAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            switch (call.Name)
            {
                case AgentToolNames.ListFiles:
                    var paths = await workspace.ListFilesAsync(cancellationToken);
                    return new AgentToolResult(call.Id, string.Join('\n', paths));

                case AgentToolNames.ReadFile:
                    var path = RequireArgument(call, "path");
                    var contents = await workspace.ReadFileAsync(path, cancellationToken);
                    return contents is null
                        ? new AgentToolResult(call.Id, $"No such file: {path}", IsError: true)
                        : new AgentToolResult(call.Id, contents);

                case AgentToolNames.WriteFile:
                    var target = RequireArgument(call, "path");
                    await workspace.WriteFileAsync(target, RequireArgument(call, "contents"), cancellationToken);
                    return new AgentToolResult(call.Id, $"Wrote {target}.");

                case AgentToolNames.RunCommand:
                    return await RunCommandAsync(call, cancellationToken);

                default:
                    return new AgentToolResult(call.Id, $"Unknown tool: {call.Name}", IsError: true);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // An invalid path or a missing argument is the model's mistake, so hand it back and let the
            // agent retry. Infrastructure faults are not caught here and end the run.
            return new AgentToolResult(call.Id, error.Message, IsError: true);
        }
    }

    /// <summary>
    /// Authorize, run, and report one command. A workspace that cannot execute and a command outside the
    /// allow list are both reported to the model rather than thrown: the agent asked for something it may
    /// not have, which is a step it can recover from, not a fault.
    /// </summary>
    private async Task<AgentToolResult> RunCommandAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        if (!workspace.CanRunCommands)
        {
            return new AgentToolResult(call.Id, "This workspace cannot run commands.", IsError: true);
        }

        if (!CommandPolicy.TryAuthorize(RequireArgument(call, "command"), out var argv, out var refusal))
        {
            return new AgentToolResult(call.Id, refusal, IsError: true);
        }

        var run = await workspace.RunCommandAsync(argv, cancellationToken);

        // Scrub before bounding, not after: bounding drops the middle, and a secret straddling a cut would
        // survive as a fragment that no longer matches a token pattern. A build that echoes an environment
        // variable must not put it into the next model request.
        var output = CommandPolicy.BoundOutput(Scrubber.ScrubString(run.Output));

        // A non-zero exit is reported as an error result so the model sees the failure it must react to,
        // while the output rides along either way — a failing test run is the useful case.
        return new AgentToolResult(call.Id, $"exit {run.ExitCode}\n{output}", IsError: !run.Succeeded);
    }

    private static string RequireArgument(AgentToolCall call, string name) =>
        call.Argument(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"Tool {call.Name} requires the '{name}' argument.");

    /// <summary>A run that changed nothing has nothing to open a pull request for, so it fails rather than
    /// producing an empty draft PR for a human to triage.</summary>
    private AgentLoopResult Complete(string summary, long inputTokens, long outputTokens, int steps)
    {
        if (workspace.ChangedFiles.Count == 0)
        {
            // The agent is asked to change nothing when it finds the code already correct, so its
            // reasoning is the useful half of this failure rather than a footnote to it.
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(summary)
                ? "Agent finished without changing any file."
                : $"The agent proposed no change: {summary.Trim()}");
        }

        return new AgentLoopResult(
            string.IsNullOrWhiteSpace(summary) ? "Fix proposed by the Conductor." : summary.Trim(),
            workspace.ChangedFiles, inputTokens, outputTokens, steps);
    }
}
