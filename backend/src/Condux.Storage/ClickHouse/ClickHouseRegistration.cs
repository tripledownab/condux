using Microsoft.Extensions.DependencyInjection;

namespace Condux.Storage.ClickHouse;

/// <summary>
/// DI registration for the ClickHouse HTTP client. The client is created by <see cref="IHttpClientFactory"/>
/// with the **standard resilience handler** (retry with jitter/backoff + timeout + circuit breaker,
/// Polly-based) — so ClickHouse calls are resilient without any hand-rolled retry — and pre-configured
/// with the base URL + auth headers.
/// </summary>
public static class ClickHouseRegistration
{
    /// <summary>Named client key for hosts that resolve the client via <see cref="IHttpClientFactory"/>.</summary>
    public const string ClientName = "clickhouse";

    /// <summary>Configure an <see cref="HttpClient"/> for the ClickHouse HTTP interface.</summary>
    public static void Configure(HttpClient client, string baseUrl, string user, string password)
    {
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Add("X-ClickHouse-User", user);
        client.DefaultRequestHeaders.Add("X-ClickHouse-Key", password);
    }

    /// <summary>
    /// Registers a reader as a resilient typed client, safe to inject into transient or scoped request
    /// handlers such as the control-plane's endpoints. Every reader in this namespace takes the same
    /// pre-configured <see cref="HttpClient"/> and wants the same resilience, so they share one
    /// registration rather than each getting a near-identical copy of it.
    /// </summary>
    public static IServiceCollection AddClickHouseReader<TReader>(
        this IServiceCollection services, string baseUrl, string user, string password)
        where TReader : class
    {
        services.AddHttpClient<TReader>(c => Configure(c, baseUrl, user, password))
            .AddStandardResilienceHandler();
        return services;
    }

    /// <summary>
    /// Registers a resilient **named** ClickHouse client (<see cref="ClientName"/>) for a long-running
    /// singleton (the consumer) to resolve via <see cref="IHttpClientFactory"/> — avoids a captive
    /// dependency on a typed client.
    /// </summary>
    public static IServiceCollection AddClickHouseNamedClient(
        this IServiceCollection services, string baseUrl, string user, string password)
    {
        services.AddHttpClient(ClientName, c => Configure(c, baseUrl, user, password))
            .AddStandardResilienceHandler();
        return services;
    }
}
