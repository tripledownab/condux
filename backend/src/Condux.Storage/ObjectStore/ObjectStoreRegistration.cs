using Condux.Core.SourceMaps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.Storage.ObjectStore;

/// <summary>
/// DI registration for the object store (ADR-0028). Always registers the <see cref="ObjectStoreConfig"/>
/// so endpoints can 404 when the feature is off; registers the <see cref="IObjectStore"/> S3 client only
/// when configured, mirroring the GitHub/Stripe opt-in wiring.
/// </summary>
public static class ObjectStoreRegistration
{
    public static IServiceCollection AddObjectStore(this IServiceCollection services, IConfiguration cfg)
    {
        var config = ObjectStoreConfig.FromEnv(cfg);
        services.AddSingleton(config);
        if (config.Enabled)
        {
            // A factory (not an eager instance) so the S3 client is built only when first resolved, which
            // lets a test override IObjectStore with a fake without a real S3 client ever being constructed.
            services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(config));
        }

        return services;
    }
}
