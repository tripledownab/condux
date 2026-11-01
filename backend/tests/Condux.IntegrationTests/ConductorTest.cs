using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The Conductor spine against a real Postgres (#60): seed an issue, run the orchestrator with the
/// fake provider, and assert the fix run + its audit trail persist through <see cref="PostgresFixStore"/>.
/// No LLM or GitHub is touched — the fake provider fabricates the draft PR.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConductorTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RunAsync_PersistsSucceededSuggestion()
    {
        // Seed an issue so the fix_suggestions.issue_id FK is satisfied; use its internal bigint id.
        // The issue needs a real project (issues.project_id FK, #97).
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var issues = new IssueRepository(pg.ConnectionString);
        var upsert = await issues.UpsertAsync(
            projectId, new Grouping("fp-fix", "ValueError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);

        var store = new PostgresFixStore(pg.ConnectionString);
        var job = new FixJob(upsert.Id, "acme/api", "main", "dev@example.com", "claude-opus-4-8");

        var result = await new FixOrchestrator(store, new FakeFixProvider()).RunAsync(job);

        Assert.NotNull(result);
        Assert.Equal(FixStatus.Succeeded, result.Status);
        Assert.Contains("example.invalid", result.PrUrl);

        var fetched = await store.GetAsync(result.Id);
        Assert.NotNull(fetched);
        Assert.Equal(FixStatus.Succeeded, fetched!.Status);
        Assert.Equal(upsert.Id, fetched.IssueId);
        Assert.Equal("acme/api", fetched.RepoFullName);
        Assert.Equal("fake", fetched.Provider);
    }

    /// <summary>
    /// A run's summary is the model's text, and Postgres text refuses U+0000, so a summary carrying one
    /// failed the success write after the draft PR existed, leaving the PR recorded nowhere. The character
    /// is stored as U+FFFD and the run as succeeded.
    /// </summary>
    [Fact]
    public async Task RunAsync_StoresASummaryHoldingANul()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var upsert = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-nul-summary", "ValueError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var store = new PostgresFixStore(pg.ConnectionString);
        var job = new FixJob(upsert.Id, "acme/api", "main", "dev@example.com", "claude-opus-4-8");

        var result = await new FixOrchestrator(store, new NulSummaryProvider()).RunAsync(job);

        var fetched = await store.GetAsync(result!.Id);
        Assert.Equal(FixStatus.Succeeded, fetched!.Status);
        Assert.Equal("fixed a\uFFFDb", fetched.Summary);
    }

    private sealed class NulSummaryProvider : IFixProvider
    {
        public string Name => "fake";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FixResult("condux/fix", "https://example.invalid/pull/1", "fixed a\0b"));
    }
}
