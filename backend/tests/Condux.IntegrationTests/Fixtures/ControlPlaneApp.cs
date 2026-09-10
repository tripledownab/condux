using System.Collections.Concurrent;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Builds a control-plane <see cref="WebApplicationFactory{Program}"/> with all required config set
/// from the test's stores. The app reads config from the environment with no hardcoded fallbacks, so
/// every host must supply it — tests that don't touch the event store pass no ClickHouse fixture and
/// get harmless placeholders (the reader only connects when an issue-detail query actually runs).
///
/// No Kafka broker runs in these tests, so the Kafka publishers are swapped for no-ops and the host log
/// level is raised to Warning — otherwise every run drowns in librdkafka "Connection refused" errors and
/// per-request Info logs. Tests that assert on a published job override the publisher again with their
/// own capturing fake (a later registration wins; the real producer is a lazy factory, so it is never
/// built once overridden).
/// </summary>
public static class ControlPlaneApp
{
    /// <summary>
    /// Every host this class has built, keyed by the database it was built against. Almost no caller
    /// disposes what <see cref="Create"/> returns, and a live host holds a Postgres connection open for
    /// as long as it runs (Realtime.ProjectEventListener LISTENs on one). That cost nothing while each
    /// test class had a container of its own to take the connections away with it, but the suite now
    /// shares one server, so they accumulated: a run peaked at 288 backends against a Postgres whose
    /// default limit is 100.
    ///
    /// Keyed by connection string rather than kept in one list because each class has its own database,
    /// so a class disposes exactly its own hosts and never another's. That is what makes this safe while
    /// test collections run in parallel.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ConcurrentBag<WebApplicationFactory<Program>>>
        Hosts = new();

    /// <summary>
    /// Disposes every host built against this database. Called by <see cref="PostgresFixture"/> at class
    /// teardown, by which point the class's tests have all finished. Disposing twice is harmless, so a
    /// test that already wrapped its own host in a <c>using</c> needs no exception. A host built against
    /// a connection string no fixture owns (the CORS tests pass a dummy) is simply never reached here.
    /// </summary>
    public static void DisposeHostsFor(string postgres)
    {
        if (!Hosts.TryRemove(postgres, out var hosts))
        {
            return;
        }

        // One after another, which is measured to be the cheaper of the two. Disposing them
        // concurrently through DisposeAsync looked like the obvious win and cost 281s more on the same
        // suite, burning CPU rather than waiting: 564s against 283s.
        foreach (var host in hosts)
        {
            host.Dispose();
        }
    }

    /// <param name="appBaseUrl">
    /// The absolute dashboard URL this host is deployed at, when a test needs one (SAML entity ids, the
    /// GitHub connect return, reset links). It sets the client's base address to the SAME scheme, because
    /// the two cannot be allowed to disagree: an https deployment issues Secure cookies, and a client
    /// talking http to it silently drops every one of them, so the test would fail on a cookie jar rather
    /// than on the thing it asserts. Pass it here rather than through <paramref name="configure"/>, which
    /// sets the config alone.
    /// </param>
    public static WebApplicationFactory<Program> Create(
        string postgres, ClickHouseFixture? clickHouse = null, string? platformAdminEmails = null,
        string? impersonationSigningKey = null, Action<IWebHostBuilder>? configure = null,
        string? appBaseUrl = null)
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (appBaseUrl is not null)
            {
                b.UseSetting("CONDUX_APP_BASE_URL", appBaseUrl);
            }
            b.UseSetting("CONDUX_POSTGRES", postgres);
            b.UseSetting("CONDUX_CLICKHOUSE_URL", clickHouse?.BaseUrl ?? "http://clickhouse.invalid:8123");
            b.UseSetting("CONDUX_CLICKHOUSE_USER", clickHouse?.ChUser ?? "test");
            b.UseSetting("CONDUX_CLICKHOUSE_PASSWORD", clickHouse?.ChPassword ?? "test");
            if (platformAdminEmails is not null)
            {
                b.UseSetting("CONDUX_PLATFORM_ADMIN_EMAILS", platformAdminEmails);
            }
            if (impersonationSigningKey is not null)
            {
                b.UseSetting("CONDUX_IMPERSONATION_SIGNING_KEY", impersonationSigningKey);
            }

            // A fixed AES-256 key so SecretBox is available, which MFA needs to seal the TOTP secret.
            // Set unconditionally rather than per-test: every real deployment should have this, and a
            // suite where it is absent by default would quietly test the not-configured path instead of
            // the feature. Fixed rather than random so a failure is reproducible; it protects nothing
            // outside an ephemeral container.
            b.UseSetting("CONDUX_SECRET_KEY", Convert.ToBase64String(Enumerable.Range(0, 32)
                .Select(i => (byte)i).ToArray()));

            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IFixRequestPublisher>(new NoopFixRequestPublisher());
                services.AddSingleton<ICveFixPublisher>(new NoopCveFixPublisher());
            });
            b.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

            // Per-test extras: extra config (e.g. Stripe env) and service overrides (e.g. a stub Stripe
            // HttpMessageHandler) so the billing tests never touch the real Stripe API. Runs last so a test
            // can override even the no-op publishers above.
            configure?.Invoke(b);
        });

        if (appBaseUrl is not null)
        {
            app.ClientOptions.BaseAddress = new Uri(new Uri(appBaseUrl), "/");
        }

        Hosts.GetOrAdd(postgres, _ => []).Add(app);
        return app;
    }

    private sealed class NoopFixRequestPublisher : IFixRequestPublisher
    {
        public Task PublishAsync(FixJob job, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopCveFixPublisher : ICveFixPublisher
    {
        public Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
