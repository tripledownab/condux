using Condux.Conductor;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.Core.Quotas;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Condux.Storage.ClickHouse;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// OpenTelemetry (traces + metrics over OTLP; opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.AddConduxTelemetry("condux-conductor");
builder.AddConduxSelfReporting(); // Condux on Condux (#75): the conductor workers report their own errors, opt-in

// Config is env-driven — no hardcoded credentials, even for dev (compose supplies them). Fail fast.
var postgres = builder.Configuration.Require("CONDUX_POSTGRES");
builder.Services.AddSingleton<IFixStore>(new PostgresFixStore(postgres));
// The org allowance counter: a failed run refunds the reservation RequestFix took (#100, ADR-0017).
builder.Services.AddSingleton<IAiFixQuota>(new PostgresAiFixQuota(postgres));

// Provider selection (CONDUX_CONDUCTOR_PROVIDER, default "fake"). The two defaults open no real PR and
// call no LLM, so CI and a bare install stay safe:
//   fake            — the deterministic FakeFixProvider (fabricates a result instantly).
//   simulated-agent — the real ManagedAgentFixProvider orchestration (start → poll → draft PR) over a
//                     SimulatedAgentGateway, so the full poll lifecycle runs locally with no LLM/GitHub.
//   anthropic       — the real thing: Claude proposes the fix from the scoped, scrubbed context and the
//                     gateway opens a draft PR via the GitHub App. Requires CONDUX_ANTHROPIC_API_KEY plus
//                     the GitHub App credentials (fails fast when missing).
var providerKind = builder.Configuration["CONDUX_CONDUCTOR_PROVIDER"] ?? "fake";
switch (providerKind)
{
    case "fake":
        builder.Services.AddSingleton<IFixProvider, FakeFixProvider>();
        break;
    case "simulated-agent":
        builder.Services.AddSingleton<IAgentGateway, SimulatedAgentGateway>();
        break;
    case "anthropic":
        var anthropicOptions = new AnthropicOptions(builder.Configuration.Require("CONDUX_ANTHROPIC_API_KEY"));
        var githubOptions = new GitHubAppOptions(
            builder.Configuration.Require("CONDUX_GITHUB_CLIENT_ID"),
            ReadPrivateKey(builder.Configuration),
            builder.Configuration["CONDUX_GITHUB_WEBHOOK_SECRET"] ?? "");

        // Plain singleton HttpClients: the worker is long-running but restarted on deploys, and the fix
        // call can legitimately take minutes — so the Anthropic client gets a wide timeout and neither
        // goes through the short-timeout resilience pipeline.
        // The provider model clients (#66). Both are registered as IModelClient; the gateway picks the
        // one matching a run's resolved provider (Anthropic by default; an org's BYO config may select
        // openai-compat). The fix call can take minutes, so each gets a wide timeout and skips the
        // short-timeout resilience pipeline.
        builder.Services.AddSingleton<IModelClient>(new AnthropicMessagesClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, anthropicOptions));
        builder.Services.AddSingleton<IModelClient>(new OpenAiCompatClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) }));
        // The fix gateway talks to the neutral source-host seam (#145); GitHub is today's implementation of
        // both the token minter and the repo client.
        var githubTokens = new GitHubInstallationTokens(
            new HttpClient(), githubOptions, () => DateTimeOffset.UtcNow);
        builder.Services.AddSingleton(githubTokens);
        builder.Services.AddSingleton<ISourceHostTokens>(githubTokens);
        builder.Services.AddSingleton<ISourceHostClient>(new GitHubRepoClient(new HttpClient()));

        // BYO-key resolution (#65): when CONDUX_SECRET_KEY is set, a run for an org with a stored config
        // uses that org's decrypted key + model; otherwise every run uses the platform default.
        var secretKey = builder.Configuration["CONDUX_SECRET_KEY"];
        var box = string.IsNullOrEmpty(secretKey) ? null : new SecretBox(secretKey);
        ILlmConfigReader? llmConfigs = box is null ? null : new PostgresLlmConfigStore(postgres);
        builder.Services.AddSingleton(new ConductorKeyResolver(anthropicOptions.ApiKey, llmConfigs, box));

        // The agentic loop (ADR-0033) is opt-in while it proves out: on, the model explores and edits the
        // scoped checkout over several turns; off, it makes the single ADR-0016 patch-proposal call. The
        // single-shot path also stays the fallback for any provider that cannot drive tools.
        var agentic = builder.Configuration.GetValue("CONDUX_CONDUCTOR_AGENTIC", defaultValue: false);
        builder.Services.AddSingleton<IAgentGateway>(sp => new AnthropicAgentGateway(
            sp.GetRequiredService<IEnumerable<IModelClient>>(),
            sp.GetRequiredService<ISourceHostTokens>(),
            sp.GetRequiredService<ISourceHostClient>(),
            sp.GetRequiredService<ConductorKeyResolver>(),
            // A turn can take minutes, same as the single-shot call, so this bypasses the short default.
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            agentic));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown CONDUX_CONDUCTOR_PROVIDER '{providerKind}'. Use 'fake', 'simulated-agent' or 'anthropic'.");
}

if (providerKind != "fake")
{
    builder.Services.AddSingleton<IFixProvider>(sp =>
        new ManagedAgentFixProvider(sp.GetRequiredService<IAgentGateway>()));
}

builder.Services.AddSingleton<FixOrchestrator>();

// The supply-chain CVE-bump path (#117): a separate domain + topic + worker that reuses the same
// IFixProvider + IAiFixQuota registered above. Its runs live in cve_fix_runs, keyed on the repo +
// advisory (never an issue), so it does not touch the issue-fix verification loop.
builder.Services.AddSingleton<ICveFixStore>(new PostgresCveFixStore(postgres));
builder.Services.AddSingleton<CveFixOrchestrator>();

// The fix-verification watcher (ADR-0019) reads the issue's exact hourly stats from ClickHouse,
// so the conductor now requires the same ClickHouse config as the control-plane and consumer.
builder.Services.AddClickHouseIssueStatsReader(
    builder.Configuration.Require("CONDUX_CLICKHOUSE_URL"),
    builder.Configuration.Require("CONDUX_CLICKHOUSE_USER"),
    builder.Configuration.Require("CONDUX_CLICKHOUSE_PASSWORD"));
builder.Services.AddSingleton(new PostgresFixVerification(postgres));
builder.Services.AddSingleton(new IssueRepository(postgres));

builder.Services.AddHostedService<ConductorWorker>();
builder.Services.AddHostedService<CveFixWorker>();
builder.Services.AddHostedService<VerificationWorker>();
builder.Build().Run();

// The GitHub App private key, inline (CONDUX_GITHUB_PRIVATE_KEY) or from the mounted .pem path — the
// same resolution the control-plane uses. Required by the anthropic provider; fails fast when absent.
static string ReadPrivateKey(IConfiguration cfg)
{
    var inline = cfg["CONDUX_GITHUB_PRIVATE_KEY"];
    if (!string.IsNullOrEmpty(inline))
    {
        return inline;
    }

    var path = cfg["CONDUX_GITHUB_PRIVATE_KEY_PATH"];
    return !string.IsNullOrEmpty(path) && File.Exists(path)
        ? File.ReadAllText(path)
        : throw new InvalidOperationException(
            "The anthropic provider needs the GitHub App private key: set CONDUX_GITHUB_PRIVATE_KEY or a "
            + "readable CONDUX_GITHUB_PRIVATE_KEY_PATH.");
}
