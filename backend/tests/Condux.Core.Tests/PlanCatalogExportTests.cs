using System.Text.Json;
using System.Text.RegularExpressions;
using Condux.Core.Plans;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Keeps the published plan facts identical to the catalog that enforces them.
///
/// Two truthfulness bugs came from hand-copying these. A client-side tier mirror hid a Free org's own
/// allowance from it, and the pricing page sold Business a BYO key the API refuses outright — that one
/// was live on the marketing site. Both were invisible because nothing compared the copy to the source.
///
/// This is a test rather than a build step because the drift is what needs catching, and the backend
/// suite already runs on every change. Regenerate with <c>CONDUX_WRITE_PLANS=1 dotnet test</c> when a
/// limit legitimately changes; otherwise a mismatch fails here, naming what moved.
///
/// Only facts are exported. Prices live in Stripe and the pricing card by design (ADR-0026 keeps the
/// catalog free of commercial terms), and taglines and feature phrasing stay hand-written, because
/// generating positioning would produce worse copy and is not what drifted.
/// </summary>
public class PlanCatalogExportTests
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>The exported shape: every tier's enforced limits, keyed by the tier's name.</summary>
    private static Dictionary<string, object> Export() =>
        Enum.GetValues<Tier>().ToDictionary(
            tier => tier.ToString(),
            tier =>
            {
                var limits = PlanCatalog.For(tier);
                return (object)new
                {
                    tier = (int)tier,
                    monthlyEvents = limits.MonthlyEvents,
                    unlimitedEvents = limits.Unlimited,
                    ratePerSecond = limits.RatePerSecond,
                    burst = limits.Burst,
                    retentionDays = limits.RetentionDays,
                    aiFixesPerMonth = limits.AiFixesPerMonth,
                    unlimitedAiFixes = limits.UnlimitedAiFixes,
                    autoFix = limits.AutoFix,
                    sso = limits.Sso,
                    byoKey = limits.ByoKey,
                    selfHostedRunner = limits.SelfHostedRunner,
                    fixComputeCapUsd = limits.FixComputeCapUsd,
                };
            });

    /// <summary>
    /// Walk up to the repo root: the test runs from its bin directory, and the artifacts it compares
    /// against sit in the workspace rather than beside the test. Both marker directories are required,
    /// because either alone also names something inside a published package tree.
    /// </summary>
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "packages"))
                                    && Directory.Exists(Path.Combine(dir.FullName, "migrations"))))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string PlansJsonPath() =>
        Path.Combine(RepoRoot().FullName, "packages", "plans", "plans.json");

    [Fact]
    public void The_published_plan_facts_match_the_catalog_that_enforces_them()
    {
        var expected = JsonSerializer.Serialize(Export(), Format);
        var path = PlansJsonPath();

        if (Environment.GetEnvironmentVariable("CONDUX_WRITE_PLANS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected + "\n");
            return;
        }

        Assert.True(File.Exists(path),
            $"{path} is missing. Regenerate with CONDUX_WRITE_PLANS=1 dotnet test.");

        Assert.Equal(expected.ReplaceLineEndings("\n"), File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void The_clickhouse_retention_default_matches_the_catalog()
    {
        // ClickHouse stamps this default on any row the consumer writes without a retention header, so a
        // catalog change that left the SQL behind would silently keep old-shaped rows on the old TTL.
        // The column cannot reference the constant, which is exactly why this comparison has to exist
        // somewhere; the alternative is a number in two files with nothing between them.
        var sql = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "migrations", "clickhouse", "0002_per_tier_retention.sql"));

        var declared = Regex.Match(sql, @"retention_days\s+UInt16\s+DEFAULT\s+(\d+)");
        Assert.True(declared.Success,
            "0002_per_tier_retention.sql no longer declares a retention_days default in the shape this "
            + "test reads. Update the pattern, and check the default still equals PlanCatalog.");

        Assert.Equal(PlanCatalog.DefaultRetentionDays, int.Parse(declared.Groups[1].Value));
    }

    [Fact]
    public void Every_tier_is_exported()
    {
        // A tier added to the enum and forgotten here would leave the dashboard and the pricing page
        // describing a plan that no longer exists, or missing one that does.
        Assert.Equal(Enum.GetValues<Tier>().Length, Export().Count);
    }
}
