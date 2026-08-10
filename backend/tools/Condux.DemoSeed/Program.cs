using Condux.DemoSeed;
using Condux.Storage.ClickHouse;

// Dev tool: populate a RUNNING dev stack's databases with the demo account + a realistic checkout dataset
// (issues, hourly stats, sampled events, Conductor fixes) so the dashboard, weekly summary and Fixes have
// something to show. Reads datastore config from env (no hardcoded creds); run it via scripts/seed-demo.sh,
// which sources deploy/.env and points it at the localhost stores:
//
//   pnpm seed:demo            (or: bash scripts/seed-demo.sh)
//
// Targets a FRESH database — it bails if demo@condux.dev already exists.

static string Require(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"Missing env var {name}. Run via scripts/seed-demo.sh (it sources deploy/.env), or set "
            + "CONDUX_POSTGRES + CONDUX_CLICKHOUSE_URL/USER/PASSWORD to the localhost dev stores.");

var postgres = Require("CONDUX_POSTGRES");
using var clickHouse = new HttpClient();
ClickHouseRegistration.Configure(
    clickHouse, Require("CONDUX_CLICKHOUSE_URL"), Require("CONDUX_CLICKHOUSE_USER"), Require("CONDUX_CLICKHOUSE_PASSWORD"));

Console.WriteLine("Seeding demo data...");
await new DemoSeeder(postgres, clickHouse).RunAsync();
