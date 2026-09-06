using System.Globalization;
using Condux.Core.CveScanning;

namespace Condux.Storage.ClickHouse;

/// <summary>One row in condux.release_modules (JSON keys match the column names). last_seen is a
/// SimpleAggregateFunction(max), so repeated inserts of the same tuple merge into the latest
/// sighting.</summary>
public sealed record ReleaseModuleRow(
    string project_id,
    string release,
    string environment,
    string ecosystem,
    string package,
    string version,
    string last_seen,
    int retention_days);

/// <summary>
/// Writes the runtime dependency inventory: which package version was actually loaded, in which
/// release and environment (ADR-0041). Same batched JSONEachRow path as the other writers here.
///
/// <para>Re-inserting a row already written is harmless and expected. The table merges on its sort key
/// and keeps the greatest last_seen, so a flush that fails and is retried costs a duplicate row until
/// the next merge and never a wrong answer. That is worth knowing because it is not true of the
/// issue stats rollup beside it, where count is a sum and a double insert double counts.</para>
/// </summary>
public sealed class ClickHouseReleaseModuleWriter(HttpClient http)
{
    public Task InsertAsync(IReadOnlyList<ReleaseModuleRow> rows, CancellationToken cancellationToken = default) =>
        ClickHouseInsert.RowsAsync(http, "condux.release_modules", rows, cancellationToken);

    /// <summary>
    /// Map one observed module to a row. <paramref name="retentionDays"/> is the project's plan tier
    /// retention, driving the column based TTL, so an inventory never outlives the events it
    /// describes. An event with no environment lands under the empty string, matching how
    /// condux.events already stores an absent one.
    /// </summary>
    public static ReleaseModuleRow ToRow(
        string projectId,
        string release,
        string? environment,
        ReleaseModule module,
        DateTimeOffset seenAt,
        int retentionDays) =>
        new(
            projectId,
            release,
            environment ?? "",
            module.Ecosystem,
            module.Package,
            module.Version,
            seenAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            retentionDays);
}
