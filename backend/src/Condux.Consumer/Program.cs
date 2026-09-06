using Condux.Consumer;
using Condux.Core.FixEngine;
using Condux.Core.OrgNotifications;
using Condux.Core.Quotas;
using Condux.Messaging;
using Condux.Notifications;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

// OpenTelemetry (traces + metrics over OTLP; opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.AddConduxTelemetry("condux-consumer");
builder.AddConduxSelfReporting(); // Condux on Condux (#75): the consumer reports its own errors, opt-in

// Resilient ClickHouse HTTP client (retry/timeout/circuit-breaker). Config is env-driven — no
// hardcoded fallback; fails fast if unset.
var clickHouseUrl = builder.Configuration.Require("CONDUX_CLICKHOUSE_URL");
var clickHouseUser = builder.Configuration.Require("CONDUX_CLICKHOUSE_USER");
var clickHousePassword = builder.Configuration.Require("CONDUX_CLICKHOUSE_PASSWORD");
builder.Services.AddClickHouseNamedClient(clickHouseUrl, clickHouseUser, clickHousePassword);

// Alerting (#55): the dispatcher loads a project's enabled rules and fans a fired alert out to its
// channels via the notifier registered for each channel (#56-58).
var pg = builder.Configuration.Require("CONDUX_POSTGRES");
builder.Services.AddSingleton(new AlertRuleRepository(pg));
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddAlertNotifiers(builder.Configuration);

// Auto-fix mode (#101): when an org is in auto mode, a new/regressed error-or-worse issue publishes a
// FixJob to the Conductor topic (quota-gated, best-effort) — the same context assembly + publish the
// manual RequestFix does. Kafka bootstrap defaults to the compose broker; the producer connects lazily.
builder.Services.AddSingleton(new ProjectRepository(pg));
builder.Services.AddSingleton(new OrgRepository(pg));
builder.Services.AddSingleton(new RepoLinkRepository(pg));
builder.Services.AddSingleton(new ReleaseRepository(pg));
builder.Services.AddSingleton(new GithubInstallationRepository(pg));
builder.Services.AddSingleton<IAiFixQuota>(new PostgresAiFixQuota(pg));
builder.Services.AddSingleton<IAiFixSpend>(new PostgresAiFixSpend(pg));
builder.Services.AddSingleton<IFixRequestPublisher>(
    new KafkaFixRequestPublisher(builder.Configuration["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092"));
// The other destination: an org that runs its own runner gets a leasable row instead of a topic message
// (ADR-0033 slice 4c), so auto-fix routes the same way the manual request does. The fix store comes with
// it, because a run queued here never reaches the orchestrator that would otherwise open its audit trail.
builder.Services.AddSingleton(new PostgresJobLeaseStore(pg));
builder.Services.AddSingleton<IFixStore>(new PostgresFixStore(pg));

// Conductor-pause notices (#130): when auto-fix is skipped because the org's cost cap or allowance is
// reached, notify the org's channels once per window (throttled) via the same notifiers as alerts.
builder.Services.AddSingleton(new OrgNotificationChannelRepository(pg));
builder.Services.AddSingleton<IPauseNotifyThrottle>(new PostgresPauseNotifyThrottle(pg));
builder.Services.AddSingleton<OrgNotificationDispatcher>();
builder.Services.AddSingleton<ConductorPauseNotifier>();
builder.Services.AddSingleton<AutoFixDispatcher>();

// Weekly summary emails (ADR-0031): an hourly-wake worker sends each enabled org's digest once per week (a
// ledger claim dedupes) to every member, via the shared branded-email path. Reuses the notifier SMTP stack
// already registered above; adds the typed ClickHouse readers + the repos the digest aggregation needs.
builder.Services.AddClickHouseReader<ClickHouseIssueStatsReader>(clickHouseUrl, clickHouseUser, clickHousePassword);
builder.Services.AddClickHouseReader<ClickHouseEventReader>(clickHouseUrl, clickHouseUser, clickHousePassword);
builder.Services.AddSingleton(new IssueRepository(pg));
builder.Services.AddSingleton(new OrgMemberRepository(pg));
builder.Services.AddSingleton(new PostgresFixStore(pg));
builder.Services.AddSingleton(new PostgresWeeklySummaryLedger(pg));
builder.Services.AddTransient<WeeklySummaryComposer>();
builder.Services.AddSingleton<WeeklySummaryMailer>();
builder.Services.AddHostedService<WeeklySummaryWorker>();

builder.Services.AddHostedService<ConsumerWorker>();
builder.Build().Run();
