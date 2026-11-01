using Condux.ControlPlane.Tickets;
using Condux.Core.Secrets;
using Condux.Core.Tickets;
using Condux.Linear;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Registers tracker tickets (ADR-0053): the stores always, since an unconfigured deployment still answers
/// "not connected" from them, and the Linear client, its sink and the one worker that calls it only when
/// Linear is configured. Called after the secrets block, because the sink seals and opens the grant.
/// </summary>
internal static class TicketRegistration
{
    // Short enough that a worker can make its worst case of five calls (two refreshes, two creates and the
    // lookup after a refusal) inside the claim timeout, so a slow Linear is answered as uncertain by the
    // worker, not by the sweep.
    private static readonly TimeSpan LinearTimeout = TimeSpan.FromSeconds(20);

    public static IServiceCollection AddTicketServices(
        this IServiceCollection services, IConfiguration cfg, string postgres, bool secretsEnabled)
    {
        var linear = LinearConfig.FromEnv(cfg, secretsEnabled);
        services.AddSingleton(linear);
        var connections = new TrackerConnectionRepository(postgres);
        services.AddSingleton(connections);
        services.AddSingleton<ITrackerConnectionStore>(connections);
        services.AddSingleton(new ProjectTrackerMappingRepository(postgres));
        services.AddSingleton(new IssueTicketRepository(postgres));
        services.AddSingleton<TicketWorkerSignal>();
        services.AddSingleton<Endpoints.IssueTicketEndpoints.Lookups>();

        if (linear.Enabled)
        {
            // One client for the process, like the GitHub token minter: a typed client per request would
            // buy nothing for two hosts called a few times a minute.
            var http = new HttpClient { Timeout = LinearTimeout };
            Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;
            services.AddSingleton(new LinearOAuth(http, linear.Options!, clock));
            services.AddSingleton(new LinearApi(http, linear.Options!));
            services.AddSingleton(sp => new LinearTicketSink(
                connections, sp.GetRequiredService<SecretBox>(), sp.GetRequiredService<LinearOAuth>(),
                sp.GetRequiredService<LinearApi>(), clock));
            services.AddSingleton<ITicketSink>(sp => sp.GetRequiredService<LinearTicketSink>());
            services.AddHostedService<TicketWorker>();
        }
        return services;
    }
}
