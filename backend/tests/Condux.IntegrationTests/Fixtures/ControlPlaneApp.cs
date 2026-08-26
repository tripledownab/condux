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
    public static WebApplicationFactory<Program> Create(
        string postgres, ClickHouseFixture? clickHouse = null, string? platformAdminEmails = null,
        string? impersonationSigningKey = null, Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
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
