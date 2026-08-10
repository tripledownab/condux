# Condux.Sdk.AspNetCore

ASP.NET Core integration for the [`Condux.Sdk`](../Condux.Sdk) .NET SDK. A middleware that reports an
**unhandled request exception** to a Condux relay (as unhandled) and re-throws, so the app's own error
handling still runs.

## Usage

```csharp
using Condux.Sdk;
using Condux.Sdk.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new ConduxClient(new ConduxOptions
{
    Dsn = "https://<key>@ingest.condux.ai/<projectId>",
    Environment = builder.Environment.EnvironmentName,
    Release = "api@1.4.2",
}));

var app = builder.Build();
app.UseConduxExceptionReporting(); // register early, before UseRouting

app.MapGet("/", () => throw new InvalidOperationException("boom"));
app.Run();
```

The middleware resolves the `ConduxClient` from DI. Capture manually anywhere by injecting the client and
calling `CaptureExceptionAsync` / `CaptureMessageAsync`.

## Develop

```bash
dotnet test Condux.Sdk.sln
```

Depends only on `Condux.Sdk` + the ASP.NET Core shared framework. The middleware is unit-tested with a
`DefaultHttpContext` and a recording transport (no real server).
