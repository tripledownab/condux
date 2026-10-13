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
    private sealed class StubScanner(bool truncated, params CveFinding[] findings) : ICveScanner
    {
        public string Name => "stub";

        public Task<CveScan> ScanAsync(
            string token, string repoFullName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CveScan(findings, truncated));
    }

    // A scan GitHub refused, as when the App lacks the Dependabot-alerts permission.
    private sealed class FailingScanner(Exception? failure = null) : ICveScanner
    {
        public string Name => "failing";

        public Task<CveScan> ScanAsync(
            string token, string repoFullName, CancellationToken cancellationToken = default) =>
            throw failure ?? new HttpRequestException("403 Forbidden");
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
    private Task<(HttpClient Client, long ProjectId, string RepoId)> ProvisionAsync(
        params CveFinding[] findings) => ProvisionAsync(truncated: false, findings);

    private Task<(HttpClient Client, long ProjectId, string RepoId)> ProvisionAsync(
        bool truncated, params CveFinding[] findings) =>
        ProvisionAsync(new StubScanner(truncated, findings));

    private async Task<(HttpClient Client, long ProjectId, string RepoId)> ProvisionAsync(ICveScanner scanner)
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString, ch, configure: b =>
            b.ConfigureServices(services =>
            {
                services.AddSingleton(scanner);
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

    private static async Task<JsonElement> ReadResponseAsync(HttpClient client, long projectId, string repoId)
    {
        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/cve-findings");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ReadFindingsAsync(HttpClient client, long projectId, string repoId) =>
        (await ReadResponseAsync(client, projectId, repoId)).GetProperty("findings");

    /// <summary>
    /// The feature, end to end. 4.17.11 was recorded running and the advisory's range is "&lt;
    /// 4.17.12", so the row comes back Exposed, with the sighting confirmed and its version, release
    /// and environment beside the advisory's own fixed version.
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
        // 2 is ModuleExposureState.Exposed; the enum crosses the wire as an integer like every other.
        Assert.Equal(2, exposure.GetProperty("state").GetInt32());
        var observed = exposure.GetProperty("observed").EnumerateArray().Single();
        Assert.True(observed.GetProperty("confirmed").GetBoolean());
        Assert.Equal("4.17.11", observed.GetProperty("version").GetString());
        Assert.Equal("production", observed.GetProperty("environment").GetString());
        Assert.Equal("1.4.2", observed.GetProperty("release").GetString());
    }

    /// <summary>
    /// A version past the fix reads Running and unconfirmed, never as safe: the wire carries no state
    /// that says a package is unaffected.
    /// </summary>
    [Fact]
    public async Task Finding_whose_running_version_is_past_the_fix_reads_running()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("lodash", "npm"));
        await RecordModuleAsync(
            projectId, "npm", "lodash", "4.17.21", new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

        var exposure = (await ReadFindingsAsync(client, projectId, repoId)).EnumerateArray().Single()
            .GetProperty("exposure");

        Assert.Equal(1, exposure.GetProperty("state").GetInt32());
        Assert.False(exposure.GetProperty("observed").EnumerateArray().Single().GetProperty("confirmed").GetBoolean());
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
    /// Unknown, and a Python customer would conclude their vulnerable dependency was not running. 2.19.0
    /// is inside the finding's range, so it reads Exposed, which is the pip matcher over HTTP.
    /// </summary>
    [Fact]
    public async Task Dependabot_ecosystem_lines_up_with_the_recorded_osv_one()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("requests", "pip"));
        await RecordModuleAsync(
            projectId, "PyPI", "requests", "2.19.0", new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

        var row = (await ReadFindingsAsync(client, projectId, repoId)).EnumerateArray().Single();

        Assert.Equal(2, row.GetProperty("exposure").GetProperty("state").GetInt32());
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

    /// <summary>
    /// A scan that stopped before the end says so on the wire. Without the flag the dashboard shows the
    /// count it received as the total, which is how a repo with more open alerts than one page read as
    /// having exactly one page of them.
    /// </summary>
    [Fact]
    public async Task A_truncated_scan_is_reported_as_truncated()
    {
        var (client, projectId, repoId) = await ProvisionAsync(truncated: true, Finding("lodash", "npm"));

        var body = await ReadResponseAsync(client, projectId, repoId);

        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.Single(body.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task A_complete_scan_is_not_reported_as_truncated()
    {
        var (client, projectId, repoId) = await ProvisionAsync(Finding("lodash", "npm"));

        Assert.False((await ReadResponseAsync(client, projectId, repoId)).GetProperty("truncated").GetBoolean());
    }

    /// <summary>
    /// A scan that failed is not a complete answer. It still answers 200 with no findings, since the
    /// surface is advisory, but marks the list truncated so the dashboard does not read the silence as
    /// "this repository has no open alerts".
    /// </summary>
    [Fact]
    public async Task A_failed_scan_returns_no_findings_marked_truncated()
    {
        var (client, projectId, repoId) = await ProvisionAsync(new FailingScanner());

        var body = await ReadResponseAsync(client, projectId, repoId);

        Assert.Empty(body.GetProperty("findings").EnumerateArray());
        Assert.True(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_scan_that_times_out_returns_no_findings_marked_truncated_not_a_500()
    {
        // HttpClient reports its own timeout as a cancellation nobody asked for. It used to escape this
        // best-effort section as if the request had been aborted, so a slow GitHub answered 500.
        var (client, projectId, repoId) =
            await ProvisionAsync(new FailingScanner(new TaskCanceledException("timed out")));

        var body = await ReadResponseAsync(client, projectId, repoId);

        Assert.Empty(body.GetProperty("findings").EnumerateArray());
        Assert.True(body.GetProperty("truncated").GetBoolean());
    }
}
