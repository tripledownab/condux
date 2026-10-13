using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Endpoints;
using Condux.ControlPlane.Llm;
using Condux.ControlPlane.Uploads;
using Condux.Core.Auth;
using Condux.Core.Http;
using Condux.Core.Secrets;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.AspNetCore.Authentication;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Service registration for the control-plane: <see cref="AddControlPlaneServices"/> registers the data
/// stores and the services built on them, calling the registrations kept beside their domain (the
/// third-party integrations, the Conductor, the event reads, the admin seed). Those calls keep the order
/// the configuration checks ran in before the split, so a misconfigured startup reports the same error
/// first. <see cref="AddControlPlaneAuth"/> and <see cref="AddControlPlaneCors"/> are the two other
/// registrations Program calls.
/// </summary>
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
        services.AddSingleton(new PasswordResetRepository(postgres));
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
        services.AddSingleton(new DebugFileRepository(postgres));
        services.AddSingleton<DebugFileUploadGate>();
        services.AddUploadStorage(cfg, postgres);
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
        // Its org-level counterpart (#129), for a notice addressed to the org rather than to a project's
        // alert rules. The consumer has had this since the Conductor-pause notices; the control-plane needs
        // it now that the SSO domain re-check below can tell an org its claim is about to stop routing.
        services.AddSingleton<OrgNotificationDispatcher>();
        // The one home for a triage change and its side effects, shared by the REST endpoints and the MCP
        // triage tools (ADR-0046) so an agent's resolve fires the same rules as a human's.
        services.AddSingleton<Issues.IssueTriage>();
        // Invite emails ride the same SMTP relay (best-effort; a no-op when SMTP is off, see InviteMailer).
        services.AddSingleton<Condux.ControlPlane.Invites.InviteMailer>();
        services.AddSingleton<Condux.ControlPlane.Auth.PasswordResetMailer>();

        // Third-party providers: the GitHub App, Google sign-in and Stripe, opt-in from the environment, and
        // enterprise SSO's OIDC client.
        services.AddThirdPartyIntegrations(cfg);

        // Platform super-admins: an env allowlist (CONDUX_PLATFORM_ADMIN_EMAILS, comma-separated) that
        // the auth handler resolves to a claim per request. Empty by default, so no one is a platform
        // admin unless explicitly listed — dev sets it in deploy/.env, production leaves it unset.
        services.AddSingleton(new EmailAllowlist(cfg["CONDUX_PLATFORM_ADMIN_EMAILS"]));

        // Read-only "view as org" impersonation (ADR-0027). Opt-in behind CONDUX_IMPERSONATION_SIGNING_KEY
        // (its own key, not coupled to Stripe/GitHub/secrets). When unset the start/stop routes 404 and the
        // scope grant is inert, so the capability is fully off unless an operator enables it.
        services.AddSingleton(ImpersonationConfig.FromEnv(cfg));

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
            // Domain verification (ADR-0043). A timeout because a resolver that never answers would hold a
            // Verify request open; failing to reach one is a reportable outcome, not something to wait out.
            services.AddHttpClient<Auth.DohTxtResolver>(c => c.Timeout = TimeSpan.FromSeconds(5));
            services.AddScoped<Auth.SsoDomainVerifier>();
            // Slice 2: re-read those records daily and take a claim away once its record has been gone
            // through the grace period, warning the org first. Registered with the store it reads, so a
            // deployment with no secret key runs no timer for a table it has no rows in.
            services.AddHostedService<Auth.SsoVerificationWorker>();
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

        // The Conductor's run stores, allowance, spend and job publishers.
        services.AddConductorServices(cfg, postgres);

        // Reads of the event store, and the symbolication applied as events are read.
        services.AddEventReads(cfg, postgres);

        // The MCP tool handlers and the per-token write budget behind them (ADR-0029, ADR-0046). The
        // limiter holds the buckets, so it has to be the same instance across requests. The handlers must
        // NOT: they take ClickHouseEventReader, which AddClickHouseReader registers as a typed HttpClient,
        // and a singleton holding one pins its message handler for the process lifetime (the captive
        // dependency AddClickHouseNamedClient exists to avoid). Scoped is what the endpoints already were.
        services.AddSingleton<Mcp.McpWriteLimiter>();
        services.AddScoped<Mcp.McpTools>();

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

        // The optional admin seed, which runs only when both seed variables are set.
        services.AddAdminSeed(cfg);

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
    // setups (compose/local dev: web on :3200, API on :8080) set CONDUX_CORS_ORIGINS to a comma list of
    // allowed origins; credentialed requests (the session cookie) require explicit origins, never "*".
    public static IServiceCollection AddControlPlaneCors(this IServiceCollection services, IConfiguration cfg)
    {
        // The whole list, not the first entry: this is the only caller that wants every origin.
        var origins = AppOrigins.Parse(cfg["CONDUX_CORS_ORIGINS"]);

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
