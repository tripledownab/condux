using System.Globalization;
using Condux.Core.CveScanning;
using Condux.Core.Events;
using Condux.Storage.ClickHouse;
using Microsoft.Extensions.Logging;

namespace Condux.Consumer;

/// <summary>
/// Turns an event's reported dependency versions into rows worth writing, and remembers what it has
/// already written so a release's inventory is inserted once a day rather than on every event from
/// that release (ADR-0041). The rules for what is recordable at all are pure and live in
/// <see cref="ReleaseModules"/>; what is here is the remembering.
///
/// <para>Once a day rather than once, because the table expires a row at last_seen + retention_days
/// and so depends on something refreshing last_seen while the release is still deployed.</para>
///
/// <para><b>The memory is an optimisation and never a correctness mechanism.</b> condux.release_modules
/// merges duplicates on its sort key and keeps the greatest last_seen, so a forgotten entry costs one
/// redundant row and never a wrong answer. That is what lets the overflow policy below be "forget
/// everything and start again" instead of an LRU with a tuning knob nobody would ever turn.</para>
/// </summary>
public sealed class ReleaseModuleRecorder(ILogger logger)
{
    /// <summary>
    /// How many distinct rows to remember. One busy project running a few releases across a couple of
    /// environments sits far below this; the cap exists so a consumer draining many projects cannot
    /// grow this set without bound. Chosen as a rule about memory, not measured from any deployment.
    /// </summary>
    private const int MaxRemembered = 50_000;

    /// <summary>
    /// Joins the parts of a remembered key. The ASCII unit separator, written as an escape rather than
    /// as a raw byte so it survives an editor, a diff and a copy paste.
    /// </summary>
    private const char Separator = '\u001f';

    private readonly HashSet<string> written = new(StringComparer.Ordinal);

    /// <summary>
    /// The rows for this event that have not already been written by this process. Returns nothing for
    /// an event with no release, no mappable platform or no modules, all of which are normal.
    /// </summary>
    public IReadOnlyList<ReleaseModuleRow> Collect(
        string projectKey, Event e, DateTimeOffset seenAt, int retentionDays)
    {
        var collected = ReleaseModules.Collect(e);
        if (collected.Truncated)
        {
            // Say so rather than truncating quietly: a cap nobody is told about reads as full coverage.
            logger.LogWarning(
                "release modules truncated at {Cap} for project {Project} release {Release}",
                ReleaseModules.MaxPerEvent, projectKey, e.Release);
        }

        if (collected.Modules.Count == 0)
        {
            return [];
        }

        // The release is non-empty here, because Collect returns nothing otherwise.
        var release = e.Release!;
        var environment = e.Environment ?? "";
        var rows = new List<ReleaseModuleRow>();
        foreach (var module in collected.Modules)
        {
            // Every column the table sorts on, so two rows that would merge are remembered as one and
            // two that would not are remembered separately. The separator is a control character no
            // package name or version contains, so "ab" cannot collide with "a" and "b".
            //
            // The day is part of the key, and that is not an optimisation detail. The table expires a
            // row at last_seen + retention_days, so an inventory only survives while something keeps
            // refreshing last_seen. A key without a day would write each row once per process and then
            // never again, and a consumer running longer than the retention window would watch a live
            // release's inventory expire underneath it while that release was still reporting. Keying
            // on the day rewrites each row at most once a day, which keeps last_seen within a day of
            // true, and the TTL is measured in whole days so that costs nothing.
            var key = string.Join(
                Separator,
                projectKey,
                release,
                environment,
                module.Ecosystem,
                module.Package,
                module.Version,
                seenAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (Remember(key))
            {
                rows.Add(ClickHouseReleaseModuleWriter.ToRow(
                    projectKey, release, environment, module, seenAt, retentionDays));
            }
        }

        return rows;
    }

    /// <summary>
    /// Record a key, answering whether it was new. On overflow the whole set is dropped, which makes
    /// the next event from each live release write its inventory again. That costs one redundant batch
    /// per release and is why this can stay a plain set.
    /// </summary>
    private bool Remember(string key)
    {
        if (written.Count >= MaxRemembered)
        {
            logger.LogInformation(
                "release module memory full at {Cap} entries, clearing; some rows will be rewritten",
                MaxRemembered);
            written.Clear();
        }

        return written.Add(key);
    }
}
