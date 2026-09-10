using System.Collections.Concurrent;
using System.Text;
using Condux.Agent.Sandbox;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

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
    SandboxOptions? sandboxOptions = null) : IAgentGateway
{
    // Scope caps, reflected in the prompt when hit (never silent): at most this many files, each truncated
    // to this many characters.
    private const int MaxFiles = 6;
    private const int MaxCharsPerFile = 48_000;

    private const string SystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. Respond with ONLY a "
        + "JSON object, no code fences and no prose: {\"summary\": \"what the fix does and why\", "
        + "\"files\": [{\"path\": \"repo/relative/path\", \"contents\": \"the COMPLETE new file contents\"}]}. "
        + "Address the root cause, change only what the fix requires, and keep the existing code style. "
        + "Add or update a test that fails without your change and passes with it. If you conclude the "
        + "code is already correct and no such test can be written, say so in the summary and change "
        + "nothing. Every entry in files must contain the full file, not a diff.";

    private const string AgentSystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. Use the tools to read "
        + "the code before changing it, then write complete file contents (never a diff). Address the root "
        + "cause, change only what the fix requires, and keep the existing code style. Add or update a test "
        + "that fails without your change and passes with it. If you conclude the code is already correct "
        + "and no such test can be written, say so in the summary and change nothing. Call finish with a "
        + "short summary once the fix is complete.";

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
        // The first real runner run failed on this guard when it assumed the hosted GitHub App was the
        // only way to get a token — hence the shared helper and its runner carve-out.
        SourceHostGuards.RequireInstallation(tokens, spec);

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

        // Stop before the model is called. This used to continue and ask for "the most likely fix", which
        // spent the allowance and opened a draft PR against files it had never read. Failing costs nothing
        // and refunds the reservation.
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
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
            ? await ProposeAgenticallyAsync(spec, files, resolved, ct)
            : await ProposeInOneShotAsync(spec, files, resolved, ct);

        // 3. Apply it: the shared host-side git tail (branch, commits, DRAFT PR).
        var (branch, prUrl, summary) = await DraftPrPublisher.PublishAsync(
            repo, token, spec, runId, proposal.Files, proposal.Summary, prefetched: files, ct: ct);

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
        var checkout = files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);

        // A sandbox is configured or it is not. When it is not, the run still happens with the same tools
        // minus the one that needs a container, which is why this is a fallback rather than a failure.
        await using var sandbox = sandboxOptions is null
            ? null
            : await ContainerWorkspace.CreateAsync(
                new DockerEngineClient(sandboxOptions), sandboxOptions, checkout, ct);

        IAgentWorkspace workspace = sandbox is not null ? sandbox : new InMemoryWorkspace(checkout);

        var tools = AgentToolCatalog.For(workspace);
        var message = BuildUserMessage(spec, files);
        // Two wire formats for one loop. The mapping differs (nested tool results, arguments as a JSON
        // string, a system message rather than a field); everything above this line does not.
        IAgentConversation conversation = resolved.Provider == "anthropic"
            ? new AnthropicToolConversation(
                agentHttp, resolved.ApiKey, resolved.Model, AgentSystemPrompt, message)
            {
                BaseUrl = AgentApiBaseUrl,
                AvailableTools = tools,
            }
            : new OpenAiToolConversation(
                agentHttp, resolved.ApiKey, resolved.Model, resolved.BaseUrl, AgentSystemPrompt, message)
            {
                AvailableTools = tools,
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
        // No empty-file case: a run that read nothing fails before reaching the model.
        var sb = new StringBuilder(spec.Prompt);
        sb.AppendLine().AppendLine();
        sb.AppendLine($"Repository: {spec.RepoFullName} (base branch: {spec.BaseBranch})");

        foreach (var file in files)
        {
            var truncated = file.Content.Length > MaxCharsPerFile;
            sb.AppendLine($"<file path=\"{file.Path}\"{(truncated ? " truncated=\"true\"" : "")}>");
            sb.AppendLine(truncated ? file.Content[..MaxCharsPerFile] : file.Content);
            sb.AppendLine("</file>");
        }

        return sb.ToString();
    }
}
