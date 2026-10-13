using Condux.Core.SourceMaps;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Uploads;

/// <summary>
/// Wires what a project may keep of its uploads: eviction past the tier's quota, called by the upload
/// endpoints, and, when object storage is on, the queue of objects to delete, the sweep that finds objects
/// no row names, and the worker that runs both.
/// </summary>
public static class UploadStorageRegistration
{
    public static IServiceCollection AddUploadStorage(this IServiceCollection services, IConfiguration cfg, string postgres)
    {
        services.AddSingleton(new UploadStorage(postgres));
        if (ObjectStoreConfig.FromEnv(cfg).Enabled)
        {
            services.AddSingleton(sp => new ObjectDeletionQueue(postgres, sp.GetRequiredService<IObjectStoreCleanup>()));
            services.AddSingleton(sp => new OrphanObjectSweep(postgres, sp.GetRequiredService<IObjectStoreCleanup>()));
            services.AddHostedService<UploadStorageWorker>();
        }
        return services;
    }
}
