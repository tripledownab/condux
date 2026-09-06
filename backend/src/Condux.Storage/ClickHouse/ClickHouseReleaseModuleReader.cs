using System.Text.Json;
using Condux.Core.CveScanning;

namespace Condux.Storage.ClickHouse;

/// <summary>
/// Reads the runtime dependency inventory back: which versions of the packages an advisory names were
/// actually seen running, in which release and environment (ADR-0041). One query serves a whole CVE
/// findings list, so the surface costs one round trip rather than one per finding. The
/// <see cref="HttpClient"/> is pre-configured (base URL + auth + resilience) by
/// <see cref="ClickHouseRegistration"/>.
/// </summary>
public sealed class ClickHouseReleaseModuleReader(HttpClient http)
{
    /// <summary>
    /// The most sightings returned for any one package. A project that cuts a release per commit
    /// accumulates rows per deploy, and this stops one such package crowding the rest of the findings
    /// list out of the response. Rows come back newest first, so what the cap drops is the oldest
    /// sightings, which is the right end to lose.
    /// </summary>
    private const int MaxPerPackage = 25;

    // Verified against ClickHouse 24.8, the version the compose stack pins.
    //
    // GROUP BY rather than FINAL, as migration 0005 instructs. Merges are eventual, so a plain read
    // can see one sort key several times carrying different last_seen values; grouping and taking the
    // max is exact whatever the merge state happens to be. Measured across three deliberately
    // unmerged parts, and the result was identical after OPTIMIZE FINAL.
    //
    // argMax collapses releases on purpose. Grouping by version and environment but NOT by release,
    // then taking the release of the newest sighting, answers "is this version still deployed, and
    // where" rather than listing every release that ever carried it. It is merge-invariant for the
    // same reason max is: the newest sighting is in the group whether or not the parts have merged.
    //
    // Package names are matched case insensitively, and the caller passes them already lowered. A
    // scanner spells a name as its registry does and an SDK reports what the runtime says, and those
    // disagree on case in real ecosystems: PyPI treats names case insensitively, so does NuGet. A
    // missed row here would render as "not observed", which is the one direction this feature must not
    // get wrong. lower() is ASCII only, which matches every registry name in practice.
    private const string ObservedSql = """
        SELECT ecosystem, package, version, environment,
               argMax(release, last_seen) AS latest_release,
               toUnixTimestamp(max(last_seen)) AS seen
        FROM condux.release_modules
        WHERE project_id = {pid:String} AND lower(package) IN {packages:Array(String)}
        GROUP BY ecosystem, package, version, environment
        ORDER BY seen DESC, package ASC, version ASC
        LIMIT {per:UInt32} BY ecosystem, package
        FORMAT JSON
        """;

    /// <summary>
    /// Every version of <paramref name="packages"/> seen running in the project, newest sighting
    /// first. Names are matched case insensitively; each row carries the package back spelled as it
    /// was reported, so the caller displays the real name rather than the lowered one.
    ///
    /// <para>An empty result means nothing was observed. Per ADR-0041 that reads as unknown and never
    /// as not affected, because an SDK that sends no modules and a genuinely absent package are
    /// indistinguishable from here.</para>
    /// </summary>
    public async Task<IReadOnlyList<ObservedModule>> ObservedAsync(
        string projectId, IReadOnlyCollection<string> packages, CancellationToken cancellationToken = default)
    {
        // Lowered to match the query, and de-duplicated because a findings list routinely carries
        // several advisories for one package and each repeat would only lengthen the URL.
        var names = packages
            .Select(package => package.Trim().ToLowerInvariant())
            .Where(package => package.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            // ClickHouse answers an empty IN with no rows, so this saves a round trip rather than
            // guarding against an error.
            return [];
        }

        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}" +
                  $"&param_packages={Uri.EscapeDataString(ToArrayLiteral(names))}" +
                  $"&param_per={MaxPerPackage}&query={Uri.EscapeDataString(ObservedSql)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<Response>(stream, cancellationToken: cancellationToken);

        var observed = new List<ObservedModule>();
        foreach (var row in payload?.data ?? [])
        {
            if (ClickHouseJson.TryReadInt64(row.seen, out var seen))
            {
                observed.Add(new ObservedModule(
                    row.ecosystem, row.package, row.version, row.environment, row.latest_release,
                    DateTimeOffset.FromUnixTimeSeconds(seen)));
            }
        }
        return observed;
    }

    /// <summary>
    /// Render the names as the Array(String) literal ClickHouse parses a bound array parameter from.
    /// The names come from a scanner's output rather than from us, so the two characters that can end
    /// a quoted element are escaped, backslash first so an escaped quote is not escaped twice.
    ///
    /// <para>This is not the injection boundary and must not be described as one. The query text is a
    /// constant and a bound parameter is never interpolated into it, so the worst a hostile name could
    /// do is produce a wrong array. Measured: an unterminated literal is refused with
    /// CANNOT_PARSE_QUOTED_STRING rather than parsed as something else.</para>
    /// </summary>
    private static string ToArrayLiteral(IReadOnlyCollection<string> packages) =>
        "[" + string.Join(",", packages.Select(Quote)) + "]";

    private static string Quote(string value) =>
        $"'{value.Replace("\\", "\\\\").Replace("'", "\\'")}'";

    private sealed record Response(List<Row> data);

    private sealed record Row(
        string ecosystem,
        string package,
        string version,
        string environment,
        string latest_release,
        JsonElement seen);
}
