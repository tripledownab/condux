using System.Net;
using System.Text.Json;
using Xunit;

// The scope is process wide, so two test classes enriching and asserting at the same time would read each
// other's state. Running the assembly's classes one at a time is what keeps these assertions about the
// scope rather than about the scheduler.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Condux.Sdk.Tests;

// Proves ambient enrichment reaches the relay in the Sentry wire shape, and that an event captured without
// it is byte-for-byte the event this SDK always sent. Mirrors the Go scope_test.go and the JS scope.test.ts.
public class ConduxScopeTests : IDisposable
{
    private readonly ScriptedTransport transport = new(ScriptedTransport.Status(HttpStatusCode.OK));

    // Every test starts and ends on an empty scope: leaking would enrich (and so break) the wire
    // assertions in the rest of the suite.
    public ConduxScopeTests() => ConduxScope.Clear();

    public void Dispose()
    {
        ConduxScope.Clear();
        GC.SuppressFinalize(this);
    }

    private async Task<JsonElement> Capture()
    {
        var client = new ConduxClient(new ConduxOptions
        {
            Dsn = "https://testkey@ingest.example.test/proj-uuid",
            Transport = transport,
            Sleep = (_, _) => Task.CompletedTask,
        });
        await client.CaptureMessageAsync("scope carrier", Level.Info);
        return JsonDocument.Parse(transport.Bodies[^1]).RootElement;
    }

    [Fact]
    public async Task An_unenriched_event_carries_none_of_the_scope_keys()
    {
        var body = await Capture();

        Assert.False(body.TryGetProperty("user", out _));
        Assert.False(body.TryGetProperty("tags", out _));
        Assert.False(body.TryGetProperty("contexts", out _));
        Assert.False(body.TryGetProperty("breadcrumbs", out _));
    }

    [Fact]
    public async Task An_enriched_event_carries_the_sentry_wire_shape()
    {
        ConduxScope.SetUser(new ConduxUser { Id = "1042", Email = "dev@example.test" });
        ConduxScope.SetTag("plan", "team");
        ConduxScope.SetContext("job", new Dictionary<string, object?> { ["queue"] = "billing", ["attempt"] = 2 });
        ConduxScope.AddBreadcrumb(
            "charge.started", category: "billing", level: Level.Info, type: "default",
            data: new Dictionary<string, object?> { ["amount"] = 4200 });

        var body = await Capture();

        var user = body.GetProperty("user");
        Assert.Equal("1042", user.GetProperty("id").GetString());
        Assert.Equal("dev@example.test", user.GetProperty("email").GetString());
        Assert.False(user.TryGetProperty("username", out _)); // unset user fields stay off the wire
        Assert.Equal("team", body.GetProperty("tags").GetProperty("plan").GetString());

        var job = body.GetProperty("contexts").GetProperty("job");
        Assert.Equal("billing", job.GetProperty("queue").GetString());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());

        // Breadcrumbs ride the Sentry {"values": [...]} envelope, not a bare array.
        var crumbs = body.GetProperty("breadcrumbs").GetProperty("values");
        Assert.Equal(1, crumbs.GetArrayLength());
        var crumb = crumbs[0];
        Assert.Equal("charge.started", crumb.GetProperty("message").GetString());
        Assert.Equal("billing", crumb.GetProperty("category").GetString());
        Assert.Equal("info", crumb.GetProperty("level").GetString());
        Assert.Equal("default", crumb.GetProperty("type").GetString());
        Assert.Equal(4200, crumb.GetProperty("data").GetProperty("amount").GetInt32());
        Assert.True(crumb.GetProperty("timestamp").GetDouble() > 0); // epoch seconds, stamped for us
    }

    [Fact]
    public async Task The_scope_rides_an_exception_capture_too()
    {
        ConduxScope.SetTag("plan", "business");
        var client = new ConduxClient(new ConduxOptions
        {
            Dsn = "https://testkey@ingest.example.test/proj-uuid",
            Transport = transport,
        });

        await client.CaptureExceptionAsync(new InvalidOperationException("boom"));

        var body = JsonDocument.Parse(transport.Bodies[^1]).RootElement;
        Assert.Equal("business", body.GetProperty("tags").GetProperty("plan").GetString());
    }

    [Fact]
    public async Task Nulls_clear_the_scope_entries()
    {
        ConduxScope.SetUser(new ConduxUser { Id = "1042" });
        ConduxScope.SetTag("plan", "team");
        ConduxScope.SetContext("job", new Dictionary<string, object?> { ["queue"] = "billing" });

        ConduxScope.SetUser(null);
        ConduxScope.SetTag("plan", null);
        ConduxScope.SetContext("job", null);
        var body = await Capture();

        Assert.False(body.TryGetProperty("user", out _));
        Assert.False(body.TryGetProperty("tags", out _));
        Assert.False(body.TryGetProperty("contexts", out _));
    }

    [Fact]
    public async Task The_breadcrumb_trail_is_capped_dropping_the_oldest()
    {
        for (var step = 0; step < 35; step++)
        {
            ConduxScope.AddBreadcrumb($"step {step}", data: new Dictionary<string, object?> { ["step"] = step });
        }

        var crumbs = (await Capture()).GetProperty("breadcrumbs").GetProperty("values");

        Assert.Equal(ConduxScope.MaxBreadcrumbs, crumbs.GetArrayLength());
        // Newest last, oldest dropped: the trail spans step 5 to step 34.
        Assert.Equal(5, crumbs[0].GetProperty("data").GetProperty("step").GetInt32());
        Assert.Equal(34, crumbs[crumbs.GetArrayLength() - 1].GetProperty("data").GetProperty("step").GetInt32());
    }

    [Fact]
    public async Task A_context_snapshot_is_taken_so_later_edits_do_not_rewrite_history()
    {
        var context = new Dictionary<string, object?> { ["queue"] = "billing" };
        ConduxScope.SetContext("job", context);

        context["queue"] = "edited after the fact";
        var body = await Capture();

        Assert.Equal("billing", body.GetProperty("contexts").GetProperty("job").GetProperty("queue").GetString());
    }

    [Fact]
    public async Task Capture_does_not_throw_on_scope_data_that_cannot_be_serialized()
    {
        // A cycle is the honest worst case for a value the app owns: the serializer refuses it. Capture has
        // to come back with a failed result, because throwing here would crash the app in the path where it
        // is already handling an error.
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;
        ConduxScope.SetContext("cyclic", cyclic);
        var client = new ConduxClient(new ConduxOptions
        {
            Dsn = "https://testkey@ingest.example.test/proj-uuid",
            Transport = transport,
        });

        var result = await client.CaptureMessageAsync("never leaves the process");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Empty(transport.Bodies);
    }
}
