using Condux.Core.FixEngine;
using Condux.Core.Llm;

namespace Condux.Agent;

/// <summary>
/// Routes each run to the right execution backend in managed-agents mode (ADR-0038): an org with a
/// stored BYO LLM config keeps the in-process gateway — their key, their configured provider, exactly
/// the behavior they chose — and every platform-key org goes to the vendor-hosted gateway. The choice
/// rides the run id as a prefix, so polling dispatches statelessly and this class holds nothing.
/// </summary>
public sealed class RoutingAgentGateway(
    IAgentGateway byo, IAgentGateway managed, ILlmConfigReader? configs) : IAgentGateway
{
    private const string ByoPrefix = "byo:";
    private const string ManagedPrefix = "cma:";

    // The mode's persisted default name; per-run truth lands with the honest-backend-names slice.
    public string BackendName => managed.BackendName;

    public async Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken ct = default)
    {
        // No config reader (CONDUX_SECRET_KEY unset) means no org can have a usable BYO key, and org 0
        // (a runner-shaped spec) has no org to look up — both are platform-key runs.
        var useByo = configs is not null && spec.OrgId != 0
            && await configs.GetAsync(spec.OrgId, ct) is not null;
        var inner = useByo ? byo : managed;
        var run = await inner.StartAsync(spec, ct);
        return new AgentRun((useByo ? ByoPrefix : ManagedPrefix) + run.RunId);
    }

    public Task<AgentRunProgress> PollAsync(string runId, CancellationToken ct = default) =>
        runId.StartsWith(ByoPrefix, StringComparison.Ordinal)
            ? byo.PollAsync(runId[ByoPrefix.Length..], ct)
            : runId.StartsWith(ManagedPrefix, StringComparison.Ordinal)
                ? managed.PollAsync(runId[ManagedPrefix.Length..], ct)
                : throw new KeyNotFoundException($"Unknown agent run '{runId}' (no backend prefix).");
}
