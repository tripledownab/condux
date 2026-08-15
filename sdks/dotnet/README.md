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
`ConduxScope.Clear()` resets everything. The relay scrubs all of it at ingest and derives the
pseudonymous users-affected count from the user fields.

### In a server, scope one request at a time

Those calls are **process wide** by default, which is right for facts about the deployment and wrong for
facts about one request: ASP.NET Core serves requests concurrently, so a bare `SetUser` in a controller
can attach that user to a different request's error. That is worse than reporting no user, because it is
confidently wrong.

`ConduxScope.BeginRequest()` isolates it. Anything set inside belongs to that request alone, layered
over the process-wide values:

```csharp
using (ConduxScope.BeginRequest())
{
    ConduxScope.SetUser(new ConduxUser { Id = userId }); // this request only
    await Handle(request);
}
```

**Register it inside your exception handler**, not before it:

```csharp
app.UseExceptionHandler("/error");     // first, so it is outermost
app.UseConduxExceptionReporting();     // inside it, so this sees the exception
```

Middleware sees an exception only as it unwinds back out, so whichever is registered first is outermost
and gets it last. A configured `UseExceptionHandler` handles the exception and returns a response rather
than rethrowing, so anything registered outside it never sees the exception and reports nothing at all.
Measured against a real app: inside, one event with the right error; outside, zero events and no sign
anything is wrong. The ASP.NET Core templates put `UseExceptionHandler` first, so adding this line after
it is correct.

**`UseConduxExceptionReporting()` also opens the request scope**, so a `SetUser` in a controller or a
filter is already isolated. Call it directly around a hosted service or a queue consumer, which has the same
problem of many in flight at once. It uses `AsyncLocal`, so it follows the request across every `await`
rather than being lost at the first one like a `ThreadStatic` would.

Detail belonging to a single event can skip the scope entirely:

```csharp
await client.CaptureExceptionAsync(error, handled: true, new CaptureContext
{
    Request = new ConduxRequest { Url = "/api/sync", Method = "POST" },
    Tags = new Dictionary<string, string> { ["job"] = "nightly" },
});
```

## Develop

```bash
dotnet build Condux.Sdk.sln -c Release
dotnet test Condux.Sdk.sln -c Release
dotnet format Condux.Sdk.sln --verify-no-changes
```

Zero external dependencies (BCL only). Transport, sleep, and clock are injectable, so tests exercise the
retry/backoff with no real network or timers (`Condux.Sdk.Tests`). The solution holds the SDK, the
ASP.NET Core integration, the `condux-test-event` tool, and their tests.
