# Condux SDK for .NET

Report errors from a .NET app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never throws** — a failed send returns a `SendResult`, it does not crash the host app.

## Usage

```csharp
using Condux.Sdk;

var condux = new ConduxClient(new ConduxOptions
{
    Dsn = "https://<key>@ingest.condux.ai/<projectId>",
    Environment = "production",
    Release = "1.4.2",
});

try
{
    DoWork();
}
catch (Exception error)
{
    await condux.CaptureExceptionAsync(error);
    throw;
}

// or a bare message
await condux.CaptureMessageAsync("cache miss storm", Level.Warning);
```

Hold one `ConduxClient` for the app's lifetime. Deploy your **portable PDBs** alongside the app for file
names and line numbers in stack frames (without them, frames keep the method + declaring type).

A DSN that is not a whole DSN (no key, no project id) throws at construction, so a broken configuration
stops the app at startup instead of quietly sending every event to an address that can only reject it.
Capture itself never throws.

## Verify your setup

Silence is what a broken error monitor and a healthy app look like from the outside, so prove the pipeline
once:

```bash
dotnet tool install -g Condux.Sdk.TestEvent
CONDUX_DSN="https://<key>@ingest.condux.ai/<projectId>" condux-test-event
```

It sends one info-level message through the real client and transport. Exit code 0 means delivered (the
message shows up as an info-level issue), 1 means delivery failed and prints why, 2 means the DSN was
missing or malformed — so a CI step can gate on it.

## Enrichment

Attach the ambient facts triage always needs. Every subsequent event carries them, so nothing has to be
threaded through capture calls:

```csharp
ConduxScope.SetUser(new ConduxUser { Id = "1042", Email = "dev@example.com" }); // null clears (sign-out)
ConduxScope.SetTag("plan", "team");                                            // null value removes it
ConduxScope.SetContext("job", new Dictionary<string, object?> { ["queue"] = "billing" }); // null removes
ConduxScope.AddBreadcrumb("charge.started", category: "billing", level: Level.Info);
```

The trail keeps the most recent `ConduxScope.MaxBreadcrumbs` (30) entries, dropping the oldest.
`ConduxScope.Clear()` resets everything. The scope is process wide and safe to use from any thread, so a
framework integration and your own code share one view of it. The relay scrubs all of it at ingest and
derives the pseudonymous users-affected count from the user fields.

## Develop

```bash
dotnet build Condux.Sdk.sln -c Release
dotnet test Condux.Sdk.sln -c Release
dotnet format Condux.Sdk.sln --verify-no-changes
```

Zero external dependencies (BCL only). Transport, sleep, and clock are injectable, so tests exercise the
retry/backoff with no real network or timers (`Condux.Sdk.Tests`). The solution holds the SDK, the
ASP.NET Core integration, the `condux-test-event` tool, and their tests.
