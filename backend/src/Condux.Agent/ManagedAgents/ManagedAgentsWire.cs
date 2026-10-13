using System.Text.Json.Serialization;

namespace Condux.Agent.ManagedAgents;

// The Managed Agents wire shapes (beta managed-agents-2026-04-01), holding only the fields Condux
// consumes — the deserializer ignores the rest. Shapes verified against the live beta (2026-08-14):
// budget amounts are integer MINOR units ("100" = $1.00) and a turn that stops, finished or not, reports
// status "idle"; why it stopped is on its session.status_idle event.

internal sealed record WireAgentCreate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("tools")] IReadOnlyList<WireToolRef> Tools);

internal sealed record WireToolRef(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("default_config"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WireToolDefault? DefaultConfig = null,
    [property: JsonPropertyName("configs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WireToolConfig>? Configs = null)
{
    public static readonly WireToolRef AgentToolset = new("agent_toolset_20260401");
}

// The baseline for every tool of a toolset, which a per-tool config overrides.
internal sealed record WireToolDefault([property: JsonPropertyName("enabled")] bool Enabled);

// One tool of a toolset switched on or off by its name.
internal sealed record WireToolConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled);

internal sealed record WireAgent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archived_at")] DateTimeOffset? ArchivedAt);

// One page of any list the vendor pages: its items and the cursor for the next page, null on the last.
internal sealed record WirePage<T>(
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data,
    [property: JsonPropertyName("next_page")] string? NextPage = null);

internal sealed record WireEnvironmentCreate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("config")] WireEnvironmentConfig Config);

// Networking is required rather than optional because the vendor reads an omitted value as unrestricted on
// create, so leaving it out is a decision about egress that nobody would have made on purpose.
internal sealed record WireEnvironmentConfig(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("networking")] WireNetworking Networking);

internal sealed record WireNetworking(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("allowed_hosts")] IReadOnlyList<string> AllowedHosts,
    [property: JsonPropertyName("allow_package_managers")] bool AllowPackageManagers,
    [property: JsonPropertyName("allow_mcp_servers")] bool AllowMcpServers);

internal sealed record WireEnvironmentUpdate([property: JsonPropertyName("config")] WireEnvironmentConfig Config);

internal sealed record WireEnvironment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archived_at")] DateTimeOffset? ArchivedAt,
    [property: JsonPropertyName("config")] WireEnvironmentConfig? Config = null);

// The agent a session runs as, with the fields the session sets itself. The vendor's session guide says
// each one replaces the agent's value in full for this session only, and an absent one is inherited. The
// live beta confirmed it for `system` on 2026-10-02: the session ran the override and the agent kept its own.
internal sealed record WireAgentOverrides(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<WireToolRef>? Tools)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "agent_with_overrides";
}

internal sealed record WireSessionCreate(
    [property: JsonPropertyName("agent")] WireAgentOverrides Agent,
    [property: JsonPropertyName("environment_id")] string EnvironmentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("resources")] IReadOnlyList<WireRepositoryResource> Resources,
    [property: JsonPropertyName("initial_events")] IReadOnlyList<WireUserMessage> InitialEvents,
    [property: JsonPropertyName("budget")] WireBudget Budget);

internal sealed record WireRepositoryResource(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("authorization_token")] string AuthorizationToken,
    [property: JsonPropertyName("checkout")] WireCheckout Checkout,
    [property: JsonPropertyName("mount_path")] string? MountPath = null)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "github_repository";
}

// A branch (`name`) or a commit (`sha`); the field the type does not use is left out of the body.
internal sealed record WireCheckout(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonPropertyName("sha"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha = null);

internal sealed record WireUserMessage(
    [property: JsonPropertyName("content")] IReadOnlyList<WireTextContent> Content)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "user.message";
}

// A client-sent session event. Only the interrupt is sent after a session starts.
internal sealed record WireSessionEvents(
    [property: JsonPropertyName("events")] IReadOnlyList<WireInterrupt> Events);

internal sealed record WireInterrupt
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "user.interrupt";
}

internal sealed record WireTextContent([property: JsonPropertyName("text")] string Text)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";
}

internal sealed record WireBudget(
    [property: JsonPropertyName("max_list_cost")] WireMoney MaxListCost)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "limit";
}

/// <summary>Amounts are integer minor units as strings: "100" = $1.00 (probed live).</summary>
internal sealed record WireMoney(
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("currency")] string Currency);

internal sealed record WireSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("usage")] WireUsage? Usage);

internal sealed record WireUsage(
    [property: JsonPropertyName("input_tokens")] long InputTokens,
    [property: JsonPropertyName("output_tokens")] long OutputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] long CacheReadInputTokens,
    [property: JsonPropertyName("cache_creation")] WireCacheCreation? CacheCreation);

internal sealed record WireCacheCreation(
    [property: JsonPropertyName("ephemeral_1h_input_tokens")] long Ephemeral1hInputTokens,
    [property: JsonPropertyName("ephemeral_5m_input_tokens")] long Ephemeral5mInputTokens);

internal sealed record WireSessionSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("title")] string? Title);

internal sealed record WireEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] IReadOnlyList<WireTextContent>? Content,
    [property: JsonPropertyName("stop_reason")] WireStopReason? StopReason = null);

// Why a session went idle, carried by its session.status_idle event and not by the session object. This
// shape is from the API reference (sessions/events/list), not probed live like the ones above.
internal sealed record WireStopReason([property: JsonPropertyName("type")] string Type);

/// <summary>The vendor's session statuses. The gateway and the session sweep read which ones mean "still
/// working" and which mean settled from here, so neither keeps its own list.</summary>
internal static class WireSessionStatus
{
    internal const string Idle = "idle";
    internal const string Terminated = "terminated";

    // "rescheduling" is the vendor retrying a transient error itself, so the session is still working.
    internal static readonly string[] Working = ["running", "rescheduling"];
    internal static readonly string[] Settled = [Idle, Terminated];

    internal static bool IsKnown(string status) => Working.Contains(status) || Settled.Contains(status);
}
