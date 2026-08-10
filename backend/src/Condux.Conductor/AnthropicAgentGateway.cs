using System.Collections.Concurrent;
using System.Text;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Conductor;

/// <summary>
/// The real fix backend (v1, "propose a patch"): Claude reads the scoped, double-scrubbed context plus the
/// current contents of the scoped files and proposes complete new file contents; the gateway then pushes a
/// new branch and opens a <b>draft</b> PR via the GitHub App installation token. Credential isolation is
/// structural — the model only ever receives scrubbed text and returns text; the GitHub token stays inside
/// this process and is never part of a model request. A run is an in-process task behind the start → poll
/// seam, so the orchestration and dashboard UX are identical to the simulated backend.
/// </summary>
public sealed class AnthropicAgentGateway(
    IEnumerable<IModelClient> models, ISourceHostTokens tokens, ISourceHostClient repo,
    ConductorKeyResolver keys, HttpClient agentHttp, bool agentic = false) : IAgentGateway
{
    // Scope caps, reflected in the prompt when hit (never silent): at most this many files, each truncated
    // to this many characters.
    private const int MaxFiles = 6;
    private const int MaxCharsPerFile = 48_000;

    private const string SystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. Respond with ONLY a "
        + "JSON object, no code fences and no prose: {\"summary\": \"what the fix does and why\", "
        + "\"files\": [{\"path\": \"repo/relative/path\", \"contents\": \"the COMPLETE new file contents\"}]}. "
        + "Address the root cause, change only what the fix requires, keep the existing code style, and add "
        + "or update a test when practical. Every entry in files must contain the full file, not a diff.";

    private const string AgentSystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. Use the tools to read "
        + "the code before changing it, then write complete file contents (never a diff). Address the root "
        + "cause, change only what the fix requires, keep the existing code style, and add or update a test "
        + "when practical. Call finish with a short summary once the fix is complete.";

    private readonly ConcurrentDictionary<string, AgentRunProgress> runs = new();

    // The provider clients, keyed by provider ("anthropic", "openai-compat"), so a run uses the one
    // matching its resolved config.
    private readonly IReadOnlyDictionary<string, IModelClient> clients = models.ToDictionary(m => m.Provider);

    public string BackendName => "anthropic";

    /// <summary>Overridable so a test can point the agentic turns at a stub instead of the live API,
    /// matching <see cref="AnthropicMessagesClient.ApiBase"/>.</summary>
    public string AgentApiBaseUrl { get; init; } = "https://api.anthropic.com";

    public Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken = default)
    {
        if (spec.InstallationId == 0)
        {
            throw new InvalidOperationException(
                "The org has no GitHub App installation. Connect GitHub in settings before requesting a fix.");
        }

        var runId = Guid.NewGuid().ToString("n");
        runs[runId] = AgentRunProgress.StillRunning;

        // The run outlives this call by design (the seam is start → poll); failures land in the run state,
        // never as an unobserved exception.
        _ = Task.Run(async () =>
        {
            try
            {
                runs[runId] = await RunAsync(runId, spec, cancellationToken);
            }
            catch (Exception ex)
            {
                runs[runId] = AgentRunProgress.Failed(ex.Message);
            }
        }, cancellationToken);

        return Task.FromResult(new AgentRun(runId));
    }

    public Task<AgentRunProgress> PollAsync(string runId, CancellationToken cancellationToken = default) =>
        runs.TryGetValue(runId, out var progress)
            ? Task.FromResult(progress)
            : throw new KeyNotFoundException($"Unknown agent run '{runId}'.");

    private async Task<AgentRunProgress> RunAsync(string runId, AgentRunSpec spec, CancellationToken ct)
    {
        var token = await tokens.GetAsync(spec.InstallationId, ct);

        // 1. Fetch the scoped files at the base branch so the model sees real current code.
        var files = new List<RepoFile>();
        foreach (var path in spec.ScopedPaths.Take(MaxFiles))
        {
            if (await repo.GetFileAsync(token, spec.RepoFullName, path, spec.BaseBranch, ct) is { } file)
            {
                files.Add(file);
            }
        }

        // 2. Ask the model for the fix plan (scrubbed context + file contents in, JSON plan out). The
        // org's BYO provider + key + model (#65/#66) are resolved here — the key is decrypted
        // in-process, never on the wire — and the matching provider client makes the call.
        var resolved = await keys.ResolveAsync(spec.OrgId, spec.Model, ct);
        var proposal = agentic && resolved.Provider == "anthropic"
            ? await ProposeAgenticallyAsync(spec, files, resolved, ct)
            : await ProposeInOneShotAsync(spec, files, resolved, ct);

        // 3. Apply it: new branch off the base, one commit per file, then the draft PR.
        var branch = $"condux/fix-{spec.IssueId}-{runId[..8]}";
        var baseSha = await repo.GetBranchHeadShaAsync(token, spec.RepoFullName, spec.BaseBranch, ct);
        await repo.CreateBranchAsync(token, spec.RepoFullName, branch, baseSha, ct);
        foreach (var (path, contents) in proposal.Files)
        {
            var existing = files.FirstOrDefault(f => f.Path == path)
                ?? await repo.GetFileAsync(token, spec.RepoFullName, path, spec.BaseBranch, ct);
            await repo.PutFileAsync(
                token, spec.RepoFullName, path, branch,
                $"Condux fix for issue {spec.IssueId}: {path}", contents, existing?.Sha, ct);
        }

        var summary = proposal.Summary.Length > 0 ? proposal.Summary : $"Draft fix for issue {spec.IssueId}.";
        var prUrl = await repo.OpenDraftPullRequestAsync(
            token, spec.RepoFullName, branch, spec.BaseBranch,
            title: Truncate($"Condux fix: {summary}", 90),
            body: $"{summary}\n\n---\nOpened as a draft by the Condux Conductor for issue {spec.IssueId}. "
                + "Review carefully before merging; Condux never merges automatically.", ct);

        return AgentRunProgress.Done(branch, prUrl, summary) with
        {
            InputTokens = proposal.InputTokens,
            OutputTokens = proposal.OutputTokens,
        };
    }

    /// <summary>What either strategy produces: a summary and the complete new contents per file.</summary>
    private sealed record FixProposal(
        string Summary, IReadOnlyDictionary<string, string> Files, long InputTokens, long OutputTokens);

    /// <summary>
    /// The agentic strategy: the model explores the scoped checkout through tools and edits it over
    /// several turns, so it can read a file it was not handed and revise its own patch. The workspace
    /// holds no token and executes nothing, so the credential isolation above is unchanged.
    /// </summary>
    private async Task<FixProposal> ProposeAgenticallyAsync(
        AgentRunSpec spec, IReadOnlyList<RepoFile> files, ResolvedLlm resolved, CancellationToken ct)
    {
        var workspace = new InMemoryWorkspace(
            files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal));
        var conversation = new AnthropicToolConversation(
            agentHttp, resolved.ApiKey, resolved.Model, AgentSystemPrompt, BuildUserMessage(spec, files))
        {
            BaseUrl = AgentApiBaseUrl,
        };

        var result = await new AgentLoop(conversation, workspace).RunAsync(ct);
        return new FixProposal(result.Summary, result.ChangedFiles, result.InputTokens, result.OutputTokens);
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
            resolved.Model, SystemPrompt, BuildUserMessage(spec, files), resolved.ApiKey, resolved.BaseUrl, ct);
        var plan = FixPlanParser.Parse(output.Text);
        return new FixProposal(
            plan.Summary,
            plan.Files.ToDictionary(change => change.Path, change => change.Contents, StringComparer.Ordinal),
            output.InputTokens,
            output.OutputTokens);
    }

    private static string BuildUserMessage(AgentRunSpec spec, IReadOnlyList<RepoFile> files)
    {
        var sb = new StringBuilder(spec.Prompt);
        sb.AppendLine().AppendLine();
        sb.AppendLine($"Repository: {spec.RepoFullName} (base branch: {spec.BaseBranch})");
        if (files.Count == 0)
        {
            sb.AppendLine("No scoped files could be read from the repository; propose the most likely fix.");
        }

        foreach (var file in files)
        {
            var truncated = file.Content.Length > MaxCharsPerFile;
            sb.AppendLine($"<file path=\"{file.Path}\"{(truncated ? " truncated=\"true\"" : "")}>");
            sb.AppendLine(truncated ? file.Content[..MaxCharsPerFile] : file.Content);
            sb.AppendLine("</file>");
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
