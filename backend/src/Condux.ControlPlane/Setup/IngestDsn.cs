namespace Condux.ControlPlane.Setup;

/// <summary>
/// Builds the client-facing DSN a customer pastes into an SDK. Sits beside <see cref="AppUrls"/> because
/// both resolve an absolute address this deployment hands out, from configuration rather than from the
/// current request: the dashboard link there, the ingest endpoint here.
///
/// The host and scheme come from <c>CONDUX_INGEST_HOST</c> / <c>CONDUX_INGEST_SCHEME</c> so production
/// points DSNs at the public relay instead of the dev default. The project is named by its public UUID
/// (#126), never the sequential bigint, which the relay resolves back on the way in.
/// </summary>
internal static class IngestDsn
{
    public static string Build(IConfiguration config, string publicKey, Guid publicId)
    {
        var scheme = config["CONDUX_INGEST_SCHEME"] ?? "http";
        var host = config["CONDUX_INGEST_HOST"] ?? "localhost:9010";
        return $"{scheme}://{publicKey}@{host}/{publicId}";
    }
}
