using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The readiness probe an uptime monitor watches. The point of it is the failing case: liveness already
/// answers "ok" unconditionally, so a control-plane that has lost Postgres serves errors to every request
/// while still looking healthy, and a monitor agrees with the outage instead of catching it.
///
/// So the test that matters is the one where a store is unreachable. A probe that only ever returns 200
/// in tests is indistinguishable from one that always returns 200.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReadinessProbeTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Reports_ready_when_the_stores_answer()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        Assert.True(await StoreReadiness.PostgresAsync(pg.ConnectionString));
    }

    [Fact]
    public async Task Reports_not_ready_when_postgres_is_unreachable()
    {
        // A port nothing listens on: the same answer an operator gets when the database is down, gone or
        // firewalled off. This is the case the whole endpoint exists for.
        const string unreachable =
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2;Command Timeout=2";

        Assert.False(await StoreReadiness.PostgresAsync(unreachable));
    }

    [Fact]
    public async Task Reports_not_ready_when_credentials_are_wrong()
    {
        // Reachable but unusable is still not ready. A probe that only checks the socket would pass here
        // and the service would still fail every request.
        var badPassword = pg.ConnectionString.Replace("Password=", "Password=wrong-");

        Assert.False(await StoreReadiness.PostgresAsync(badPassword));
    }

    [Fact]
    public async Task Reports_not_ready_when_clickhouse_is_unreachable()
    {
        using var http = new HttpClient();

        Assert.False(await StoreReadiness.ClickHouseAsync(http, "http://127.0.0.1:1", "u", "p"));
    }

    [Fact]
    public async Task Answers_within_its_own_deadline_rather_than_hanging()
    {
        // A probe that hangs is worse than one that fails: a monitor cannot tell it apart from its own
        // network trouble, so an outage reads as a monitoring glitch. A blackholed address is the case
        // that would hang without the timeout.
        var started = DateTimeOffset.UtcNow;

        await StoreReadiness.PostgresAsync(
            "Host=10.255.255.1;Port=5432;Username=x;Password=x;Database=x;Timeout=2");

        Assert.True(
            DateTimeOffset.UtcNow - started < StoreReadiness.Timeout + TimeSpan.FromSeconds(3),
            "the probe should give up on its own rather than wait for the network to");
    }

    [Fact]
    public async Task The_endpoint_answers_503_when_a_store_is_down()
    {
        // End to end through the real app, since the status code is what a monitor reads. Without a
        // ClickHouse fixture the app points at an unresolvable host, so Postgres is live and ClickHouse is
        // not: which also proves one failing store is enough to report not ready.
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = ControlPlaneApp.Create(pg.ConnectionString);

        var resp = await app.CreateClient().GetAsync("/api/readyz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // The token a monitor matches on must be absent while anything is failing. Asserting the whole
        // string is deliberate: "ready" is a substring of nothing else here, but a monitor searching raw
        // text would match it inside a longer word, so the value has to be exactly this.
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        // Named, so an operator reading the body knows which store to look at.
        Assert.True(body.GetProperty("postgres").GetBoolean());
        Assert.False(body.GetProperty("clickhouse").GetBoolean());
    }
}
