using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.CveScanning;
using Condux.Core.SourceControl;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The CVE findings endpoint answering with runtime exposure (ADR-0041), over HTTP against real
/// stores. This is the one join nothing else covers: the reader has its own tests, the classifier has
/// its own, and the endpoint's no-GitHub path is covered in ReposApiTest, but only here do a scanner's
/// findings and ClickHouse's inventory meet inside the handler.
///
/// <para>The scanner and the token minter are stubbed because the alternative is calling GitHub. Every
/// other part is real: the route, the role filter, the installation lookup in Postgres, the ClickHouse
/// query and the pure matching rule.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CveFindingExposureApiTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    private sealed class StubScanner(params CveFinding[] findings) : ICveScanner
    {
        public string Name => "stub";

        public Task<IReadOnlyList<CveFinding>> ScanAsync(
            string token, string repoFullName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CveFinding>>(findings);
    }

    // Only GetAsync is implemented: the findings path mints one installation token and never asks for
    // the read-only downscoped one, whose default implementation throws if anything ever does.
    private sealed class StubTokens : ISourceHostTokens
    {
        public Task<string> GetAsync(long installationId, CancellationToken ct = default) =>
            Task.FromResult("ghs_stub");
    }

    private static CveFinding Finding(string package, string ecosystem) =>
        new(
            AdvisoryId: "GHSA-jf85-cpcp-j695",
            CveId: "CVE-2019-10744",
            Severity: CveSeverity.Critical,
            Summary: "Prototype pollution",
            Package: package,
            Ecosystem: ecosystem,
            VulnerableRange: "< 4.17.12",
            FixedVersion: "4.17.12",
            Url: "https://example.test/advisory",
            Source: "stub");

    /// <summary>Provision org, project and a linked repo, with a scanner that reports these findings.</summary>
    private async Task<(HttpClient Client, long ProjectId, string RepoId)> ProvisionAsync(
        params CveFinding[] findings)
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString, ch, configure: b =>
            b.ConfigureServices(services =>
            {
                services.AddSingleton<ICveScanner>(new StubScanner(findings));
                services.AddSingleton<ISourceHostTokens, StubTokens>();
            })).CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);

        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "javascript" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        // The endpoint returns early unless the org has an installation, so the scan never runs without it.
        await new GithubInstallationRepository(pg.ConnectionString)
            .LinkAsync(Random.Shared.NextInt64(1, long.MaxValue), orgId, "acme");

        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });
        var repoId = (await linkResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        return (client, projectId, repoId);
    }

    private async Task RecordModuleAsync(
        long projectId, string ecosystem, string package, string version, DateTimeOffset seenAt)
    {
        using var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        await new ClickHouseReleaseModuleWriter(http).InsertAsync(
        [
            ClickHouseReleaseModuleWriter.ToRow(
                projectId.ToString(CultureInfo.InvariantCulture), "1.4.2", "production",
                new ReleaseModule(ecosystem, package, version), seenAt, 90),
        ]);
    }

    private static async Task<JsonElement> ReadFindingsAsync(HttpClient client, long projectId, string repoId)
    {
        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/cve-findings");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// The feature, end to end. A version of the advisory's package was recorded running, so the row
    /// comes back as Running with the observed version, release and environment beside the advisory's
    /// own fixed version. Nothing asserts which side of the range it falls on: this slice ships no
    /// matcher and the comparison is the reader's.
    /// </summary>
    [Fact]
    public async Task Finding_whose_package_is_running_reports_the_observed_version()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("lodash", "npm"));
        var seenAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        await RecordModuleAsync(projectId, "npm", "lodash", "4.17.11", seenAt);

        var row = (await ReadFindingsAsync(client, projectId, repoId)).EnumerateArray().Single();

        Assert.Equal("lodash", row.GetProperty("finding").GetProperty("package").GetString());
        Assert.Equal("4.17.12", row.GetProperty("finding").GetProperty("fixedVersion").GetString());

        var exposure = row.GetProperty("exposure");
        // 1 is ModuleExposureState.Running; the enum crosses the wire as an integer like every other.
        Assert.Equal(1, exposure.GetProperty("state").GetInt32());
        var observed = exposure.GetProperty("observed").EnumerateArray().Single();
        Assert.Equal("4.17.11", observed.GetProperty("version").GetString());
        Assert.Equal("production", observed.GetProperty("environment").GetString());
        Assert.Equal("1.4.2", observed.GetProperty("release").GetString());
    }

    /// <summary>
    /// With nothing recorded the finding still appears, carrying Unknown and no sightings. This is the
    /// state of every finding until an SDK sends a module list, so the advisory must not be hidden or
    /// altered by the annotation failing to find anything.
    /// </summary>
    [Fact]
    public async Task Finding_with_no_inventory_still_appears_and_reads_unknown()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("left-pad", "npm"));

        var row = (await ReadFindingsAsync(client, projectId, repoId)).EnumerateArray().Single();

        Assert.Equal("left-pad", row.GetProperty("finding").GetProperty("package").GetString());
        Assert.Equal(0, row.GetProperty("exposure").GetProperty("state").GetInt32());
        Assert.Empty(row.GetProperty("exposure").GetProperty("observed").EnumerateArray());
    }

    /// <summary>
    /// Dependabot says "pip" and we record OSV's "PyPI". Without the ecosystem fold this row would read
    /// Unknown, and a Python customer would conclude their vulnerable dependency was not running.
    /// </summary>
    [Fact]
    public async Task Dependabot_ecosystem_lines_up_with_the_recorded_osv_one()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("requests", "pip"));
        await RecordModuleAsync(
            projectId, "PyPI", "requests", "2.19.0", new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

        var row = (await ReadFindingsAsync(client, projectId, repoId)).EnumerateArray().Single();

        Assert.Equal(1, row.GetProperty("exposure").GetProperty("state").GetInt32());
        Assert.Equal(
            "2.19.0",
            row.GetProperty("exposure").GetProperty("observed").EnumerateArray().Single()
                .GetProperty("version").GetString());
    }

    /// <summary>
    /// Another project's inventory never leaks in. A dependency inventory is a supply-chain map of a
    /// customer's application, so this is asserted at the HTTP boundary and not only in the query test.
    /// </summary>
    [Fact]
    public async Task Another_projects_running_version_is_not_attributed_to_this_one()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("lodash", "npm"));
        await RecordModuleAsync(
            projectId + 9999, "npm", "lodash", "0.0.1",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

        var row = (await ReadFindingsAsync(client, projectId, repoId)).EnumerateArray().Single();

        Assert.Equal(0, row.GetProperty("exposure").GetProperty("state").GetInt32());
    }
}
