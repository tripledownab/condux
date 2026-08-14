using Condux.Agent.Sandbox;
using Condux.Core.CveScanning;
using Condux.Core.SourceControl;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The forge-agnostic CVE path end to end: read lockfiles, run the scanner in a container, parse the
/// report. Real Docker and the real advisory database, because the parts worth proving here cannot be
/// faked — that the tool starts at all in our container, that it can reach the database it needs, and
/// that its output still matches what the reader expects.
///
/// The forge is faked, since fetching files is not what is under test and a real one would need
/// credentials.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OsvCveScannerTest
{
    // lodash 4.17.15 has long-published advisories with fixes, so the assertions do not depend on a
    // specific CVE staying current — only on this old version remaining vulnerable, which it always will.
    private const string PackageLock = """
        {
          "name": "probe", "version": "1.0.0", "lockfileVersion": 3,
          "packages": {
            "": { "name": "probe", "version": "1.0.0", "dependencies": { "lodash": "4.17.15" } },
            "node_modules/lodash": { "version": "4.17.15" }
          }
        }
        """;

    /// <summary>A forge holding one vulnerable lockfile and nothing else.</summary>
    private sealed class FakeForge(IReadOnlyDictionary<string, string> files) : ISourceHostClient
    {
        public Task<IReadOnlyList<string>> ListTreeAsync(
            string token, string repoFullName, string gitRef, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. files.Keys]);

        public Task<RepoFile?> GetFileAsync(
            string token, string repoFullName, string path, string gitRef, CancellationToken ct = default) =>
            Task.FromResult(files.TryGetValue(path, out var content)
                ? new RepoFile(path, content, "sha")
                : null);

        public Task<string> GetBranchHeadShaAsync(
            string token, string repoFullName, string branch, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CreateBranchAsync(
            string token, string repoFullName, string branch, string sha, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PutFileAsync(
            string token, string repoFullName, string path, string branch, string message,
            string contents, string? sha, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListBranchesAsync(
            string token, string repoFullName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(
            string token, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SuspectCommit?> GetLatestCommitTouchingAsync(
            string token, string repoFullName, string path, string gitRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> OpenDraftPullRequestAsync(
            string token, string repoFullName, string head, string baseBranch, string title,
            string body, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static SandboxOptions Options() => new(
        SandboxDaemon.Local(),
        Image: "ghcr.io/google/osv-scanner:latest",
        MemoryBytes: 1024L * 1024 * 1024,
        PidsLimit: 256,
        CommandTimeout: TimeSpan.FromMinutes(3));

    /// <summary>
    /// Reaches osv.dev, so it runs only when asked for. It proves something no fixture can — that the tool
    /// starts in our container and can reach the database it needs — but as a standing check it would make
    /// an unrelated pull request go red whenever that service is slow, rate limiting or down. The report
    /// reader is covered against captured output in the unit suite, which is where the wire format that
    /// actually breaks is pinned.
    /// </summary>
    [Fact]
    public async Task Finds_a_known_vulnerable_dependency_through_the_whole_chain()
    {
        if (Environment.GetEnvironmentVariable("CONDUX_TEST_OSV_LIVE") is not "1")
        {
            return;
        }

        var scanner = new OsvCveScanner(
            new FakeForge(new Dictionary<string, string> { ["package-lock.json"] = PackageLock }), Options());

        var findings = await scanner.ScanAsync("token", "acme/api");

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("lodash", f.Package));
        Assert.All(findings, f => Assert.Equal("npm", f.Ecosystem));
        // A finding nobody can act on is not worth surfacing, so at least one must name a fix version.
        Assert.Contains(findings, f => f.IsFixable);
        // Severity comes from the scanner's score; all-Unknown would mean the report shape moved.
        Assert.Contains(findings, f => f.Severity != CveSeverity.Unknown);
    }

    [Fact]
    public async Task A_repository_with_no_lockfile_scans_clean_without_starting_a_container()
    {
        var scanner = new OsvCveScanner(
            new FakeForge(new Dictionary<string, string> { ["README.md"] = "# hi" }), Options());

        // Plenty of repositories have no dependencies to scan; that is a clean result, not a failure, and
        // it should not cost a container start to discover.
        Assert.Empty(await scanner.ScanAsync("token", "acme/api"));
    }
}
