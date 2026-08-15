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
app.UseExceptionHandler("/error");     // your handler first, so it is outermost
app.UseConduxExceptionReporting();     // Condux inside it, so it sees the exception

app.MapGet("/", () => throw new InvalidOperationException("boom"));
app.Run();
```

**The order of those two lines decides whether anything is reported.** Middleware sees an exception only
as it unwinds back out, and a configured `UseExceptionHandler` returns a response rather than rethrowing:
registered outside it, this middleware never sees the exception and reports zero events, silently.
Measured against a real app both ways. The ASP.NET Core templates put `UseExceptionHandler` first, so
adding the Condux line after it is correct. (An app with no exception handler at all also works, since
the exception then escapes to whatever middleware is outermost.)

`UseConduxExceptionReporting()` resolves the `ConduxClient` from DI as the pipeline is built, so a missing
registration throws once at startup naming the fix, rather than breaking every request the app serves.
Capture manually anywhere by injecting the client and calling `CaptureExceptionAsync` /
`CaptureMessageAsync`.

## Develop

```bash
dotnet test Condux.Sdk.sln
```

Depends only on `Condux.Sdk` + the ASP.NET Core shared framework. The middleware is unit-tested with a
`DefaultHttpContext` and a recording transport (no real server).
