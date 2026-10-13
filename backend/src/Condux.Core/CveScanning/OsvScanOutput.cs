using System.Globalization;
using System.Text.Json;

namespace Condux.Core.CveScanning;

/// <summary>
/// Reads osv-scanner's JSON report into <see cref="CveFinding"/>. Kept pure and separate from running the
/// scanner, because the wire format is the part that breaks when the tool moves, and it is the part that
/// can be tested against real captured output with no container.
///
/// The report nests results → packages → { groups, vulnerabilities }. A group is osv-scanner's own
/// clustering of aliased advisories with the highest CVSS score it saw; the vulnerability entries carry
/// the detail. Both are needed: the score lives only on the group, the fix version only on the entry.
/// </summary>
public static class OsvScanOutput
{
    public const string SourceName = "osv-scanner";

    /// <summary>
    /// Parse a report. Malformed JSON throws, since that means the scanner changed or failed rather than
    /// finding nothing, and silently reporting "no vulnerabilities" would be the worst possible answer.
    /// </summary>
    public static IReadOnlyList<CveFinding> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var findings = new List<CveFinding>();

        foreach (var result in Array(document.RootElement, "results"))
        {
            foreach (var package in Array(result, "packages"))
            {
                var identity = package.TryGetProperty("package", out var p) ? p : default;
                var name = String(identity, "name");
                var ecosystem = String(identity, "ecosystem");
                var scores = GroupScores(package);

                foreach (var vulnerability in Array(package, "vulnerabilities"))
                {
                    var id = String(vulnerability, "id");
                    if (id.Length == 0)
                    {
                        continue;
                    }

                    var (introduced, fixedVersion, upper) = AffectedRange(vulnerability, name);
                    findings.Add(new CveFinding(
                        AdvisoryId: id,
                        CveId: Aliases(vulnerability).FirstOrDefault(a => a.StartsWith("CVE-", StringComparison.Ordinal)),
                        Severity: scores.TryGetValue(id, out var score)
                            ? CveSeverities.FromCvssScore(score)
                            : CveSeverity.Unknown,
                        Summary: String(vulnerability, "summary"),
                        Package: name,
                        Ecosystem: ecosystem,
                        VulnerableRange: Range(introduced, upper),
                        FixedVersion: fixedVersion,
                        Url: $"https://osv.dev/vulnerability/{id}",
                        Source: SourceName));
                }
            }
        }

        return CveSeverities.Normalize(findings);
    }

    /// <summary>
    /// The highest CVSS score osv-scanner assigned, per advisory id. It reports the score once per group
    /// of aliased ids, so every id in a group inherits its group's score.
    /// </summary>
    private static Dictionary<string, double> GroupScores(JsonElement package)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in Array(package, "groups"))
        {
            // Reported as a string ("8.1"), so it is parsed with the invariant culture rather than the
            // machine's, which would read "8.1" as 81 where a comma is the decimal separator.
            if (!group.TryGetProperty("max_severity", out var raw)
                || !double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            {
                continue;
            }

            foreach (var id in Array(group, "ids"))
            {
                if (id.GetString() is { Length: > 0 } value)
                {
                    scores[value] = score;
                }
            }
        }

        return scores;
    }

    /// <summary>
    /// The introduced version, the fixed version and the upper bound as a comparator, from the
    /// affected range. An advisory can list several affected packages, so the entry naming this one is
    /// the relevant one.
    ///
    /// <para>OSV ends an interval with one of three events, and each means a different comparator:
    /// <c>fixed</c> is "&lt;", <c>last_affected</c> is "&lt;=" and <c>limit</c> is "&lt;". Only
    /// <c>fixed</c> names a version to bump to. Reading <c>fixed</c> alone wrote a range bounded by
    /// either of the others as open ended, which claims every later version is affected.</para>
    /// </summary>
    private static (string? Introduced, string? Fixed, string? Upper) AffectedRange(
        JsonElement vulnerability, string package)
    {
        foreach (var affected in Array(vulnerability, "affected"))
        {
            var named = affected.TryGetProperty("package", out var p) ? String(p, "name") : "";
            if (named.Length > 0 && !string.Equals(named, package, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var range in Array(affected, "ranges"))
            {
                string? introduced = null;
                string? fixedVersion = null;
                string? upper = null;
                foreach (var element in Array(range, "events"))
                {
                    introduced ??= Event(element, "introduced");
                    var fixedHere = Event(element, "fixed");
                    fixedVersion ??= fixedHere;
                    upper ??= fixedHere is not null ? $"<{fixedHere}"
                        : Event(element, "last_affected") is { } last ? $"<={last}"
                        : Event(element, "limit") is { } limit ? $"<{limit}"
                        : null;
                }

                if (upper is not null || introduced is not null)
                {
                    return (introduced, fixedVersion, upper);
                }
            }
        }

        return (null, null, null);
    }

    private static string? Event(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string Range(string? introduced, string? upper) => (introduced, upper) switch
    {
        (not null, not null) => $">={introduced} {upper}",
        (not null, null) => $">={introduced}",
        (null, not null) => upper,
        _ => "",
    };

    private static IEnumerable<string> Aliases(JsonElement vulnerability) =>
        Array(vulnerability, "aliases").Select(a => a.GetString() ?? "").Where(a => a.Length > 0);

    private static JsonElement.ArrayEnumerator Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : default;

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";
}
