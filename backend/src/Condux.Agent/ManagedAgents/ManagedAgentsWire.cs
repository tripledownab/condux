using System.Text.Json.Serialization;

namespace Condux.Agent.ManagedAgents;

// The Managed Agents wire shapes (beta managed-agents-2026-04-01), holding only the fields Condux
// consumes — the deserializer ignores the rest. Shapes verified against the live beta (2026-08-14):
// budget amounts are integer MINOR units ("100" = $1.00) and a finished turn reports status "idle".

internal sealed record WireAgentCreate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("tools")] IReadOnlyList<WireToolRef> Tools);

internal sealed record WireToolRef([property: JsonPropertyName("type")] string Type)
{
    public static readonly WireToolRef AgentToolset = new("agent_toolset_20260401");
}

internal sealed record WireAgent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archived_at")] DateTimeOffset? ArchivedAt);

internal sealed record WireAgentList(
    [property: JsonPropertyName("data")] IReadOnlyList<WireAgent> Data);

internal sealed record WireEnvironmentCreate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("config")] WireEnvironmentConfig Config);

internal sealed record WireEnvironmentConfig([property: JsonPropertyName("type")] string Type);

internal sealed record WireEnvironment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archived_at")] DateTimeOffset? ArchivedAt);

internal sealed record WireEnvironmentList(
    [property: JsonPropertyName("data")] IReadOnlyList<WireEnvironment> Data);

internal sealed record WireSessionCreate(
    [property: JsonPropertyName("agent")] string Agent,
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

internal sealed record WireCheckout(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name);

internal sealed record WireUserMessage(
    [property: JsonPropertyName("content")] IReadOnlyList<WireTextContent> Content)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "user.message";
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

internal sealed record WireEventList(
    [property: JsonPropertyName("data")] IReadOnlyList<WireEvent> Data,
    [property: JsonPropertyName("next_page")] string? NextPage = null);

internal sealed record WireEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] IReadOnlyList<WireTextContent>? Content);
