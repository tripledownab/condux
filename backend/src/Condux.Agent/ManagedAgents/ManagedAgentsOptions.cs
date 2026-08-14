using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Condux.Agent.ManagedAgents;

/// <summary>
/// Config for the Anthropic Managed Agents backend (ADR-0038). The key is the same platform key the
/// direct Messages client uses; the session budget is an ops runaway bound enforced vendor-side
/// (deliberately not a PlanCatalog value — plan policy is enforced at RequestFix, this caps one
/// session). Optional agent/environment pins skip lazy creation for ops determinism.
/// </summary>
public sealed record ManagedAgentsOptions(string ApiKey, decimal MaxSessionUsd)
{
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
    public string? AgentId { get; init; }
    public string? EnvironmentId { get; init; }

    /// <summary>Read from the environment; mirrors <see cref="SandboxOptions.FromEnv"/> — a missing
    /// key throws (the managed-agents provider cannot run without it), optionals default.</summary>
    public static ManagedAgentsOptions FromEnv(IConfiguration config)
    {
        var apiKey = config["CONDUX_ANTHROPIC_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "CONDUX_CONDUCTOR_PROVIDER=managed-agents requires CONDUX_ANTHROPIC_API_KEY.");
        }

        var maxUsd = 5.00m;
        if (config["CONDUX_CMA_MAX_SESSION_USD"] is { Length: > 0 } raw)
        {
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out maxUsd)
                || maxUsd <= 0)
            {
                throw new InvalidOperationException(
                    $"CONDUX_CMA_MAX_SESSION_USD must be a positive decimal, got '{raw}'.");
            }
        }

        return new ManagedAgentsOptions(apiKey, maxUsd)
        {
            AgentId = NullIfEmpty(config["CONDUX_CMA_AGENT_ID"]),
            EnvironmentId = NullIfEmpty(config["CONDUX_CMA_ENVIRONMENT_ID"]),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
