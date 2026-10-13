using System.Text;
using Condux.Agent.Sandbox;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

using Microsoft.Extensions.Logging;

namespace Condux.Agent;

/// <summary>
/// The real fix backend (v1, "propose a patch"): Claude reads the scoped, double-scrubbed context plus the
/// current contents of the scoped files and proposes complete new file contents; the gateway then pushes a
/// new branch and opens a <b>draft</b> PR via the GitHub App installation token. Credential isolation is
/// structural — the model only ever receives scrubbed text and returns text; the GitHub token stays inside
/// this process and is never part of a model request. A run is an in-process task behind the start → poll
/// seam, so the orchestration and dashboard UX come from that seam rather than from this backend.
/// </summary>
public sealed class AnthropicAgentGateway(
    IEnumerable<IModelClient> models, ISourceHostTokens tokens, ISourceHostClient repo,
    ModelKeyResolver keys, HttpClient agentHttp, bool agentic = false,
    SandboxOptions? sandboxOptions = null, ILogger<AnthropicAgentGateway>? logger = null) : IAgentGateway
{
    // How many scoped files a run reads. The per-file truncation cap lives with the message that
    // applies it, in AgentPrompts.
    private const int MaxFiles = 6;

    private readonly InProcessRuns runs = new();

    // The provider clients, keyed by provider ("anthropic", "openai-compat"), so a run uses the one
    // matching its resolved config.
    private readonly IReadOnlyDictionary<string, IModelClient> clients = models.ToDictionary(m => m.Provider);

    public string BackendName => "anthropic";

    /// <summary>Overridable so a test can point the agentic turns at a stub instead of the live API,
    /// matching <see cref="AnthropicMessagesClient.ApiBase"/>.</summary>
    public string AgentApiBaseUrl { get; init; } = "https://api.anthropic.com";

    public Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken = default)
    {
        // The first real runner run failed on this guard when it assumed the hosted GitHub App was the
        // only way to get a token — hence the shared helper and its runner carve-out.
        SourceHostGuards.RequireInstallation(tokens, spec);

        var runId = runs.Start(ct => RunAsync(spec, ct), cancellationToken);
        return Task.FromResult(new AgentRun(runId));
    }

    public Task<AgentRunProgress> PollAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(runs.Poll(runId));

    public Task<AgentRunProgress> CancelAsync(
        string runId, CancellationToken cancellationToken = default, bool publishFinished = false) =>
        runs.StopAsync(runId, cancellationToken);

    private async Task<AgentRunProgress> RunAsync(AgentRunSpec spec, CancellationToken ct)
    {
        var token = await tokens.GetAsync(spec.InstallationId, ct);

        // 1. Fetch the scoped files at the base branch so the model sees real current code.
        var files = new List<RepoFile>();
        var withheld = new List<RepoFile>();
        foreach (var path in spec.ScopedPaths.Take(MaxFiles))
        {
            // A file with no text to give (too large for the host, or not UTF-8) is left out rather than
            // shown empty, so the run does not count it as seen; the publisher then refuses any change to it.
            switch (await repo.GetFileAsync(token, spec.RepoFullName, path, spec.BaseBranch, ct))
            {
                case { Content: not null } file:
                    files.Add(file);
                    break;
                case { } file:
                    withheld.Add(file);
                    break;
            }
        }

        // The frames did resolve; the files are there and simply cannot be read as text. Saying "no source
        // files" would send the user to fix a code mapping that is right.
        if (files.Count == 0 && withheld.Count > 0)
        {
            throw new FixFailedException(
                FixFailureReason.SourceFilesUnreadable,
                $"The files this issue points to on {spec.RepoFullName}@{spec.BaseBranch} could not be read as "
                + "text (too large for the source host, or not UTF-8), so they cannot be changed.");
        }

        // Stop before the model is called. This used to continue and ask for "the most likely fix", which
        // spent the allowance and opened a draft PR against files it had never read. Failing costs nothing
        // and refunds the reservation.
        if (files.Count == 0)
        {
            // GitHub answers 404 both for a file that is not there and for a repository or branch this
            // installation cannot see, so every read coming back empty does not yet say which. Reading
            // the branch head does: it throws on the second, which classifies as a GitHub access problem
            // rather than sending the user off to write code mappings that would not help.
            await repo.GetBranchHeadShaAsync(token, spec.RepoFullName, spec.BaseBranch, ct);
            throw new FixFailedException(
                FixFailureReason.NoSourceFiles,
                $"No source files could be read for this issue on {spec.RepoFullName}@{spec.BaseBranch}, "
                + "so there is nothing to fix. Its stack frames did not resolve to files in the repository, "
                + "which usually means a missing or incorrect code mapping.");
        }

        // 2. Ask the model for the fix plan (scrubbed context + file contents in, JSON plan out). The
        // org's BYO provider + key + model (#65/#66) are resolved here — the key is decrypted
        // in-process, never on the wire — and the matching provider client makes the call.
        var resolved = await keys.ResolveAsync(spec.OrgId, spec.Model, ct);
        // The agentic loop needs a provider that can drive tools; anything else takes the single-shot
        // path, which is why that path is kept rather than replaced. A capability on the client, not a
        // provider name here, so a new provider is a client and a mapping.
        var proposal = agentic && clients.TryGetValue(resolved.Provider, out var agentClient)
                && agentClient.SupportsTools
            ? await ProposeAgenticallyAsync(spec, files, resolved, token, ct)
            : await ProposeInOneShotAsync(spec, files, resolved, ct);

        // 3. Apply it: the shared host-side git tail (branch, commits, DRAFT PR).
        var (branch, prUrl, summary) = await DraftPrPublisher.PublishAsync(
            repo, token, spec, proposal.Files, proposal.Summary, proposal.Seen,
            LeftUnread(files.Concat(withheld), proposal), prefetched: files, ct: ct);

        return AgentRunProgress.Done(branch, prUrl, summary) with
        {
            InputTokens = proposal.InputTokens,
            OutputTokens = proposal.OutputTokens,
        };
    }

    // The scoped files the run never saw whole and so left as they were. Named in the pull request, since a
    // change that needed them (a lockfile after a version bump) is not complete until someone updates them.
    private static IReadOnlyList<string> LeftUnread(IEnumerable<RepoFile> scoped, FixProposal proposal) =>
        [.. scoped
            .Where(file => !proposal.Seen.Includes(file.Path, file.Sha) && !proposal.Files.ContainsKey(file.Path))
            .Select(file => file.Path)];

    /// <summary>What either strategy produces: a summary and the complete new contents per file.</summary>
    private sealed record FixProposal(
        string Summary, IReadOnlyDictionary<string, string> Files, FilesSeen Seen, long InputTokens, long OutputTokens);

    /// <summary>
    /// The agentic strategy: the model explores the scoped checkout through tools and edits it over
    /// several turns, so it can read a file it was not handed and revise its own patch. Nothing the
    /// workspace exposes can write to the repository, so the credential isolation above is unchanged.
    /// </summary>
    private async Task<FixProposal> ProposeAgenticallyAsync(
        AgentRunSpec spec, IReadOnlyList<RepoFile> files, ResolvedLlm resolved, string token, CancellationToken ct)
    {
        var checkout = files.ToDictionary(file => file.Path, file => file.Content!, StringComparer.Ordinal);

        // A sandbox is configured or it is not. When it is not, the run still happens with the same tools
        // minus the one that needs a container, which is why this is a fallback rather than a failure.
        await using var sandbox = sandboxOptions is null
            ? null
            : await ContainerWorkspace.CreateAsync(
                new DockerEngineClient(sandboxOptions), sandboxOptions, checkout, ct);

        // The checkout holds only the files fetched for the issue; the rest of the repository is read on
        // demand at the base branch. The read function closes over the token, but it can only read, so
        // nothing the workspace exposes can write to the repository.
        var workspace = new RepositoryWorkspace(
            sandbox is not null ? sandbox : new InMemoryWorkspace(checkout), files, files.Where(AgentPrompts.ShowsWhole),
            (path, c) => repo.GetFileAsync(token, spec.RepoFullName, path, spec.BaseBranch, c));

        var tools = AgentToolCatalog.For(workspace);
        var message = AgentPrompts.BuildUserMessage(spec, files);
        // Two wire formats for one loop. The mapping differs (nested tool results, arguments as a JSON
        // string, a system message rather than a field); everything above this line does not.
        IAgentConversation conversation = resolved.Provider == "anthropic"
            ? new AnthropicToolConversation(
                agentHttp, resolved.ApiKey, resolved.Model, AgentPrompts.AgentSystemPrompt, message)
            {
                BaseUrl = AgentApiBaseUrl,
                AvailableTools = tools,
            }
            : new OpenAiToolConversation(
                agentHttp, resolved.ApiKey, resolved.Model, resolved.BaseUrl, AgentPrompts.AgentSystemPrompt, message)
            {
                AvailableTools = tools,
            };

        var result = await new AgentLoop(conversation, workspace, onToolCall: (call, outcome) => LogToolCall(spec, call, outcome))
            .RunAsync(ct);
        return new FixProposal(
            result.Summary, result.ChangedFiles, workspace.Seen, result.InputTokens, result.OutputTokens);
    }

    /// <summary>
    /// The single-shot strategy (ADR-0016), and the compatibility path for any model that cannot drive a
    /// tool loop: one call in, a complete JSON plan out.
    /// </summary>
    private async Task<FixProposal> ProposeInOneShotAsync(
        AgentRunSpec spec, IReadOnlyList<RepoFile> files, ResolvedLlm resolved, CancellationToken ct)
    {
        if (!clients.TryGetValue(resolved.Provider, out var client))
        {
            throw new InvalidOperationException($"No model client for provider '{resolved.Provider}'.");
        }

        var output = await client.CreateAsync(
            resolved.Model, AgentPrompts.SystemPrompt, AgentPrompts.BuildUserMessage(spec, files), resolved.ApiKey, resolved.BaseUrl, ct);
        var plan = FixPlanParser.Parse(output.Text);
        return new FixProposal(
            plan.Summary,
            plan.Files.ToDictionary(change => change.Path, change => change.Contents, StringComparer.Ordinal),
            // One call, shown these files and nothing else; a file cut short was not seen whole.
            FilesSeen.Only(files.Where(AgentPrompts.ShowsWhole)),
            output.InputTokens,
            output.OutputTokens);
    }

    // The tool and the path it named, never file contents or a command: both are text the model chose and
    // either can carry anything. The path is the model's own spelling, which is what a reader auditing the
    // run needs to see.
    private void LogToolCall(AgentRunSpec spec, AgentToolCall call, AgentToolResult outcome) =>
        logger?.LogInformation(
            "Agentic fix run {RunId}: {Tool} {Path} {Outcome}",
            spec.RunId, ToLoggable(call.Name), ToLoggable(call.Argument("path")),
            outcome.IsError ? "refused or failed" : "ok");

    // Both values are the model's own text, so a line break in one would forge a log line. Control
    // characters and the Unicode line and paragraph separators are dropped, and the length capped.
    private static string ToLoggable(string value)
    {
        var printable = string.Concat(value.Where(c => !char.IsControl(c) && c is not ('\u2028' or '\u2029')));
        return printable.Length <= 300 ? printable : printable[..300] + "…";
    }
}
