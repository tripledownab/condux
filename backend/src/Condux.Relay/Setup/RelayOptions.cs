using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace Condux.Relay.Setup;

/// <summary>Every environment-sourced setting the relay reads, parsed once at startup. One home so a
/// value cannot be re-read and re-defaulted at a second use site, and so the defaults are legible
/// together instead of scattered through the pipeline. Passed explicitly to whatever needs it rather
/// than resolved from the container, so a use site names its configuration in its own signature.
/// </summary>
internal sealed record RelayOptions(
    string? Valkey,
    string? Postgres,
    string KafkaBootstrap,
    double SpikeRatePerSecond,
    long SpikeBurst,
    long MaxIngestBytes,
    long MaxOtlpRecords,
    IReadOnlyList<IPNetwork> TrustedProxies)
{
    public static RelayOptions FromEnv(IConfiguration configuration) => new(
        Valkey: configuration["CONDUX_VALKEY"],
        Postgres: configuration["CONDUX_POSTGRES"],
        KafkaBootstrap: configuration["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092",
        SpikeRatePerSecond: ParseDouble(configuration["CONDUX_SPIKE_PER_SECOND"], 5_000),
        SpikeBurst: ParseLong(configuration["CONDUX_SPIKE_BURST"], 10_000),
        // The ceiling a body may reach once decompressed. Kestrel's MaxRequestBodySize is set to it too, and
        // ASP.NET's decompression holds the EXPANDED body to that limit, throwing a 413 on Kestrel. The read
        // in Ingest/IngestBodies bounds it again explicitly, because IHttpMaxRequestBodySizeFeature, which
        // both read the limit from, is not present under every host, and there the guard silently did nothing.
        MaxIngestBytes: ParseLong(configuration["CONDUX_MAX_INGEST_BYTES"], 20 * 1024 * 1024),
        // How many storable records one OTLP export may turn into. This is the one place that number and
        // its reasoning live; everywhere else points here rather than restating them. The body cap above
        // bounds the bytes and not the records, and a record is tiny: the smallest JSON one this endpoint
        // will store is `{"severityNumber":17}` at 21 bytes, so a body at the 20MiB ceiling carries about
        // 950,000 of them, each a Kafka publish and a stored row that outlive the request. 20,000 is set
        // against what a real sender produces: the OpenTelemetry Collector's batch processor defaults
        // sendBatchSize to 8192 and leaves send_batch_max_size at 0
        // (processor/batchprocessor/factory.go), so a genuine batch can exceed 8192 but not by this much.
        MaxOtlpRecords: ParseLong(configuration["CONDUX_MAX_OTLP_RECORDS"], 20_000),
        // The networks a proxy in front of the relay connects from, whose X-Forwarded-For names the client
        // (Ingest/IngestSenders). Empty trusts no one, so a header is never believed by default: a proxy
        // that passes on what the client claimed would otherwise let the client choose the address it is
        // charged as.
        TrustedProxies: ParseNetworks(configuration["CONDUX_TRUSTED_PROXIES"]));

    // A list that does not parse stops the relay rather than trusting less or more than was meant.
    private static IReadOnlyList<IPNetwork> ParseNetworks(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => IPNetwork.TryParse(entry, out var network)
                ? network
                : throw new InvalidOperationException(
                    $"CONDUX_TRUSTED_PROXIES must be comma-separated CIDR ranges; '{entry}' is not one."))
            .ToList();

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static long ParseLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
