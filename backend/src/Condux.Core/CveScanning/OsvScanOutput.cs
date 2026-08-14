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

                    var (introduced, fixedVersion) = AffectedRange(vulnerability, name);
                    findings.Add(new CveFinding(
                        AdvisoryId: id,
                        CveId: Aliases(vulnerability).FirstOrDefault(a => a.StartsWith("CVE-", StringComparison.Ordinal)),
                        Severity: scores.TryGetValue(id, out var score)
                            ? CveSeverities.FromCvssScore(score)
                            : CveSeverity.Unknown,
                        Summary: String(vulnerability, "summary"),
                        Package: name,
                        Ecosystem: ecosystem,
                        VulnerableRange: Range(introduced, fixedVersion),
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
    /// The introduced and fixed versions for this package, from the affected range. An advisory can list
    /// several affected packages, so the entry naming this one is the relevant one.
    /// </summary>
    private static (string? Introduced, string? Fixed) AffectedRange(JsonElement vulnerability, string package)
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
                foreach (var element in Array(range, "events"))
                {
                    introduced ??= element.TryGetProperty("introduced", out var i) ? i.GetString() : null;
                    fixedVersion ??= element.TryGetProperty("fixed", out var f) ? f.GetString() : null;
                }

                if (fixedVersion is not null || introduced is not null)
                {
                    return (introduced, fixedVersion);
                }
            }
        }

        return (null, null);
    }

    private static string Range(string? introduced, string? fixedVersion) => (introduced, fixedVersion) switch
    {
        (not null, not null) => $">={introduced} <{fixedVersion}",
        (not null, null) => $">={introduced}",
        (null, not null) => $"<{fixedVersion}",
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
