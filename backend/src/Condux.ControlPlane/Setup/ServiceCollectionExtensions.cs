using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Llm;
using Condux.Core.Auth;
using Condux.Core.CveFix;
using Condux.Core.CveScanning;
using Condux.Core.FixEngine;
using Condux.Core.Secrets;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Condux.Core.Quotas;
using Condux.Messaging;
using Condux.Notifications;
using Condux.Storage.ClickHouse;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.AspNetCore.Authentication;

namespace Condux.ControlPlane.Setup;

/// <summary>Service registration for the control-plane: data stores + auth.</summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>CORS policy name for the credentialed dashboard origin(s).</summary>
    public const string CorsPolicy = "condux-dashboard";

    public static IServiceCollection AddControlPlaneServices(this IServiceCollection services, IConfiguration cfg)
    {
        // Datastore connections come from the environment — never hardcode credentials, even for dev
        // (dev values are supplied by docker-compose). Missing config fails fast with a clear message.
        var postgres = cfg.Require("CONDUX_POSTGRES");

        services.AddSingleton(new IssueRepository(postgres));
        services.AddSingleton(new IssueSeenRepository(postgres));
        services.AddSingleton(new IssueNoteRepository(postgres));
        services.AddSingleton(new SavedViewRepository(postgres));
        services.AddSingleton(new OrgRepository(postgres));
        services.AddSingleton(new ProjectRepository(postgres));
        services.AddSingleton(new DsnKeyRepository(postgres));
        services.AddSingleton(new UserRepository(postgres));
        services.AddSingleton(new SessionRepository(postgres));
        services.AddSingleton(new OrgMemberRepository(postgres));
        services.AddSingleton(new OrgInviteRepository(postgres));
        services.AddSingleton(new RepoLinkRepository(postgres));
        services.AddSingleton(new ReleaseRepository(postgres));
        services.AddSingleton(new ReleaseTokenRepository(postgres));
        services.AddSingleton(new McpTokenRepository(postgres));
        // Customer-hosted runners (ADR-0033 slice 4): the credential they authenticate with, and the
        // store they lease work from.
        services.AddSingleton(new RunnerTokenRepository(postgres));
        services.AddSingleton(new PostgresJobLeaseStore(postgres));
        // Live-badge nudges (ADR-0030): fired when a fix run changes outside any browser — queued for a
        // runner, claimed, or reported — so open dashboards refetch instead of waiting for a poll. The
        // consumer fires the same channel for issue events; the listener below fans both out over SSE.
        services.AddSingleton(new ProjectEventNotifier(postgres));
        services.AddSingleton(new SourceMapArtifactRepository(postgres));
        services.AddSingleton(new AlertRuleRepository(postgres));
        services.AddSingleton(new OrgNotificationChannelRepository(postgres));
        services.AddSingleton(new AdminRepository(postgres));
        // Admin management console (ADR-0027): the audit trail + cross-org Conductor spend reads.
        services.AddSingleton(new AdminAuditRepository(postgres));
        services.AddSingleton(new AdminSpendRepository(postgres));
        services.AddSingleton(new GithubInstallationRepository(postgres));

        // The readiness probe's own client (/api/readyz), with a timeout well under any monitor's: a probe
        // that hangs is worse than one that fails, because a monitor cannot tell it apart from its own
        // network trouble. StoreReadiness bounds each check too; this is the backstop.
        services.AddHttpClient("readiness", c => c.Timeout = StoreReadiness.Timeout);

        // The alert/org-notification delivery layer, so a channel can be test-sent from the dashboard
        // (the same notifiers the consumer uses; webhook/slack always on, email opt-in via CONDUX_SMTP_HOST).
        // ChannelTester resolves the INotifier for a channel and reports the outcome instead of throwing.
        services.AddAlertNotifiers(cfg);
        services.AddSingleton<ChannelTester>();
        // Alert dispatch from the control-plane too: triage events (issue resolved/assigned) fire alert
        // rules here, not just the consumer's new-issue/regression path.
        services.AddSingleton<AlertDispatcher>();
        // Invite emails ride the same SMTP relay (best-effort; a no-op when SMTP is off, see InviteMailer).
        services.AddSingleton<Condux.ControlPlane.Invites.InviteMailer>();

        // GitHub App (the Conductor, #61). Opt-in: enabled only when CONDUX_GITHUB_* are set; a partial
        // config fails fast here. When unset the /api/github/* routes return 404. The token minter + repo
        // client (behind the branch-picker proxy) are only registered when enabled — endpoints that need
        // them resolve lazily after checking config.Enabled.
        var githubConfig = GitHubAppConfig.FromEnv(cfg);
        services.AddSingleton(githubConfig);
        // The user-authorization leg of connect (relinking an already-installed app). Inert without the
        // client secret, so it registers unconditionally and the endpoints check the config.
        services.AddHttpClient<GitHubUserOAuth>();
        if (githubConfig.Enabled)
        {
            // GitHub is registered under both the concrete type (its GitHub-only Dependabot read) and the
            // neutral source-host seam (#145) that the fix flow + branch picker resolve: token minter + client.
            var githubTokens = new GitHubInstallationTokens(
                new HttpClient(), githubConfig.Options!, () => DateTimeOffset.UtcNow);
            services.AddSingleton(githubTokens);
            services.AddSingleton<ISourceHostTokens>(githubTokens);
            var githubRepoClient = new GitHubRepoClient(new HttpClient());
            services.AddSingleton(githubRepoClient);
            services.AddSingleton<ISourceHostClient>(githubRepoClient);
            // GitHub's CVE fast path (ADR-0022): Dependabot has already scanned, so findings are a read.
            // The neutral scanner runs a container and queries an advisory database, which is too slow for
            // a page load and belongs behind a job — so it is not the one registered here.
            services.AddSingleton<ICveScanner>(new DependabotCveScanner(githubRepoClient));
        }

        // "Sign in with Google" (OIDC, #71). Opt-in: enabled only when CONDUX_GOOGLE_* are set; a partial
        // config fails fast here. When unset the /api/auth/oauth/google/* routes 404 and the login page hides
        // the button. The typed OIDC client is inert without config, so it's always registered for injection.
        services.AddSingleton(GoogleOAuthConfig.FromEnv(cfg));
        services.AddHttpClient<GoogleOidcClient>();

        // Enterprise SSO (per-org OIDC, #72). The exchange client is always registered (inert without a
        // config; it's only reached after the per-org sso_config is found); the per-org config STORE needs
        // the secret box, so it registers with the secrets block below and endpoints resolve it lazily.
        services.AddHttpClient<SsoOidcClient>();

        // Stripe billing (#billing). Opt-in: enabled only when CONDUX_STRIPE_* are set; a partial config
        // fails fast. When unset the /api/orgs/{id}/billing/* + /api/stripe/webhook routes 404. The typed
        // Stripe client is inert without config, so it's always registered for injection.
        services.AddSingleton(StripeConfig.FromEnv(cfg));
        services.AddHttpClient<Billing.StripeClient>();

        // Platform super-admins: an env allowlist (CONDUX_PLATFORM_ADMIN_EMAILS, comma-separated) that
        // the auth handler resolves to a claim per request. Empty by default, so no one is a platform
        // admin unless explicitly listed — dev sets it in deploy/.env, production leaves it unset.
        services.AddSingleton(new EmailAllowlist(cfg["CONDUX_PLATFORM_ADMIN_EMAILS"]));

        // Read-only "view as org" impersonation (ADR-0027). Opt-in behind CONDUX_IMPERSONATION_SIGNING_KEY
        // (its own key, not coupled to Stripe/GitHub/secrets). When unset the start/stop routes 404 and the
        // scope grant is inert, so the capability is fully off unless an operator enables it.
        services.AddSingleton(ImpersonationConfig.FromEnv(cfg));

        // The Conductor: read fix suggestions, and publish fix requests to the topic its worker drains.
        // Kafka bootstrap is optional (defaults to the compose broker); the producer connects lazily.
        // Registered under the concrete type too — the weekly digest composer reads its org-scoped fix
        // aggregate (ADR-0031); IFixStore resolves to the same instance.
        services.AddSingleton(new PostgresFixStore(postgres));
        services.AddSingleton<IFixStore>(sp => sp.GetRequiredService<PostgresFixStore>());
        // Fix verification (ADR-0019): the webhook marks a merged Conductor PR as watching here.
        services.AddSingleton(new PostgresFixVerification(postgres));
        // The Fixes section read/write model (#118): project-wide list, detail + audit, view + archive.
        services.AddSingleton(new PostgresFixCatalog(postgres));

        // BYO-key registry (#65). Opt-in: the /api/orgs/{id}/llm-config routes activate only when
        // CONDUX_SECRET_KEY (the base64 AES-256 master key) is set, so keys are never stored without
        // an encryption key. When unset the routes 404, like the GitHub App.
        var secretKey = cfg["CONDUX_SECRET_KEY"];
        var secretsEnabled = !string.IsNullOrEmpty(secretKey);
        services.AddSingleton(new SecretsConfig(secretsEnabled));
        if (secretsEnabled)
        {
            services.AddSingleton(new SecretBox(secretKey!));
            services.AddSingleton(new PostgresLlmConfigStore(postgres));
            services.AddHttpClient<ILlmKeyValidator, LlmKeyValidator>();
            // Per-org SSO config store — the client secret is sealed with the same SecretBox (#72).
            services.AddSingleton(new PostgresSsoConfigStore(postgres));
        }

        // TOTP second factor (ADR-0039). The store and the service register unconditionally, unlike the
        // SecretBox-gated blocks above: MultiFactor takes a NULLABLE SecretBox and reports Available,
        // so a deployment without CONDUX_SECRET_KEY answers "not configured" and names the variable
        // rather than 404ing the routes, which would read as "this product has no MFA".
        services.AddSingleton(new UserMfaRepository(postgres));
        services.AddSingleton(sp => new MultiFactor(
            sp.GetRequiredService<UserMfaRepository>(),
            sp.GetRequiredService<SessionRepository>(),
            sp.GetService<SecretBox>()));
        // The per-org AI-fix allowance counter RequestFix reserves against (#100, ADR-0017).
        services.AddSingleton<IAiFixQuota>(new PostgresAiFixQuota(postgres));
        // The per-org month-to-date Conductor spend, for the cost cap + the usage meter (#120).
        services.AddSingleton<IAiFixSpend>(new PostgresAiFixSpend(postgres));
        // Registered as a factory (not an eager instance) so the Confluent producer — and its background
        // broker connection — is only built when a fix is first requested, not at startup. This is what
        // lets tests override it with a no-op fake without a real librdkafka producer ever spinning up
        // (an eager `new` would run before any override and flood the log with connection-refused errors).
        services.AddSingleton<IFixRequestPublisher>(
            _ => new KafkaFixRequestPublisher(cfg["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092"));

        // Supply-chain CVE-bump runs (#117 slice 2): read the per-repo runs, and publish a bump request
        // to the topic the Conductor's CVE worker drains. A separate domain from the issue fixes above.
        services.AddSingleton<ICveFixStore>(new PostgresCveFixStore(postgres));
        services.AddSingleton<ICveFixPublisher>(
            _ => new KafkaCveFixPublisher(cfg["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092"));

        // Event store (ClickHouse) for the issue-detail view — a resilient typed HttpClient
        // (retry/timeout/circuit-breaker via IHttpClientFactory + the standard resilience handler).
        services.AddClickHouseEventReader(
            cfg.Require("CONDUX_CLICKHOUSE_URL"),
            cfg.Require("CONDUX_CLICKHOUSE_USER"),
            cfg.Require("CONDUX_CLICKHOUSE_PASSWORD"));
        // The exact per-issue histogram (issue_stats_1h rollup, #102), same resilient client shape.
        services.AddClickHouseIssueStatsReader(
            cfg.Require("CONDUX_CLICKHOUSE_URL"),
            cfg.Require("CONDUX_CLICKHOUSE_USER"),
            cfg.Require("CONDUX_CLICKHOUSE_PASSWORD"));

        // Object storage (S3/MinIO) for source-map artifacts (ADR-0028). Opt-in: the IObjectStore client
        // registers only when CONDUX_S3_* are set, so the /api/sourcemaps routes 404 until configured. The
        // read-time symbolicator needs the store, so it too registers only when the store is configured.
        services.AddObjectStore(cfg);
        if (ObjectStoreConfig.FromEnv(cfg).Enabled)
        {
            services.AddSingleton<SourceMaps.FrameSymbolicator>();
        }
        // The shared read-time symbolication service (issue detail/events + MCP tools). Always registered
        // with an optional symbolicator, so it no-ops when object storage is off and callers never branch.
        services.AddSingleton(sp => new SourceMaps.EventSymbolication(
            sp.GetService<SourceMaps.FrameSymbolicator>(),
            sp.GetRequiredService<ILogger<SourceMaps.EventSymbolication>>()));

        // Live nav-badge stream (ADR-0030): the in-process SSE fan-out hub + one background LISTEN
        // connection on the shared project-events channel (fed by the consumer's NOTIFY). Postgres-only,
        // no new infra; the badge also polls, so a listener blip is self-healing.
        services.AddSingleton<Realtime.ProjectEventHub>();
        services.AddHostedService(sp => new Realtime.ProjectEventListener(
            postgres,
            sp.GetRequiredService<Realtime.ProjectEventHub>(),
            sp.GetRequiredService<ILogger<Realtime.ProjectEventListener>>()));

        // Weekly summary emails (ADR-0031): the dashboard can send an org admin a live test digest. Reuses the
        // same composer + mailer the consumer's scheduled worker runs (the composer needs transient ClickHouse
        // readers, so it is transient too); the mailer no-ops when SMTP is off.
        services.AddTransient<Notifications.WeeklySummaryComposer>();
        services.AddSingleton<Notifications.WeeklySummaryMailer>();

        // Optional dev-only admin seed: register the hosted seeder only when both env vars are set, so
        // a local stack gets a ready-to-use login and production (which leaves them unset) seeds nothing.
        var seedEmail = cfg["CONDUX_SEED_ADMIN_EMAIL"];
        var seedPassword = cfg["CONDUX_SEED_ADMIN_PASSWORD"];
        if (!string.IsNullOrEmpty(seedEmail) && !string.IsNullOrEmpty(seedPassword))
        {
            services.AddHostedService(sp => new DevAdminSeeder(
                seedEmail, seedPassword,
                sp.GetRequiredService<UserRepository>(),
                sp.GetRequiredService<OrgRepository>(),
                sp.GetRequiredService<OrgMemberRepository>(),
                sp.GetRequiredService<ILogger<DevAdminSeeder>>()));
        }

        return services;
    }

    // The session cookie resolves to a ClaimsPrincipal via a custom scheme (keeps the door open for
    // OIDC/SSO #71 — just add another scheme). Authorization gates the protected endpoints.
    public static IServiceCollection AddControlPlaneAuth(this IServiceCollection services)
    {
        services
            .AddAuthentication(SessionAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuth.Scheme, null);
        services.AddAuthorization();
        return services;
    }

    // Cross-origin access for the dashboard. In production the dashboard is served same-origin
    // (app.condux.ai fronts both), so this stays unset and no CORS headers are emitted. In split-origin
    // setups (compose/local dev: web on :3000, API on :8080) set CONDUX_CORS_ORIGINS to a comma list of
    // allowed origins; credentialed requests (the session cookie) require explicit origins, never "*".
    public static IServiceCollection AddControlPlaneCors(this IServiceCollection services, IConfiguration cfg)
    {
        var origins = (cfg["CONDUX_CORS_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
        }));
        return services;
    }
}
