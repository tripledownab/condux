using System.Collections.Concurrent;

namespace Condux.Agent.ManagedAgents;

/// <summary>
/// The vendor-side objects every Managed Agents run shares: the agent and the environment. Pinned ids from
/// options are used as they are. Otherwise each is found by name or created once per process, one caller
/// at a time, because the fix and CVE workers share the gateway and a concurrent first run must not create
/// the same object twice. Nothing here ever updates an agent, so every session sets the system prompt
/// itself: it carries the answer contract the gateway parses, and an agent found by name or pinned would
/// otherwise run on whatever prompt it was last given. A session on an agent found by name or created here
/// also sets the tools, for the same reason. A pinned agent keeps its own tools, because an override
/// replaces a field whole and would undo what the operator chose, such as a disabled tool. No session sets
/// the model, so a run is recorded and priced as the job's model whatever its agent actually runs. An
/// environment found by name IS updated, because a session cannot override its networking. A pinned
/// environment is left as the operator configured it.
/// </summary>
internal sealed class ManagedAgentsProvisioner(ManagedAgentsClient client, ManagedAgentsOptions options)
{
    // The system text every session runs with. The final-answer contract is the same JSON the one-shot
    // path uses, so FixPlanParser is reused verbatim on the way out.
    internal const string SystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. The repository is "
        + "mounted READ-ONLY in your workspace (the user message names the path). Explore it freely, "
        + "reading files with grep or bash, "
        + "run the tests if the environment allows, and edit your working copy to develop the fix, but "
        + "never attempt git push or any other remote write; the checkout cannot accept one. Address the "
        + "root cause, change only what the fix requires, and keep the existing code style. Add or update "
        + "a test that fails without your change and passes with it. If you conclude the code is already "
        + "correct and no such test can be written, say so in the summary and change nothing. Change at "
        + "most 10 files; if a correct fix needs more, stop and explain in the summary instead. End your "
        + "final message with ONLY a JSON object, no code fences and no prose after it: {\"summary\": "
        + "\"what the fix does and why\", \"files\": [{\"path\": \"repo/relative/path\", \"contents\": "
        + "\"the COMPLETE new file contents\"}]}. Every entry in files must contain the full file, not a diff.";

    // Every agent this creates is named with this prefix, which is how the session sweep finds them.
    internal const string AgentNamePrefix = "condux-conductor-";

    private readonly ConcurrentDictionary<string, string> agentIds = new();
    private readonly SemaphoreSlim provisioning = new(1, 1);
    private string? environmentId;

    // The vendor's toolset with every tool off except the five named here, so a tool the vendor adds
    // later stays off until it is named. Three tools are off on purpose. With `read` on, the platform
    // promotes every skill in the mounted repository's root `.claude/skills` to an announced instruction,
    // unreviewed, and that tool is the only documented switch, so leaving it off removes the promotion.
    // The agent still reads files with `grep` and `bash`. `web_fetch` and `web_search` run on the vendor's
    // servers rather than in the sandbox, so the environment's networking does not restrict them, and
    // either one could carry repository text out in the URL or query an injected instruction writes.
    internal static readonly IReadOnlyList<WireToolRef> Tools =
    [
        WireToolRef.AgentToolset with
        {
            DefaultConfig = new WireToolDefault(Enabled: false),
            Configs =
            [
                .. new[] { "bash", "edit", "write", "glob", "grep" }
                    .Select(name => new WireToolConfig(name, Enabled: true)),
            ],
        },
    ];

    // The sandbox's networking allows no host. The working tree holds the customer's source and the agent
    // reads text that anyone holding a DSN chose, so the only safe amount of egress is none. Package
    // registries stay closed too, because opening them also opens the code hosts, and the vendor's docs say
    // that access covers a push made with whatever credential the command supplies. A `git fetch` of the
    // mounted repository still works under this policy, and the host does every git write itself.
    internal static readonly WireEnvironmentConfig Environment = new(
        "cloud", new WireNetworking("limited", AllowedHosts: [], AllowPackageManagers: false, AllowMcpServers: false));

    // Whether the vendor's answer shows the networking this class sent, compared field by field against
    // Environment, so the policy is stated once.
    private static bool StoresPolicy(WireNetworking? stored) =>
        stored is not null
        && stored.Type == Environment.Networking.Type
        && stored.AllowPackageManagers == Environment.Networking.AllowPackageManagers
        && stored.AllowMcpServers == Environment.Networking.AllowMcpServers
        && stored.AllowedHosts is not null
        && stored.AllowedHosts.SequenceEqual(Environment.Networking.AllowedHosts);

    public async Task<WireAgentOverrides> GetAgentAsync(string model, CancellationToken ct) =>
        options.AgentId is { } pinned
            ? new(pinned, SystemPrompt, Tools: null)
            : new(await GetOrCreateAgentAsync(model, ct), SystemPrompt, Tools);

    public async Task<string> GetEnvironmentIdAsync(CancellationToken ct) =>
        options.EnvironmentId ?? await GetOrCreateEnvironmentAsync(ct);

    private async Task<string> GetOrCreateAgentAsync(string model, CancellationToken ct)
    {
        if (agentIds.TryGetValue(model, out var cached))
        {
            return cached;
        }
        await provisioning.WaitAsync(ct);
        try
        {
            if (agentIds.TryGetValue(model, out cached))
            {
                return cached;
            }
            var name = $"{AgentNamePrefix}{model}";
            var existing = (await client.ListAgentsAsync(ct))
                .FirstOrDefault(a => a.Name == name && a.ArchivedAt is null);
            var id = existing?.Id ?? (await client.CreateAgentAsync(name, model, SystemPrompt, Tools, ct)).Id;
            agentIds[model] = id;
            return id;
        }
        finally
        {
            provisioning.Release();
        }
    }

    private async Task<string> GetOrCreateEnvironmentAsync(CancellationToken ct)
    {
        if (environmentId is not null)
        {
            return environmentId;
        }
        await provisioning.WaitAsync(ct);
        try
        {
            if (environmentId is null)
            {
                // An environment found by name is updated to the current policy, since an older build may
                // have created it with different networking.
                var existing = (await client.ListEnvironmentsAsync(ct))
                    .FirstOrDefault(e => e.Name == "condux-conductor" && e.ArchivedAt is null);
                var stored = existing is null
                    ? await client.CreateEnvironmentAsync("condux-conductor", Environment, ct)
                    : await client.UpdateEnvironmentAsync(existing.Id, Environment, ct);
                // The answer must show the policy stored. A field the vendor ignored would otherwise leave
                // every session running on whatever networking the environment had before.
                if (!StoresPolicy(stored.Config?.Networking))
                {
                    throw new InvalidOperationException(
                        $"Managed Agents environment {stored.Id} did not store the requested networking.");
                }
                environmentId = stored.Id;
            }
            return environmentId;
        }
        finally
        {
            provisioning.Release();
        }
    }
}
