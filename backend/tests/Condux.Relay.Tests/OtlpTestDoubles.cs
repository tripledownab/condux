using System.Globalization;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Relay.Ingest;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.Relay.Tests;

// Doubles the OTLP endpoint tests share, so the logs and the traces suites drive the same quota and
// broker behaviour and a change to one is a change to both.

// Records the count of every call as well as answering it, so a test can say how many times the
// handler asked and for how much. Returning a fixed decision keeps the meter's own arithmetic out of
// the test: the endpoint's job is to publish exactly what it was told was admitted.
internal sealed class StubQuotaMeter(QuotaDecision decision) : IQuotaMeter
{
    public List<long> Requested { get; } = [];
    public List<long> Refunded { get; } = [];
    public List<string> Keys { get; } = [];

    public ValueTask<QuotaDecision> TryConsumeAsync(
        string key, long monthlyLimit, long count = 1, CancellationToken ct = default)
    {
        Requested.Add(count);
        Keys.Add(key);
        return ValueTask.FromResult(decision);
    }

    public ValueTask RefundAsync(string key, long count, CancellationToken ct = default)
    {
        Refunded.Add(count);
        Keys.Add(key);
        return ValueTask.CompletedTask;
    }
}

// Stores the first few and then fails, standing in for a broker that stops answering mid-batch.
internal sealed class FailAfterPublisher(int succeed) : IEventPublisher
{
    public int Published { get; private set; }

    public Task PublishAsync(
        string projectId, Event e, int retentionDays, long sentAtUnixMs, CancellationToken cancellationToken = default)
    {
        if (Published >= succeed)
        {
            throw new InvalidOperationException("broker unreachable");
        }
        Published++;
        return Task.CompletedTask;
    }
}

internal static class OtlpTestHosts
{
    /// <summary>The relay with the given services in place of its own. The per-export ceiling defaults to
    /// 20,000, so a test drives it down rather than building a body big enough to reach it.</summary>
    public static WebApplicationFactory<Program> WithOtlpServices(
        this WebApplicationFactory<Program> factory, IEventPublisher publisher, IQuotaMeter? quota = null,
        int? maxRecords = null, IProjectStore? projects = null, IngestBodyBudget? budget = null) =>
        factory.WithWebHostBuilder(b =>
        {
            if (maxRecords is not null)
            {
                b.UseSetting("CONDUX_MAX_OTLP_RECORDS", maxRecords.Value.ToString(CultureInfo.InvariantCulture));
            }
            b.ConfigureServices(s =>
            {
                s.AddSingleton(publisher);
                if (quota is not null) s.AddSingleton(quota);
                if (projects is not null) s.AddSingleton(projects);
                if (budget is not null) s.AddSingleton(budget);
            });
        });
}
