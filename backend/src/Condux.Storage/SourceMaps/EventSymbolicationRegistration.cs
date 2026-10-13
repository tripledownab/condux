using Condux.Core.Http;
using Condux.Storage.DebugFiles;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Condux.Storage.SourceMaps;

/// <summary>
/// DI registration for read-time symbolication, shared by the control-plane, which shows events, and the
/// consumer, whose auto-fix builds a fix context from one. Both passes read their files from object storage,
/// so each registers only when CONDUX_S3_* are set, and the native pass also needs CONDUX_SYMBOLIZER_URL.
/// The service around them always registers, with optional passes, so it does nothing when they are off and
/// no caller branches. It registers the repositories the passes read too, unless the caller already has,
/// so a service cannot turn a pass on and leave it unable to start.
/// </summary>
public static class EventSymbolicationRegistration
{
    // Generous for one file handed over, which is the slowest call. Every read runs under a budget of its
    // own (EventSymbolication), so this bounds a call no budget is watching.
    private static readonly TimeSpan SymbolizerTimeout = TimeSpan.FromSeconds(30);

    // One image's answer, held until its event is rewritten. A real one for a full batch of addresses is a
    // small fraction of this; a larger one fails its image rather than taking the memory.
    private const long MaxAnswerBytes = 8L * 1024 * 1024;

    public static IServiceCollection AddEventSymbolication(
        this IServiceCollection services, IConfiguration cfg, string postgres)
    {
        services.AddObjectStore(cfg);
        var symbolizer = ToSymbolizerUrl(cfg);
        if (ObjectStoreConfig.FromEnv(cfg).Enabled)
        {
            services.TryAddSingleton(new SourceMapArtifactRepository(postgres));
            services.AddSingleton<FrameSymbolicator>();
            if (symbolizer is not null)
            {
                services.TryAddSingleton(new DebugFileRepository(postgres));
                services.AddHttpClient(SymbolizerClient.HttpClientName, c =>
                {
                    c.BaseAddress = symbolizer;
                    c.Timeout = SymbolizerTimeout;
                    c.MaxResponseContentBufferSize = MaxAnswerBytes;
                });
                services.AddSingleton<DebugFileReadGate>();
                services.AddSingleton<SymbolizerCaches>();
                // Its meter factory comes with AddHttpClient above.
                services.AddSingleton<SymbolizerMetrics>();
                services.AddSingleton<NativeSymbolicator>();
            }
        }

        services.AddSingleton(sp => new EventSymbolication(
            sp.GetService<FrameSymbolicator>(), sp.GetService<NativeSymbolicator>(),
            sp.GetRequiredService<ILogger<EventSymbolication>>()));
        return services;
    }

    // Unset turns the native pass off. A value that is set must be a URL, checked whether or not storage is
    // on, or a typo would wait unseen for the day it is.
    private static Uri? ToSymbolizerUrl(IConfiguration cfg) =>
        cfg["CONDUX_SYMBOLIZER_URL"] switch
        {
            null or "" => null,
            var value when HttpUrls.IsAbsoluteHttp(value) => new Uri(value.EndsWith('/') ? value : value + "/"),
            _ => throw new InvalidOperationException("CONDUX_SYMBOLIZER_URL must be an absolute http(s) URL"),
        };
}
