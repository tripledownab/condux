using Microsoft.Extensions.Configuration;

namespace Condux.Telemetry;

/// <summary>Configuration helpers shared across the services.</summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Reads a required configuration value (supplied via environment variables in every environment,
    /// including local dev via docker-compose). Throws a clear error if it's missing — we never
    /// hardcode connection strings or credentials as fallbacks.
    /// </summary>
    public static string Require(this IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Required configuration '{key}' is not set. Provide it via an environment variable.");
}
