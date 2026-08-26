using System.Text.Json.Serialization;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Endpoints;
using Condux.ControlPlane.Mcp;
using Condux.ControlPlane.Setup;
using Condux.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

// OpenTelemetry (traces + metrics over OTLP; opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.AddConduxTelemetry("condux-controlplane",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());

// Reject numbers sent as JSON strings (the JsonSerializerDefaults.Web default is AllowReadingFromString).
// .NET 10's OpenAPI generator reflects that default by annotating every integer/number schema with a string
// pattern, which orval then can't reconcile with the integer type and degrades to `unknown`. Strict keeps the
// schema a clean `type: integer`, matches the contract we already advertise, and never accepted stringified
// numbers from the dashboard anyway.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

// OpenAPI document (built into ASP.NET Core). Served at /openapi/v1.json; it's the source of truth for
// the dashboard's generated TS client (orval → TanStack Query hooks) and the published Postman collection.
// Pin to 3.0: .NET 10 defaults new documents to OpenAPI 3.1 (different nullable + integer schema shapes),
// which would silently drift the committed contract and everything regenerated from it. Adopting 3.1 is a
// deliberate follow-up, not a side effect of the framework bump.
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Condux Control Plane API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

builder.Services.AddControlPlaneServices(builder.Configuration);
builder.Services.AddControlPlaneAuth();
builder.Services.AddControlPlaneCors(builder.Configuration);

// Condux on Condux (#75): opt-in self-error reporting via the Condux .NET SDK (CONDUX_SELF_DSN).
builder.AddConduxSelfReporting();

var app = builder.Build();

// Report the control-plane's own unhandled request exceptions to a Condux project when self-reporting is
// enabled. First in the pipeline, so it wraps every endpoint; fire-and-forget (the SDK never throws).
if (app.Services.GetService<Condux.Sdk.ConduxClient>() is { } conduxSelf)
{
    app.Use(async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            // A cancellation from a client disconnect or graceful shutdown is expected noise, not a fault;
            // still report a genuine OperationCanceledException thrown for any other reason.
            if (!ConduxSelfReport.IsExpectedCancellation(exception, context.RequestAborted.IsCancellationRequested))
            {
                _ = conduxSelf.CaptureExceptionAsync(exception);
            }
            throw;
        }
    });
}

// CORS runs before auth so credentialed preflight/requests from a split-origin dashboard are handled
// (no-op when CONDUX_CORS_ORIGINS is unset, i.e. same-origin production).
app.UseCors(ServiceCollectionExtensions.CorsPolicy);
app.UseAuthentication();
// Read-only impersonation (ADR-0027): resolve the view-as cookie into request scope and block writes
// while active. After authentication (needs User) and before authorization (the scope grant reads it).
app.UseImpersonation();
app.UseAuthorization();

// Expose the OpenAPI JSON (no UI — the client generator and API consumers read this).
app.MapOpenApi();

app.MapOperationalEndpoints(builder.Configuration);
app.MapIssueEndpoints();
app.MapIssueNoteEndpoints();
app.MapProvisioningEndpoints();
app.MapRepoEndpoints();
app.MapReleaseTokenEndpoints();
app.MapMcpTokenEndpoints();
app.MapRunnerEndpoints();
app.MapMcpEndpoints();
app.MapSourceMapEndpoints();
app.MapFixEndpoints();
app.MapCveFixEndpoints();
app.MapFixCatalogEndpoints();
app.MapLlmConfigEndpoints();
app.MapAlertEndpoints();
app.MapNotificationEndpoints();
app.MapWeeklySummaryEndpoints();
app.MapMemberEndpoints();
app.MapInviteEndpoints();
app.MapAuthEndpoints();
app.MapMfaEndpoints();
app.MapOnboardingEndpoints();
app.MapOAuthEndpoints();
app.MapSsoEndpoints();
app.MapSamlSsoEndpoints();
app.MapSsoConfigEndpoints();
app.MapBillingEndpoints();
app.MapAdminEndpoints();
app.MapAdminOrgEndpoints();
app.MapAdminBillingEndpoints();
app.MapAdminSpendEndpoints();
app.MapImpersonationEndpoints();
app.MapGithubEndpoints();
app.MapGithubConnectEndpoints();

// Dev-only dogfood lever (#75): GET /api/boom throws so the control-plane self-reports its own error to
// CONDUX_SELF_DSN (parity with the dashboard's boom). Never mapped in production.
if (app.Environment.IsDevelopment())
{
    app.MapDevEndpoints();
}

app.Run();

// Exposed so integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program { }
