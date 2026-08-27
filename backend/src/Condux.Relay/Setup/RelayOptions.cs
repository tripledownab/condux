using System.Globalization;
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
    long MaxIngestBytes)
{
    public static RelayOptions FromEnv(IConfiguration configuration) => new(
        Valkey: configuration["CONDUX_VALKEY"],
        Postgres: configuration["CONDUX_POSTGRES"],
        KafkaBootstrap: configuration["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092",
        SpikeRatePerSecond: ParseDouble(configuration["CONDUX_SPIKE_PER_SECOND"], 5_000),
        SpikeBurst: ParseLong(configuration["CONDUX_SPIKE_BURST"], 10_000),
        // The ceiling a body may reach once decompressed. Kestrel's limit bounds the COMPRESSED request,
        // which a zip bomb slips under trivially, so the decompressed size is bounded explicitly where
        // the body is read (ReadBoundedAsync). Enforcing it there rather than through
        // IHttpMaxRequestBodySizeFeature is deliberate: that feature is not present under every host, so
        // the guard silently did nothing.
        MaxIngestBytes: ParseLong(configuration["CONDUX_MAX_INGEST_BYTES"], 20 * 1024 * 1024));

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static long ParseLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
