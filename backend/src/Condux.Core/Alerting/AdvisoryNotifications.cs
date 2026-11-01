using Condux.Core.CveScanning;
using Condux.Core.Events;

namespace Condux.Core.Alerting;

/// <summary>The <see cref="AlertNotification"/> a dependency advisory fires when it newly comes to apply to
/// a repository linked to a project. Pure, so the mapping is tested without a webhook, a database or a
/// notifier.</summary>
public static class AdvisoryNotifications
{
    /// <summary>
    /// The notification for <paramref name="finding"/> in project <paramref name="projectId"/>. It has no
    /// issue, so <see cref="AlertNotification.IssuePublicId"/> is empty. The advisory's severity becomes the
    /// level, so a rule's level set filters advisories as it filters issues; a severity the source does not
    /// name is treated as high rather than dropped, since a rule that cannot place it should still hear of it.
    /// </summary>
    public static AlertNotification ToNotification(long projectId, CveFinding finding) => new(
        projectId, Guid.Empty, $"{finding.AdvisoryId}: {finding.Summary}", ToCulprit(finding),
        ToLevel(finding.Severity), AlertEventType.Advisory);

    private static Level ToLevel(CveSeverity severity) => severity switch
    {
        CveSeverity.Critical => Level.Fatal,
        CveSeverity.Moderate => Level.Warning,
        CveSeverity.Low => Level.Info,
        _ => Level.Error,
    };

    // The affected package and what to do about it, then where GitHub shows it, in the one line every
    // transport renders.
    private static string ToCulprit(CveFinding finding)
    {
        var parts = new List<string>
        {
            string.Join(' ', new[] { finding.Ecosystem, finding.Package, finding.VulnerableRange }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
            finding.IsFixable ? $"fixed in {finding.FixedVersion}" : "no fixed version yet",
        };
        if (!string.IsNullOrWhiteSpace(finding.ManifestPath))
        {
            parts.Add(finding.ManifestPath);
        }
        if (!string.IsNullOrWhiteSpace(finding.Url))
        {
            parts.Add(finding.Url);
        }
        return string.Join(", ", parts);
    }
}
