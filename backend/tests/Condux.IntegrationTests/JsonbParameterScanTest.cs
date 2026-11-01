using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Fails on a <c>jsonb</c> parameter not added through <c>PostgresJson.AddJsonb</c>, which replaces any
/// <c>\u0000</c> the JSON holds. Postgres refuses that escape in jsonb (22P05), the JSON is built from caller
/// and model text, and a NUL in it failed the statement: a runner fix request answered 503 and an audit row
/// was dropped unseen. Both forms are scanned: a parameter typed <c>NpgsqlDbType.Jsonb</c>, and one cast in
/// SQL as <c>@name::jsonb</c>, which must be added under that name with <c>AddJsonb</c> in the same file.
/// </summary>
public class JsonbParameterScanTest
{
    private static readonly Regex Cast = new(@"@(\w+)::jsonb", RegexOptions.Compiled);

    [Fact]
    public void Every_jsonb_parameter_goes_through_AddJsonb()
    {
        var problems = new List<string>();
        foreach (var file in RepoFiles.ReadSourceLines().GroupBy(line => line.File))
        {
            if (Path.GetFileName(file.Key) == "PostgresJson.cs")
            {
                continue;
            }
            var text = string.Join('\n', file.Select(line => line.Text));
            problems.AddRange(file
                .Where(line => line.Text.Contains("NpgsqlDbType.Jsonb", StringComparison.Ordinal))
                .Select(line => $"{line.File}:{line.Number} types a jsonb parameter itself"));
            problems.AddRange(Cast.Matches(text).Select(m => m.Groups[1].Value).Distinct()
                .Where(name => !text.Contains($"AddJsonb(\"{name}\"", StringComparison.Ordinal))
                .Select(name => $"{file.Key}: @{name}::jsonb is not added with AddJsonb"));
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The scan sees jsonb casts at all, so a pattern that stopped matching would not pass silently.</summary>
    [Fact]
    public void The_scan_sees_jsonb_casts()
    {
        Assert.Contains(RepoFiles.ReadSourceLines(), line => Cast.IsMatch(line.Text));
    }
}
