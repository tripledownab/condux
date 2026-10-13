using Condux.Agent;
using Condux.Conductor;
using Condux.Core.FixEngine;
using Condux.Core.Llm;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The BYO/vendor routing composite (ADR-0038): an org with a stored BYO LLM config keeps the
/// in-process gateway (its key, its provider), everyone else goes to Managed Agents, and the choice
/// survives polling via the run-id prefix — no routing state to lose. Plus the poll-cadence config that
/// finally makes ManagedAgentOptions env-tunable.
/// </summary>
public sealed class RoutingAgentGatewayTests
{
    private sealed class RecordingGateway(string name) : IAgentGateway
    {
        public List<string> Started { get; } = [];
        public List<string> Polled { get; } = [];
        public string BackendName => name;

        public Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken ct = default)
        {
            Started.Add(spec.IssueId);
            return Task.FromResult(new AgentRun($"{name}-run"));
        }

        public List<string> Cancelled { get; } = [];

        public bool? LastPublishFinished { get; private set; }

        public Task<AgentRunProgress> CancelAsync(string runId, CancellationToken ct = default, bool publishFinished = false)
        {
            Cancelled.Add(runId);
            LastPublishFinished = publishFinished;
            return Task.FromResult(AgentRunProgress.Failed("stopped"));
        }

        public Task<AgentRunProgress> PollAsync(string runId, CancellationToken ct = default)
        {
            Polled.Add(runId);
            return Task.FromResult(AgentRunProgress.Done("b", $"https://pr/{name}", "s"));
        }
    }

    private sealed class FakeConfigs(params long[] orgsWithConfig) : ILlmConfigReader
    {
        public Task<StoredLlmConfig?> GetAsync(long orgId, CancellationToken ct = default) =>
            Task.FromResult(orgsWithConfig.Contains(orgId)
                ? new StoredLlmConfig(orgId, "anthropic", "m", "", [], DateTimeOffset.UnixEpoch)
                : null);
    }

    private static AgentRunSpec Spec(long orgId) =>
        new("iss-1", "acme/app", "main", "fix", "model") { RunId = Guid.CreateVersion7(), OrgId = orgId };

    [Fact]
    public async Task A_byo_org_routes_to_the_in_process_gateway_and_polls_it_back()
    {
        var byo = new RecordingGateway("byo-gw");
        var managed = new RecordingGateway("cma-gw");
        var router = new RoutingAgentGateway(byo, managed, new FakeConfigs(3));

        var run = await router.StartAsync(Spec(orgId: 3));
        var progress = await router.PollAsync(run.RunId);

        Assert.StartsWith("byo:", run.RunId);
        Assert.Single(byo.Started);
        Assert.Empty(managed.Started);
        Assert.Equal(["byo-gw-run"], byo.Polled); // the prefix is stripped before the inner poll
        Assert.Equal("https://pr/byo-gw", progress.PrUrl);
    }

    [Fact]
    public async Task A_stop_reaches_the_backend_that_started_the_run()
    {
        var byo = new RecordingGateway("byo-gw");
        var managed = new RecordingGateway("cma-gw");
        var router = new RoutingAgentGateway(byo, managed, new FakeConfigs(3));
        var byoRun = await router.StartAsync(Spec(orgId: 3));
        var managedRun = await router.StartAsync(Spec(orgId: 4));

        await router.CancelAsync(byoRun.RunId);
        await router.CancelAsync(managedRun.RunId, publishFinished: true);

        Assert.Equal(["byo-gw-run"], byo.Cancelled);
        Assert.Equal(["cma-gw-run"], managed.Cancelled);
        // Whether a finished answer may still be published reaches the backend unchanged.
        Assert.False(byo.LastPublishFinished);
        Assert.True(managed.LastPublishFinished);
    }

    [Fact]
    public async Task An_org_without_a_config_routes_to_managed_agents()
    {
        var byo = new RecordingGateway("byo-gw");
        var managed = new RecordingGateway("cma-gw");
        var router = new RoutingAgentGateway(byo, managed, new FakeConfigs(99));

        var run = await router.StartAsync(Spec(orgId: 3));

        Assert.StartsWith("cma:", run.RunId);
        Assert.Empty(byo.Started);
        Assert.Single(managed.Started);
    }

    [Fact]
    public async Task No_config_reader_and_org_zero_both_route_to_managed_agents()
    {
        var byo = new RecordingGateway("byo-gw");
        var managed = new RecordingGateway("cma-gw");

        var noReader = new RoutingAgentGateway(byo, managed, configs: null);
        Assert.StartsWith("cma:", (await noReader.StartAsync(Spec(orgId: 3))).RunId);

        var orgZero = new RoutingAgentGateway(byo, managed, new FakeConfigs(0));
        Assert.StartsWith("cma:", (await orgZero.StartAsync(Spec(orgId: 0))).RunId);
        Assert.Equal(2, managed.Started.Count);
        Assert.Empty(byo.Started);
    }

    [Fact]
    public async Task A_run_id_without_a_backend_prefix_throws()
    {
        var router = new RoutingAgentGateway(
            new RecordingGateway("a"), new RecordingGateway("b"), configs: null);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => router.PollAsync("naked-run-id"));
    }

    [Fact]
    public void Poll_options_default_by_provider_and_read_the_env()
    {
        var empty = new ConfigurationBuilder().Build();
        var inProcess = ConductorPollOptions.FromEnv(empty, "anthropic");
        Assert.Equal(ManagedAgentOptions.Default, inProcess);

        var managed = ConductorPollOptions.FromEnv(empty, "managed-agents");
        Assert.Equal(TimeSpan.FromSeconds(10), managed.PollInterval);
        Assert.Equal(240, managed.MaxPolls); // a vendor session legitimately runs tens of minutes

        var tuned = ConductorPollOptions.FromEnv(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONDUX_CONDUCTOR_POLL_SECONDS"] = "3",
                ["CONDUX_CONDUCTOR_MAX_POLLS"] = "7",
            }).Build(), "anthropic");
        Assert.Equal(TimeSpan.FromSeconds(3), tuned.PollInterval);
        Assert.Equal(7, tuned.MaxPolls);

        Assert.Throws<InvalidOperationException>(() => ConductorPollOptions.FromEnv(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONDUX_CONDUCTOR_MAX_POLLS"] = "0",
            }).Build(), "anthropic"));

        // Compose passes both as ${VAR:-}, so unset arrives as an empty string and keeps the default.
        var blank = ConductorPollOptions.FromEnv(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONDUX_CONDUCTOR_POLL_SECONDS"] = "",
                ["CONDUX_CONDUCTOR_MAX_POLLS"] = "",
            }).Build(), "managed-agents");
        Assert.Equal(managed, blank);
    }

    [Theory]
    [InlineData("86400", "1", true)]
    [InlineData("86401", "1", false)]
    [InlineData("2147483647", "2147483647", false)] // overflowed the budget and the sweep's cutoff
    public void Poll_options_refuse_a_budget_past_a_day(string seconds, string maxPolls, bool accepted)
    {
        // The sweep fails a run only after twice the budget, so a longer one only delays the refund of a
        // run the Conductor lost, and a large enough one overflowed the arithmetic and stopped the host.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CONDUX_CONDUCTOR_POLL_SECONDS"] = seconds,
            ["CONDUX_CONDUCTOR_MAX_POLLS"] = maxPolls,
        }).Build();

        if (accepted)
        {
            Assert.Equal(TimeSpan.FromDays(1), ConductorPollOptions.FromEnv(config, "anthropic").PollBudget);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => ConductorPollOptions.FromEnv(config, "anthropic"));
        }
    }
}
