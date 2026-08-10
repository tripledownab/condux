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

## Develop

```bash
dotnet build Condux.Sdk.sln -c Release
dotnet test Condux.Sdk.sln -c Release
dotnet format Condux.Sdk.sln --verify-no-changes
```

Zero external dependencies (BCL only). Transport, sleep, and clock are injectable, so tests exercise the
retry/backoff with no real network or timers (`Condux.Sdk.Tests`).
