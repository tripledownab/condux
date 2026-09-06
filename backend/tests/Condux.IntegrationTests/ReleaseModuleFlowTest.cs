using Condux.Core.CveScanning;
using Condux.Core.Events;
using Condux.Core.Scrub;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The runtime dependency inventory (ADR-0041) end to end against a real ClickHouse: scrub an event,
/// collect its modules, write them, read them back, and classify a CVE finding against them.
///
/// <para>This exists because the unit tests on either side cannot meet in the middle. The reader's
/// unit tests stub the HTTP layer, so they prove the request is built and parsed correctly but never
/// that ClickHouse accepts the query; the writer's prove rows are posted but never that they come
/// back. The claims below (merge invariance, argMax collapsing releases, case-insensitive matching)
/// are all properties of the storage engine, so only a real one can settle them.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReleaseModuleFlowTest(ClickHouseFixture ch) : IClassFixture<ClickHouseFixture>
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private HttpClient Client()
    {
        var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        return http;
    }

    private static Event EventWith(
        Dictionary<string, string> modules, string release = "1.4.2", string environment = "production") =>
        new()
        {
            EventId = "e1",
            Level = Level.Error,
            Platform = "javascript",
            Release = release,
            Environment = environment,
            Modules = modules,
        };

    private async Task WriteAsync(string projectId, Event e, DateTimeOffset seenAt)
    {
        using var http = Client();
        var rows = ReleaseModules.Collect(e).Modules
            .Select(module => ClickHouseReleaseModuleWriter.ToRow(
                projectId, e.Release!, e.Environment, module, seenAt, 90))
            .ToList();
        await new ClickHouseReleaseModuleWriter(http).InsertAsync(rows);
    }

    private async Task<IReadOnlyList<ObservedModule>> ReadAsync(string projectId, params string[] packages)
    {
        using var http = Client();
        return await new ClickHouseReleaseModuleReader(http).ObservedAsync(projectId, packages);
    }

    /// <summary>
    /// The whole chain on one package, including the scrub. jsonwebtoken is the case that matters:
    /// EventScrubber used to replace the version of any package whose NAME contained "token", so the
    /// single most valuable row this feature can show was the one guaranteed to arrive redacted. Here
    /// the real scrubber runs, and the real version has to survive all the way back out of ClickHouse.
    /// </summary>
    [Fact]
    public async Task Scrubbed_event_modules_survive_the_round_trip_and_classify_as_running()
    {
        var scrubbed = EventScrubber.Scrub(EventWith(new Dictionary<string, string>
        {
            ["jsonwebtoken"] = "8.5.1",
            ["lodash"] = "4.17.11",
        }));
        Assert.NotEqual(Scrubber.Redacted, scrubbed.Modules["jsonwebtoken"]);

        await WriteAsync("101", scrubbed, Monday);
        var observed = await ReadAsync("101", "jsonwebtoken", "lodash");

        var jwt = Assert.Single(observed, module => module.Package == "jsonwebtoken");
        Assert.Equal("8.5.1", jwt.Version);
        Assert.Equal("npm", jwt.Ecosystem);
        Assert.Equal("production", jwt.Environment);
        Assert.Equal("1.4.2", jwt.Release);
        Assert.Equal(Monday, jwt.LastSeen);

        // And the finding a scanner would report for it now reads as running rather than unknown.
        var exposure = ModuleExposures.For(Finding("jsonwebtoken", "npm"), observed);
        Assert.Equal(ModuleExposureState.Running, exposure.State);
        Assert.Equal("8.5.1", Assert.Single(exposure.Observed).Version);
    }

    /// <summary>
    /// The reason migration 0005 says to read with GROUP BY and never FINAL. Two inserts of the same
    /// sort key land in separate parts that may not have merged, so a plain SELECT can return both.
    /// The read must answer one row carrying the LATER sighting, whatever the merge state is, or a
    /// live release's inventory would look stale and could expire early under the TTL.
    /// </summary>
    [Fact]
    public async Task Repeated_sightings_of_one_row_collapse_to_the_latest_without_FINAL()
    {
        var e = EventWith(new Dictionary<string, string> { ["axios"] = "1.6.0" });
        await WriteAsync("102", e, Monday);
        await WriteAsync("102", e, Monday.AddDays(2));

        var observed = Assert.Single(await ReadAsync("102", "axios"));

        Assert.Equal(Monday.AddDays(2), observed.LastSeen);
    }

    /// <summary>
    /// argMax reports where a version is running NOW, not everywhere it ever ran. One version carried
    /// by two releases is one row naming the newer, because "still deployed" is the question and a
    /// list of every historical release is not an answer to it.
    /// </summary>
    [Fact]
    public async Task One_version_across_two_releases_reports_the_most_recent_release()
    {
        var packages = new Dictionary<string, string> { ["express"] = "4.16.0" };
        await WriteAsync("103", EventWith(packages, release: "2.0.0"), Monday);
        await WriteAsync("103", EventWith(packages, release: "2.1.0"), Monday.AddDays(1));

        var observed = Assert.Single(await ReadAsync("103", "express"));

        Assert.Equal("2.1.0", observed.Release);
    }

    /// <summary>
    /// A version live in two environments stays two rows. Which environment it is in changes what the
    /// reader does about it, so collapsing production and staging together would destroy the point.
    /// </summary>
    [Fact]
    public async Task The_same_version_in_two_environments_stays_two_rows()
    {
        var packages = new Dictionary<string, string> { ["react"] = "18.2.0" };
        await WriteAsync("104", EventWith(packages, environment: "production"), Monday);
        await WriteAsync("104", EventWith(packages, environment: "staging"), Monday);

        var observed = await ReadAsync("104", "react");

        Assert.Equal(
            ["production", "staging"],
            observed.Select(module => module.Environment).OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The query filters on lower(package), so a scanner's spelling finds a runtime's. PyPI and NuGet
    /// both treat names case insensitively, so the two genuinely disagree; matching exactly would turn
    /// that into a false "not observed", which is the one direction this feature must not get wrong.
    /// </summary>
    [Fact]
    public async Task A_package_stored_in_one_casing_is_found_by_another()
    {
        await WriteAsync("105", EventWith(new Dictionary<string, string> { ["Flask"] = "2.0.0" }), Monday);

        var observed = Assert.Single(await ReadAsync("105", "flask"));

        // Stored spelling comes back, so the display shows the name as the developer wrote it.
        Assert.Equal("Flask", observed.Package);
    }

    /// <summary>
    /// A dependency inventory is a supply-chain map of a customer's application, so the tenancy
    /// boundary is asserted here rather than assumed from the endpoint's role filter.
    /// </summary>
    [Fact]
    public async Task Another_projects_inventory_is_never_returned()
    {
        await WriteAsync("106", EventWith(new Dictionary<string, string> { ["lodash"] = "0.0.1" }), Monday);
        await WriteAsync("107", EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.21" }), Monday);

        var observed = Assert.Single(await ReadAsync("107", "lodash"));

        Assert.Equal("4.17.21", observed.Version);
    }

    /// <summary>
    /// An unmatched finding reads Unknown, and Unknown must never be rendered as "not affected"
    /// (ADR-0041). Asserted against a real store so it covers the query returning nothing, not just
    /// the pure rule.
    /// </summary>
    [Fact]
    public async Task A_package_never_seen_reads_as_unknown_rather_than_safe()
    {
        await WriteAsync("108", EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.21" }), Monday);

        var exposure = ModuleExposures.For(
            Finding("left-pad", "npm"), await ReadAsync("108", "left-pad"));

        Assert.Equal(ModuleExposureState.Unknown, exposure.State);
        Assert.Empty(exposure.Observed);
    }

    /// <summary>
    /// Dependabot is the only scanner wired to the findings surface and it says "pip", while we record
    /// what the platform maps to, which is OSV's "PyPI". This is the join the whole feature rests on,
    /// proven against real stored rows rather than only in the pure matcher's unit tests.
    /// </summary>
    [Fact]
    public async Task A_dependabot_ecosystem_matches_the_osv_one_we_recorded()
    {
        var python = new Event
        {
            EventId = "e2",
            Level = Level.Error,
            Platform = "python",
            Release = "3.1.0",
            Environment = "production",
            Modules = new Dictionary<string, string> { ["requests"] = "2.19.0" },
        };
        await WriteAsync("109", python, Monday);

        var observed = await ReadAsync("109", "requests");
        Assert.Equal("PyPI", Assert.Single(observed).Ecosystem);

        var exposure = ModuleExposures.For(Finding("requests", "pip"), observed);
        Assert.Equal(ModuleExposureState.Running, exposure.State);
    }

    private static CveFinding Finding(string package, string ecosystem) =>
        new(
            AdvisoryId: "GHSA-test",
            CveId: null,
            Severity: CveSeverity.High,
            Summary: "test advisory",
            Package: package,
            Ecosystem: ecosystem,
            VulnerableRange: "< 9.9.9",
            FixedVersion: "9.9.9",
            Url: "https://example.test/advisory",
            Source: "dependabot");
}
