using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.Core.Quotas;
using Xunit;

namespace Condux.Core.Tests;

public class CveFixOrchestratorTests
{
    private static readonly CveFixJob Job = new(
        Guid.NewGuid(), "acme/api", "main", "GHSA-jf85-cpcp-j695", "CVE-2019-10744",
        "lodash", "npm", "< 4.17.12", "4.17.12", "https://gh/acme/api/security/dependabot/1",
        "dev@example.com", "claude-opus-4-8")
    {
        Prompt = "Bump lodash to 4.17.12.",
        ScopedPaths = ["package.json"],
    };

    [Fact]
    public async Task RunAsync_Succeeds_RecordsTheDraftBumpPr()
    {
        var store = new InMemoryCveFixStore();
        var run = await new CveFixOrchestrator(store, new FakeFixProvider()).RunAsync(Job);

        Assert.Equal(FixStatus.Succeeded, run.Status);
        Assert.Equal("fake", run.Provider);
        Assert.Equal("GHSA-jf85-cpcp-j695", run.GhsaId);
        Assert.Equal("lodash", run.Package);
        Assert.Equal("4.17.12", run.ToVersion);
        Assert.Contains("example.invalid", run.PrUrl);
        Assert.NotEqual("", run.Branch);

        // The persisted row reflects the final state.
        Assert.Equal(FixStatus.Succeeded, store.Current[run.Id].Status);
    }

    [Fact]
    public async Task RunAsync_PassesTheAdvisoryAsTheProvidersIssueId_AndTheBumpPrompt()
    {
        var capturing = new CapturingProvider();
        await new CveFixOrchestrator(new InMemoryCveFixStore(), capturing).RunAsync(Job);

        Assert.Equal("GHSA-jf85-cpcp-j695", capturing.LastRequest!.IssueId); // advisory rides as the issue id
        Assert.Equal("Bump lodash to 4.17.12.", capturing.LastRequest.Prompt);
        Assert.Equal(["package.json"], capturing.LastRequest.ScopedPaths);
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_MarksFailed()
    {
        var store = new InMemoryCveFixStore();
        var run = await new CveFixOrchestrator(store, new ThrowingProvider()).RunAsync(Job);

        Assert.Equal(FixStatus.Failed, run.Status);
        Assert.Equal(FixStatus.Failed, store.Current[run.Id].Status);
    }

    [Fact]
    public async Task RunAsync_RefundsTheAllowance_OnlyWhenTheRunFails()
    {
        var quota = new RecordingQuota();
        var withOrg = Job with { OrgId = 7 };

        await new CveFixOrchestrator(new InMemoryCveFixStore(), new ThrowingProvider(), quota).RunAsync(withOrg);
        Assert.Equal([7L], quota.Refunds);

        await new CveFixOrchestrator(new InMemoryCveFixStore(), new FakeFixProvider(), quota).RunAsync(withOrg);
        Assert.Equal([7L], quota.Refunds); // a success keeps the reservation

        // A job enqueued outside the quota gate (OrgId 0) never refunds.
        await new CveFixOrchestrator(new InMemoryCveFixStore(), new ThrowingProvider(), quota).RunAsync(Job);
        Assert.Equal([7L], quota.Refunds);
    }

    private sealed class RecordingQuota : IAiFixQuota
    {
        public List<long> Refunds { get; } = [];

        public Task<bool> TryConsumeAsync(long orgId, int monthlyLimit, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task RefundAsync(long orgId, DateTimeOffset nowUtc, CancellationToken ct = default)
        {
            Refunds.Add(orgId);
            return Task.CompletedTask;
        }

        public Task<int> GetUsedAsync(long orgId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<bool> TryConsumeLifetimeAsync(
            long orgId, int lifetimeLimit, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task RefundLifetimeAsync(long orgId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> GetLifetimeUsedAsync(long orgId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ThrowingProvider : IFixProvider
    {
        public string Name => "throwing";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider boom");
    }

    private sealed class CapturingProvider : IFixProvider
    {
        public FixRequest? LastRequest { get; private set; }

        public string Name => "capturing";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new FixResult("condux/fix-x", "https://example.invalid/x/pull/0", "s"));
        }
    }

    private sealed class InMemoryCveFixStore : ICveFixStore
    {
        public readonly Dictionary<Guid, CveFixRun> Current = [];

        public Task InsertAsync(CveFixRun r, CancellationToken ct = default)
        {
            Current[r.Id] = r;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(CveFixRun r, CancellationToken ct = default)
        {
            Current[r.Id] = r;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CveFixRun>> ListByRepoAsync(Guid repoLinkId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CveFixRun>>([.. Current.Values.Where(r => r.RepoLinkId == repoLinkId)]);

        public Task<bool> HasActiveRunAsync(Guid repoLinkId, string ghsaId, CancellationToken ct = default) =>
            Task.FromResult(Current.Values.Any(r =>
                r.RepoLinkId == repoLinkId && r.GhsaId == ghsaId
                && r.Status is FixStatus.Pending or FixStatus.Running));
    }
}
