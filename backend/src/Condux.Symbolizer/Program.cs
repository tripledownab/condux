// The symbolizer's entry point: resolve native addresses against cached debug files with llvm-symbolizer.
// It holds no credential and opens no database, broker or store, because it parses files a stranger chose
// (ADR-0049). The binary is checked once at startup, so a missing install fails the container instead of
// every request.
using Condux.Symbolizer;
using Condux.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

var options = SymbolizerOptions.FromConfiguration(builder.Configuration);
await StartupCheck.RequireSymbolizerAsync(options);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DebugFileCache>();
builder.Services.AddSingleton<LlvmSymbolizerRunner>();
builder.Services.AddSingleton<FileUploadGate>();
builder.Services.AddSingleton<ResolveGate>();

var app = builder.Build();
app.Services.GetRequiredService<DebugFileCache>().RemoveTemporaries();
app.MapSymbolizerEndpoints();
app.Run();

/// <summary>The entry point type, exposed for the integration tests' application factory.</summary>
public partial class Program;
