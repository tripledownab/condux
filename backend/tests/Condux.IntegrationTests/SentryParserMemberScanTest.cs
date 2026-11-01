using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Fails on a member lookup in the Sentry parser that does not go through <c>JsonMembers.TryGetMember</c>.
/// <c>TryGetProperty</c> throws unless its element is an object, and the sender chooses every kind, so a
/// direct call on an array item answered a body like <c>{"breadcrumbs":[1]}</c> with a 500. Some lookups
/// checked the kind first and some did not, which is why the rule is the helper and not a check per call.
/// </summary>
public class SentryParserMemberScanTest
{
    private static readonly string Ingest = Path.Combine("Condux.Core", "Ingest") + Path.DirectorySeparatorChar;

    [Fact]
    public void The_sentry_parser_looks_members_up_only_through_TryGetMember()
    {
        var direct = RepoFiles.ReadSourceLines()
            .Where(line => line.File.StartsWith(Ingest, StringComparison.Ordinal)
                && Path.GetFileName(line.File) != "JsonMembers.cs"
                && line.Text.Contains(".TryGetProperty(", StringComparison.Ordinal))
            .Select(line => $"{line.File}:{line.Number}")
            .ToList();

        Assert.True(direct.Count == 0, "TryGetProperty called directly at " + string.Join(", ", direct));
    }

    /// <summary>The scan reads the right directory: the helper itself is there and calls it.</summary>
    [Fact]
    public void The_scan_sees_the_parser_files()
    {
        Assert.Contains(RepoFiles.ReadSourceLines(), line => line.File.StartsWith(Ingest, StringComparison.Ordinal)
            && Path.GetFileName(line.File) == "JsonMembers.cs"
            && line.Text.Contains(".TryGetProperty(", StringComparison.Ordinal));
    }
}
