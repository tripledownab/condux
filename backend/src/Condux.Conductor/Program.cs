using Condux.Agent;
using Condux.Agent.ManagedAgents;
using Condux.Agent.Sandbox;
using Condux.Conductor;
using Condux.Core.CveFix;
using Condux.Core.Llm;
using Condux.Core.FixEngine;
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

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

// OpenTelemetry (traces + metrics over OTLP; opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.AddConduxTelemetry("condux-conductor");
builder.AddConduxSelfReporting(); // Condux on Condux (#75): the conductor workers report their own errors, opt-in

// Config is env-driven — no hardcoded credentials, even for dev (compose supplies them). Fail fast.
var postgres = builder.Configuration.Require("CONDUX_POSTGRES");
builder.Services.AddSingleton<IFixStore>(new PostgresFixStore(postgres));

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
        AddAnthropicStack(builder, postgres, out var byoGateway, out _, out _, out _);
        builder.Services.AddSingleton<IAgentGateway>(sp => byoGateway(sp));
        break;
    case "managed-agents":
        // ADR-0038: the vendor-hosted backend, with the in-process stack kept alive beside it — a BYO
        // org's runs stay on its own key and provider (the routing composite decides per run).
        AddAnthropicStack(builder, postgres,
            out var byoInner, out var cmaTokens, out var cmaRepo, out var routedConfigs);
        var cmaOptions = ManagedAgentsOptions.FromEnv(builder.Configuration);
        // Control calls only (the vendor runs the long work), but session creation uploads the prompt, so
        // give it headroom over the 100s default.
        var cmaClient = new ManagedAgentsClient(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, cmaOptions);
        builder.Services.AddSingleton(new ManagedAgentsSessionSweeper(cmaClient, cmaOptions));
        builder.Services.AddSingleton<IAgentGateway>(sp => new RoutingAgentGateway(
            byoInner(sp),
            new ManagedAgentsGateway(cmaClient, cmaTokens, cmaRepo, cmaOptions),
            routedConfigs));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown CONDUX_CONDUCTOR_PROVIDER '{providerKind}'. Use 'fake', 'simulated-agent', 'anthropic' or 'managed-agents'.");
}

// Read for every provider, since the stale-run sweep sizes its cutoff from it too.
var pollOptions = ConductorPollOptions.FromEnv(builder.Configuration, providerKind);
builder.Services.AddSingleton(pollOptions);
if (providerKind != "fake")
{
    builder.Services.AddSingleton<IFixProvider>(sp =>
        new ManagedAgentFixProvider(sp.GetRequiredService<IAgentGateway>(), pollOptions));
}

// The full in-process fix stack (model clients, source-host seams, BYO-key resolution, the ADR-0033
// gateway) — one body serving both the 'anthropic' mode and, as the BYO side, 'managed-agents'.
void AddAnthropicStack(
    HostApplicationBuilder builder, string postgres,
    out Func<IServiceProvider, AnthropicAgentGateway> gatewayFactory,
    out GitHubInstallationTokens githubTokens, out ISourceHostClient repoClient,
    out ILlmConfigReader? llmConfigs)
{
    var anthropicOptions = new AnthropicOptions(builder.Configuration.Require("CONDUX_ANTHROPIC_API_KEY"));
    var githubOptions = new GitHubAppOptions(
        builder.Configuration.Require("CONDUX_GITHUB_CLIENT_ID"),
        ReadPrivateKey(builder.Configuration),
        builder.Configuration["CONDUX_GITHUB_WEBHOOK_SECRET"] ?? "");

    // Plain singleton HttpClients: the worker is long-running but restarted on deploys, and the fix
    // call can legitimately take minutes — so each model client gets a wide timeout and skips the
    // short-timeout resilience pipeline. Both are IModelClient; the gateway picks the one matching a
    // run's resolved provider (Anthropic by default; an org's BYO config may select openai-compat, #66).
    builder.Services.AddSingleton<IModelClient>(new AnthropicMessagesClient(
        new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, anthropicOptions));
    builder.Services.AddSingleton<IModelClient>(new OpenAiCompatClient(
        new HttpClient { Timeout = TimeSpan.FromMinutes(10) }));
    // The fix gateway talks to the neutral source-host seam (#145); GitHub is today's implementation of
    // both the token minter and the repo client.
    githubTokens = new GitHubInstallationTokens(new HttpClient(), githubOptions, () => DateTimeOffset.UtcNow);
    repoClient = new GitHubRepoClient(new HttpClient());
    builder.Services.AddSingleton(githubTokens);
    builder.Services.AddSingleton<ISourceHostTokens>(githubTokens);
    builder.Services.AddSingleton(repoClient);

    // BYO-key resolution (#65): when CONDUX_SECRET_KEY is set, a run for an org with a stored config
    // uses that org's decrypted key + model; otherwise every run uses the platform default.
    var secretKey = builder.Configuration["CONDUX_SECRET_KEY"];
    var box = string.IsNullOrEmpty(secretKey) ? null : new SecretBox(secretKey);
    llmConfigs = box is null ? null : new PostgresLlmConfigStore(postgres);
    builder.Services.AddSingleton(new ModelKeyResolver(anthropicOptions.ApiKey, llmConfigs, box));

    // The agentic loop (ADR-0033) is opt-in while it proves out: on, the model explores and edits the
    // scoped checkout over several turns; off, it makes the single ADR-0016 patch-proposal call. The
    // single-shot path also stays the fallback for any provider that cannot drive tools. The sandbox
    // (slice 2) is opt-in on top: without it the agent can read and edit but not run anything.
    var agentic = builder.Configuration.GetValue("CONDUX_CONDUCTOR_AGENTIC", defaultValue: false);
    var sandbox = SandboxOptions.FromEnv(builder.Configuration);
    gatewayFactory = sp => new AnthropicAgentGateway(
        sp.GetRequiredService<IEnumerable<IModelClient>>(),
        sp.GetRequiredService<ISourceHostTokens>(),
        sp.GetRequiredService<ISourceHostClient>(),
        sp.GetRequiredService<ModelKeyResolver>(),
        // A turn can take minutes, same as the single-shot call, so this bypasses the short default.
        new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
        agentic,
        sandbox,
        sp.GetRequiredService<ILogger<AnthropicAgentGateway>>());
}

builder.Services.AddSingleton<FixOrchestrator>();

// The supply-chain CVE-bump path (#117): a separate domain + topic + worker that reuses the same
// IFixProvider registered above. Its runs live in cve_fix_runs, keyed on the repo +
// advisory (never an issue), so it does not touch the issue-fix verification loop; a merged bump has
// its own (ADR-0051), below.
builder.Services.AddSingleton<ICveFixStore>(new PostgresCveFixStore(postgres));
builder.Services.AddSingleton<CveFixOrchestrator>();

// The fix-verification watcher (ADR-0019) reads the issue's exact hourly stats from ClickHouse,
// so the conductor now requires the same ClickHouse config as the control-plane and consumer.
builder.Services.AddClickHouseReader<ClickHouseIssueStatsReader>(
    builder.Configuration.Require("CONDUX_CLICKHOUSE_URL"),
    builder.Configuration.Require("CONDUX_CLICKHOUSE_USER"),
    builder.Configuration.Require("CONDUX_CLICKHOUSE_PASSWORD"));
builder.Services.AddSingleton(new PostgresFixVerification(postgres));
// The CVE bump watch (ADR-0051) reads the runtime dependency inventory, on the same timer.
builder.Services.AddClickHouseReader<ClickHouseReleaseModuleReader>(
    builder.Configuration.Require("CONDUX_CLICKHOUSE_URL"),
    builder.Configuration.Require("CONDUX_CLICKHOUSE_USER"),
    builder.Configuration.Require("CONDUX_CLICKHOUSE_PASSWORD"));
builder.Services.AddSingleton(new PostgresCveFixVerification(postgres));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CveFixVerifier>();
builder.Services.AddSingleton(new IssueRepository(postgres));

// Live dashboard nudges (ADR-0030): a hosted run concluding, a verification verdict and a CVE bump
// finishing all happen outside any browser, so without these pings the open fixes surface sits on its
// slow poll. Best-effort via ProjectEventNudge, like every other notify.
builder.Services.AddSingleton(new ProjectEventNotifier(postgres));
builder.Services.AddSingleton(new RepoLinkRepository(postgres));

builder.Services.AddHostedService<ConductorWorker>();
builder.Services.AddHostedService<CveFixWorker>();
builder.Services.AddHostedService<VerificationWorker>();
// Fails and refunds the runs a stop or crash left in progress, since their jobs are never redelivered.
builder.Services.AddSingleton(new PostgresStaleRunSweep(postgres));
// Asks GitHub whether a stopped run opened its pull request; the source host is registered only by the
// providers that can open one, and without it the check clears every run.
builder.Services.AddSingleton<UnrecordedPullRequests>();
builder.Services.AddHostedService<StaleRunSweeper>();
builder.Build().Run();

// The GitHub App private key, through the one resolver both deployments share, which is where the
// diagnostic for a key that exists but cannot be read lives. Only the meaning of ABSENT differs between
// them, and that difference is this line: the anthropic provider cannot run without a key.
static string ReadPrivateKey(IConfiguration cfg) =>
    GitHubPrivateKey.Resolve(cfg["CONDUX_GITHUB_PRIVATE_KEY"], cfg["CONDUX_GITHUB_PRIVATE_KEY_PATH"])
        ?? throw new InvalidOperationException(
            "The anthropic provider needs the GitHub App private key: set CONDUX_GITHUB_PRIVATE_KEY or a "
            + "readable CONDUX_GITHUB_PRIVATE_KEY_PATH.");
