using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.Core.CveFix;

/// <summary>
/// What a conclusion is stored with (<c>cve_fix_runs.verify_evidence</c>): when it was reached, where the
/// window it read for anything still vulnerable began, against which target and ranges, whether the read held every sighting since the merge or stopped at the
/// reader's ceiling, for Cannot verify why, and each sighting it did hold with how it was classed, so a reader sees what the
/// verdict rested on, including that each sighting is what the project's own events reported.
/// </summary>
public sealed record CveFixEvidence(
    DateTimeOffset EvaluatedAt,
    DateTimeOffset QuietSince,
    DateTimeOffset MergedAt,
    string Target,
    IReadOnlyList<string> Ranges,
    bool Complete,
    CannotVerifyReason? Reason,
    IReadOnlyList<CveFixEvidenceSighting> Sightings)
{
    // Stored with each class as its name, which is what a person opening the column wants to see, and
    // read back with the same options, so the two cannot drift.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The evidence as <c>cve_fix_runs.verify_evidence</c> stores it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Evidence read back from <c>cve_fix_runs.verify_evidence</c>, or null when the column holds
    /// something this version cannot read whole: invalid JSON, a missing field (a missing instant reads
    /// as the default one), or a class it does not
    /// know, such as one a newer conductor wrote. One run's evidence must not fail the list it is in.
    /// </summary>
    public static CveFixEvidence? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CveFixEvidence>(json, Json) is
            { Target: not null, Ranges: not null, Sightings: not null } evidence
                && evidence.EvaluatedAt != default && evidence.QuietSince != default && evidence.MergedAt != default
                && evidence.Sightings.All(s => s is { Version: not null, Environment: not null, Release: not null })
                ? evidence
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One sighting in the evidence. <paramref name="InWindow"/> says it was stamped after
/// <see cref="CveFixEvidence.QuietSince"/>, which is what the verdict counted for anything still
/// vulnerable, so a reader names those without restating the window's rule.
/// </summary>
public sealed record CveFixEvidenceSighting(
    string Version, string Environment, string Release, DateTimeOffset LastSeen, SightingClass Class, bool InWindow);

/// <summary>
/// Why a merged CVE bump could not be verified (ADR-0051), so the dashboard can say so without restating
/// the policy that decided it. Values persist in the stored evidence by name and ride the wire.
/// </summary>
public enum CannotVerifyReason
{
    /// <summary>The ecosystem has no version scheme here, so no sighting can be placed.</summary>
    NoScheme = 1,

    /// <summary>The run has no stored ranges: requested before they were stored, or its alert had no
    /// readable range list.</summary>
    NoRanges = 2,

    /// <summary>The read of sightings stopped at the reader's ceiling, so one may have been left out.</summary>
    Incomplete = 3,

    /// <summary>No version of the package was reported running since the merge.</summary>
    NothingReported = 4,

    /// <summary>Versions were reported, but none confirmed fixed.</summary>
    FixedNotReported = 5,

    /// <summary>A fixed version was reported, and so was one that cannot be placed, in the last window.</summary>
    UndecidedReported = 6,
}
