using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.Core.Quotas;
using Condux.Messaging;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Registers what the control-plane needs of the Conductor: the issue fix and CVE bump run stores, their
/// verification and catalog reads, the allowance and spend the requests reserve against, and the
/// publishers that queue a job for the conductor's workers.
/// </summary>
internal static class ConductorRegistration
{
    public static IServiceCollection AddConductorServices(
        this IServiceCollection services, IConfiguration cfg, string postgres)
    {
        // Read fix suggestions, and publish fix requests to the topic the conductor's worker drains.
        // Registered under the concrete type too: the weekly digest composer reads its org-scoped fix
        // aggregate (ADR-0031); IFixStore resolves to the same instance.
        services.AddSingleton(new PostgresFixStore(postgres));
        services.AddSingleton<IFixStore>(sp => sp.GetRequiredService<PostgresFixStore>());
        // Fix verification (ADR-0019): the webhook marks a merged Conductor PR as watching here.
        services.AddSingleton(new PostgresFixVerification(postgres));
        services.AddSingleton(new PostgresCveFixVerification(postgres));
        // The Fixes section read/write model (#118): project-wide list, detail + audit, view + archive.
        services.AddSingleton(new PostgresFixCatalog(postgres));

        // The per-org AI-fix allowance counter RequestFix reserves against (#100, ADR-0017).
        services.AddSingleton<IAiFixQuota>(new PostgresAiFixQuota(postgres));
        // The per-org month-to-date Conductor spend, for the cost cap + the usage meter (#120).
        services.AddSingleton<IAiFixSpend>(new PostgresAiFixSpend(postgres));
        // Both publishers are factories, never instances: the container disposes only what it built, and
        // disposing flushes a job still queued at shutdown. Being lazy also keeps a test's fake from ever
        // starting a real producer. A reserved run rides on each queued job, so dropping one costs a run.
        // Kafka bootstrap is optional (defaults to the compose broker); the producer connects lazily.
        var kafka = cfg["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092";
        services.AddSingleton<IFixRequestPublisher>(
            sp => new KafkaFixRequestPublisher(kafka, sp.GetRequiredService<ILogger<KafkaFixRequestPublisher>>()));

        // Supply-chain CVE-bump runs (#117 slice 2): read the per-repo runs, and publish a bump request
        // to the topic the Conductor's CVE worker drains. A separate domain from the issue fixes above.
        // Also under the concrete type, for withdrawing a run nothing holds, which only the dashboard
        // asks for.
        services.AddSingleton(new PostgresCveFixStore(postgres));
        services.AddSingleton<ICveFixStore>(sp => sp.GetRequiredService<PostgresCveFixStore>());
        services.AddSingleton<ICveFixPublisher>(
            sp => new KafkaCveFixPublisher(kafka, sp.GetRequiredService<ILogger<KafkaCveFixPublisher>>()));

        return services;
    }
}
