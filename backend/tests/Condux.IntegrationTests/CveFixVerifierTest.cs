extern alias conductor;
using System.Text.Json;
using conductor::Condux.Conductor;
using Condux.Core.CveFix;
using Condux.Core.CveScanning;
using Condux.Core.FixEngine;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The CVE bump watch (ADR-0051) end to end on real stores: a merged bump in Postgres, sightings of its
/// package in the ClickHouse inventory, and the verifier's tick storing a conclusion with its evidence.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CveFixVerifierTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    private static readonly DateTimeOffset MergedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_bump_holds_once_the_fixed_version_runs_and_the_vulnerable_one_stops()
    {
        var (runId, projectId) = await SeedMergedBumpAsync();
        await SeeAsync(projectId, "4.17.20", MergedAt.AddHours(-3));
        await SeeAsync(projectId, "4.17.21", MergedAt.AddDays(1));

        await TickAsync(MergedAt.AddDays(4));

        var (status, evidence) = await ReadConclusionAsync(runId);
        Assert.Equal(VerifyStatus.Held, status);
        var json = JsonDocument.Parse(evidence!).RootElement;
        Assert.Equal("4.17.21", json.GetProperty("target").GetString());
        Assert.True(json.GetProperty("complete").GetBoolean());
        Assert.Equal(["< 4.17.21"], json.GetProperty("ranges").EnumerateArray().Select(r => r.GetString()));
        // Only sightings since the merge are read: the vulnerable one before it is not part of the verdict.
        Assert.Equal(
            [("4.17.21", "fixed")],
            json.GetProperty("sightings").EnumerateArray()
                .Select(s => (s.GetProperty("version").GetString(), s.GetProperty("class").GetString())));
    }

    [Fact]
    public async Task A_bump_still_reporting_the_vulnerable_version_at_the_deadline_did_not_hold()
    {
        var (runId, projectId) = await SeedMergedBumpAsync();
        await SeeAsync(projectId, "4.17.21", MergedAt.AddDays(1));
        await SeeAsync(projectId, "4.17.20", MergedAt.AddDays(13));

        await TickAsync(MergedAt.AddDays(14));

        Assert.Equal(VerifyStatus.DidNotHold, (await ReadConclusionAsync(runId)).Status);
    }

    [Fact]
    public async Task A_bump_with_nothing_reported_is_watched_until_the_deadline_then_cannot_be_verified()
    {
        var (runId, _) = await SeedMergedBumpAsync();

        await TickAsync(MergedAt.AddDays(13));
        Assert.Equal((VerifyStatus.Watching, null), await ReadConclusionAsync(runId));

        await TickAsync(MergedAt.AddDays(14));
        Assert.Equal(VerifyStatus.CannotVerify, (await ReadConclusionAsync(runId)).Status);
    }

    /// <summary>
    /// A vulnerable version still reported in production behind 25 environments reporting the fixed one
    /// more recently. A read capped per package dropped the production row and concluded held; this one
    /// reads every sighting since the merge.
    /// </summary>
    [Fact]
    public async Task A_vulnerable_sighting_behind_many_newer_environments_still_blocks_held()
    {
        var (runId, projectId) = await SeedMergedBumpAsync();
        await SeeAsync(projectId, "4.17.20", MergedAt.AddDays(3.2), "production");
        for (var i = 0; i < 25; i++)
        {
            await SeeAsync(projectId, "4.17.21", MergedAt.AddDays(3.5), $"preview-{i}");
        }

        await TickAsync(MergedAt.AddDays(4));

        Assert.Equal((VerifyStatus.Watching, null), await ReadConclusionAsync(runId));
    }

    /// <summary>
    /// More sightings since the merge than the reader returns: the read reports itself incomplete, and
    /// the bump does not hold on it even though every sighting it did return is fixed.
    /// </summary>
    [Fact]
    public async Task A_read_past_the_ceiling_is_incomplete_and_does_not_hold()
    {
        var (runId, projectId) = await SeedMergedBumpAsync();
        using var http = Client();
        await new ClickHouseReleaseModuleWriter(http).InsertAsync(
        [
            .. Enumerable.Range(0, ClickHouseReleaseModuleReader.MaxSightings + 1).Select(i => ClickHouseReleaseModuleWriter.ToRow(
                projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1.4.2", $"env-{i}",
                new ReleaseModule("npm", "lodash", "4.17.21"), MergedAt.AddDays(1), 90)),
        ]);

        var (sightings, complete) = await new ClickHouseReleaseModuleReader(http).ReadSightingsSinceAsync(
            projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), "lodash", MergedAt);
        Assert.False(complete);
        Assert.Equal(ClickHouseReleaseModuleReader.MaxSightings, sightings.Count);

        await TickAsync(MergedAt.AddDays(4));
        Assert.Equal((VerifyStatus.Watching, null), await ReadConclusionAsync(runId));

        // At the deadline it cannot be verified, and its evidence says the read stopped short.
        await TickAsync(MergedAt.AddDays(14));
        var (status, evidence) = await ReadConclusionAsync(runId);
        Assert.Equal(VerifyStatus.CannotVerify, status);
        Assert.False(JsonDocument.Parse(evidence!).RootElement.GetProperty("complete").GetBoolean());
    }

    /// <summary>
    /// The worker's own tick runs the CVE half: a bump concludes through <see cref="VerificationWorker"/>
    /// exactly as through the verifier alone.
    /// </summary>
    [Fact]
    public async Task The_verification_worker_concludes_bumps_on_its_tick()
    {
        var (runId, projectId) = await SeedMergedBumpAsync();
        await SeeAsync(projectId, "4.17.21", MergedAt.AddDays(1));
        var services = new ServiceCollection().AddLogging();
        services.AddClickHouseReader<ClickHouseReleaseModuleReader>(ch.BaseUrl, ch.ChUser, ch.ChPassword);
        services.AddClickHouseReader<ClickHouseIssueStatsReader>(ch.BaseUrl, ch.ChUser, ch.ChPassword);
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var worker = new VerificationWorker(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<VerificationWorker>.Instance,
            scopes,
            new PostgresFixVerification(pg.ConnectionString),
            new IssueRepository(pg.ConnectionString),
            new PostgresFixStore(pg.ConnectionString),
            new ProjectEventNotifier(pg.ConnectionString),
            new Condux.Telemetry.ConduxSelfReporter(null),
            new CveFixVerifier(
                new PostgresCveFixVerification(pg.ConnectionString), scopes,
                new ProjectEventNotifier(pg.ConnectionString), new FixedClock(MergedAt.AddDays(4)),
                NullLogger<CveFixVerifier>.Instance));

        Assert.True(await worker.TickBothAsync(default));

        Assert.Equal(VerifyStatus.Held, (await ReadConclusionAsync(runId)).Status);
    }

    /// <summary>
    /// The since-merge read folds separators like the findings read, so a stored "Zope_Interface" is a
    /// sighting of a bump on "zope.interface"; without the fold it is missed and could not block held.
    /// </summary>
    [Fact]
    public async Task The_since_merge_read_finds_a_package_stored_with_other_separators()
    {
        var (_, projectId) = await SeedMergedBumpAsync();
        using var http = Client();
        await new ClickHouseReleaseModuleWriter(http).InsertAsync(
        [
            ClickHouseReleaseModuleWriter.ToRow(
                projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1.4.2", "production",
                new ReleaseModule("PyPI", "Zope_Interface", "5.4.0"), MergedAt.AddDays(1), 90),
        ]);

        var (sightings, _) = await new ClickHouseReleaseModuleReader(http).ReadSightingsSinceAsync(
            projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), "zope.interface", MergedAt);

        Assert.Equal("Zope_Interface", Assert.Single(sightings).Package);
    }

    /// <summary>
    /// One bump whose read fails (here a package name past ClickHouse's URL limit) is left watching, and
    /// the bump after it is still concluded on the same tick.
    /// </summary>
    [Fact]
    public async Task A_bump_whose_read_fails_does_not_stop_the_others()
    {
        var (brokenId, _) = await SeedMergedBumpAsync(package: new string('a', 2 * 1024 * 1024));
        var (runId, projectId) = await SeedMergedBumpAsync();
        await SeeAsync(projectId, "4.17.21", MergedAt.AddDays(1));

        await TickAsync(MergedAt.AddDays(4));

        Assert.Equal((VerifyStatus.Watching, null), await ReadConclusionAsync(brokenId));
        Assert.Equal(VerifyStatus.Held, (await ReadConclusionAsync(runId)).Status);
    }

    /// <summary>A conclusion pings the project's live stream, so an open dashboard refreshes (ADR-0030).</summary>
    [Fact]
    public async Task A_conclusion_nudges_the_project()
    {
        var (_, projectId) = await SeedMergedBumpAsync();
        await SeeAsync(projectId, "4.17.21", MergedAt.AddDays(1));
        Assert.True(await ProjectNudges.IsSentAsync(
            pg.ConnectionString, projectId, () => TickAsync(MergedAt.AddDays(4))));
    }

    private async Task<(Guid RunId, long ProjectId)> SeedMergedBumpAsync(string package = "lodash")
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repoName = $"acme/api-{Guid.NewGuid():N}";
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, repoName, "main");
        var store = new PostgresCveFixStore(pg.ConnectionString);
        var run = new CveFixRun(
            Guid.CreateVersion7(), repo.Id, "GHSA-jf85-cpcp-j695", null, package, "npm", "< 4.17.21", "4.17.21",
            "", FixStatus.Running, "fake", "model", Branch: "", PrUrl: "", Summary: "", Actor: "1",
            MergedAt.AddDays(-1), MergedAt.AddDays(-1))
        { JobId = Guid.CreateVersion7(), VulnerableRanges = ["< 4.17.21"] };
        Assert.True(await store.InsertAsync(run));
        await store.RecordSuccessAsync(run with { Status = FixStatus.Succeeded, Branch = "condux/cve", PrUrl = "https://x/p/1" });
        Assert.NotEmpty(await new PostgresCveFixVerification(pg.ConnectionString)
            .MarkMergedAsync(repoName, "condux/cve", MergedAt));
        return (run.Id, projectId);
    }

    private async Task SeeAsync(long projectId, string version, DateTimeOffset at, string environment = "production")
    {
        using var http = Client();
        await new ClickHouseReleaseModuleWriter(http).InsertAsync(
        [
            ClickHouseReleaseModuleWriter.ToRow(
                projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1.4.2", environment,
                new ReleaseModule("npm", "lodash", version), at, 90),
        ]);
    }

    // The verifier's own tick, as the worker runs it: a scope with the inventory reader, and the clock
    // fixed at now. It concludes every watching bump in this class's database, which is only this
    // class's own, and the tests read back only theirs.
    private async Task TickAsync(DateTimeOffset now)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddClickHouseReader<ClickHouseReleaseModuleReader>(ch.BaseUrl, ch.ChUser, ch.ChPassword);
        await using var provider = services.BuildServiceProvider();
        var verifier = new CveFixVerifier(
            new PostgresCveFixVerification(pg.ConnectionString),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ProjectEventNotifier(pg.ConnectionString),
            new FixedClock(now),
            NullLogger<CveFixVerifier>.Instance);

        await verifier.TickAsync(default);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private HttpClient Client()
    {
        var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        return http;
    }

    private async Task<(VerifyStatus Status, string? Evidence)> ReadConclusionAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT verify_status, verify_evidence::text FROM cve_fix_runs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return ((VerifyStatus)reader.GetInt16(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
