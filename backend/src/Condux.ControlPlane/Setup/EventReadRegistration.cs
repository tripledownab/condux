using Condux.Storage.ClickHouse;
using Condux.Storage.SourceMaps;
using Condux.Telemetry;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Registers the control-plane's reads of the event store (ClickHouse): stored events, the hourly issue
/// counts and the runtime dependency inventory, plus the source map symbolication applied to events as
/// they are read.
/// </summary>
internal static class EventReadRegistration
{
    public static IServiceCollection AddEventReads(this IServiceCollection services, IConfiguration cfg, string postgres)
    {
        // Each reader is a resilient typed HttpClient (retry/timeout/circuit-breaker via
        // IHttpClientFactory + the standard resilience handler). The store's config is required, so a
        // missing value fails fast with a clear message.
        var url = cfg.Require("CONDUX_CLICKHOUSE_URL");
        var user = cfg.Require("CONDUX_CLICKHOUSE_USER");
        var password = cfg.Require("CONDUX_CLICKHOUSE_PASSWORD");
        // Stored events, for the issue-detail view.
        services.AddClickHouseReader<ClickHouseEventReader>(url, user, password);
        // The exact per-issue histogram (issue_stats_1h rollup, #102).
        services.AddClickHouseReader<ClickHouseIssueStatsReader>(url, user, password);
        // The runtime dependency inventory (ADR-0041), which tells the CVE findings surface whether a
        // vulnerable package was actually seen running.
        services.AddClickHouseReader<ClickHouseReleaseModuleReader>(url, user, password);

        // Object storage (S3-compatible) for source-map artifacts (ADR-0028), and the symbolication built
        // on it (issue detail/events, MCP tools, fix context). Opt-in: the IObjectStore client registers
        // only when CONDUX_S3_* are set, so the /api/sourcemaps routes 404 until configured.
        services.AddEventSymbolication(cfg, postgres);

        return services;
    }
}
