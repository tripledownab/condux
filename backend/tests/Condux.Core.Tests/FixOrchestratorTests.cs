using System.Text.Json;
using Condux.Core.FixEngine;
using Condux.Core.Quotas;
using Xunit;

namespace Condux.Core.Tests;

public class FixOrchestratorTests
{
    private static readonly FixJob Job = new(42, "acme/api", "main", "dev@example.com", "claude-opus-4-8");

    [Fact]
    public async Task RunAsync_Succeeds_RecordsDraftPrAndAudit()
    {
        var store = new InMemoryFixStore();
        var result = await new FixOrchestrator(store, new FakeFixProvider()).RunAsync(Job);

        Assert.Equal(FixStatus.Succeeded, result.Status);
        Assert.Equal("fake", result.Provider);
        Assert.Equal(42, result.IssueId);
        Assert.Contains("example.invalid", result.PrUrl);
        Assert.NotEqual("", result.Branch);

        // The persisted row reflects the final state, and the run is audited end to end.
        Assert.Equal(FixStatus.Succeeded, store.Current[result.Id].Status);
        Assert.Equal(["requested", "draft_pr_opened"], store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_MarksFailedAndAudits()
    {
        var store = new InMemoryFixStore();
        var result = await new FixOrchestrator(store, new ThrowingProvider()).RunAsync(Job);

        Assert.Equal(FixStatus.Failed, result.Status);
        Assert.Equal(FixStatus.Failed, store.Current[result.Id].Status);
        Assert.Equal(["requested", "failed"], store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_WithManagedAgentProvider_DrivesTheFullStartPollLifecycle()
    {
        // The managed provider orchestrates a simulated backend end to end (start → poll → draft PR),
        // so the orchestrator records a SUCCEEDED run named after the backend, with no LLM or real PR.
        var store = new InMemoryFixStore();
        var provider = new ManagedAgentFixProvider(
            new SimulatedAgentGateway(pollsWhileRunning: 1),
            new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 10),
            (_, _) => Task.CompletedTask);

        var result = await new FixOrchestrator(store, provider).RunAsync(Job);

        Assert.Equal(FixStatus.Succeeded, result.Status);
        Assert.Equal("simulated-agent", result.Provider);
        Assert.Contains("example.invalid", result.PrUrl);
        Assert.Equal(["requested", "draft_pr_opened"], store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_AuditsWhoRepoProviderModelAndPromptHash()
    {
        var store = new InMemoryFixStore();
        var result = await new FixOrchestrator(store, new FakeFixProvider())
            .RunAsync(Job with { Prompt = "Assembled, scrubbed context." });

        using var requested = JsonDocument.Parse(store.Detail(result.Id, "requested"));
        Assert.Equal("acme/api", requested.RootElement.GetProperty("repo").GetString());
        Assert.Equal("claude-opus-4-8", requested.RootElement.GetProperty("model").GetString());
        Assert.Equal("fake", requested.RootElement.GetProperty("provider").GetString());
        Assert.Equal(64, requested.RootElement.GetProperty("promptHash").GetString()!.Length); // SHA-256 hex

        using var opened = JsonDocument.Parse(store.Detail(result.Id, "draft_pr_opened"));
        Assert.Contains("example.invalid", opened.RootElement.GetProperty("prUrl").GetString());
        Assert.NotEqual("", opened.RootElement.GetProperty("branch").GetString());
        // Token usage is audited per run (0 for the no-model fake) so pricing calibrates on real cost.
        Assert.Equal(0, opened.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0, opened.RootElement.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task RunAsync_PassesTheJobsAssembledPrompt_FallingBackWhenEmpty()
    {
        var capturing = new CapturingProvider();

        await new FixOrchestrator(new InMemoryFixStore(), capturing)
            .RunAsync(Job with { Prompt = "Assembled, scrubbed context." });
        Assert.Equal("Assembled, scrubbed context.", capturing.LastPrompt);

        await new FixOrchestrator(new InMemoryFixStore(), capturing).RunAsync(Job);
        Assert.Equal("Fix issue 42.", capturing.LastPrompt); // empty prompt → minimal fallback
    }

    [Fact]
    public async Task RunAsync_RefundsTheQuotaReservation_OnlyWhenTheRunFails()
    {
        var quota = new RecordingQuota();
        var jobWithOrg = Job with { OrgId = 7 };

        // A failed run gives the reservation back; a successful one keeps it consumed.
        await new FixOrchestrator(new InMemoryFixStore(), new ThrowingProvider(), quota).RunAsync(jobWithOrg);
        Assert.Equal([7L], quota.Refunds);

        await new FixOrchestrator(new InMemoryFixStore(), new FakeFixProvider(), quota).RunAsync(jobWithOrg);
        Assert.Equal([7L], quota.Refunds);

        // A job enqueued outside the quota gate (OrgId 0) never refunds.
        await new FixOrchestrator(new InMemoryFixStore(), new ThrowingProvider(), quota).RunAsync(Job);
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
            Task.FromResult(Refunds.Count);

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
        public string? LastPrompt { get; private set; }

        public string Name => "capturing";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            LastPrompt = request.Prompt;
            return Task.FromResult(new FixResult("condux/fix-42", "https://example.invalid/x/pull/0", "s"));
        }
    }

    private sealed class InMemoryFixStore : IFixStore
    {
        public readonly Dictionary<Guid, FixSuggestion> Current = [];
        private readonly List<(Guid FixId, string Event, string Detail)> _audit = [];

        public string[] Events(Guid fixId) => [.. _audit.Where(a => a.FixId == fixId).Select(a => a.Event)];

        public string Detail(Guid fixId, string eventName) =>
            _audit.First(a => a.FixId == fixId && a.Event == eventName).Detail;

        public Task InsertAsync(FixSuggestion s, CancellationToken ct = default)
        {
            Current[s.Id] = s;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(FixSuggestion s, CancellationToken ct = default)
        {
            Current[s.Id] = s;
            return Task.CompletedTask;
        }

        public Task AppendAuditAsync(Guid fixId, string actor, string eventName, string detailJson, CancellationToken ct = default)
        {
            _audit.Add((fixId, eventName, detailJson));
            return Task.CompletedTask;
        }

        public Task<FixSuggestion?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Current.TryGetValue(id, out var s) ? s : null);

        public Task<IReadOnlyList<FixSuggestion>> ListByIssueAsync(long issueId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FixSuggestion>>([.. Current.Values.Where(s => s.IssueId == issueId)]);
    }
}
