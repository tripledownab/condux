using System.Net.Http.Headers;
using Condux.Agent;
using Condux.Agent.Sandbox;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Condux.Runner;
using Condux.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The customer-hosted runner (ADR-0033 slice 4, #68). It leases fix work from the control plane over one
// authenticated outbound connection, runs it here with the customer's own model and source-host
// credentials, and reports the outcome back. Neither credential ever reaches Condux, and Condux never
// needs a route into the customer's network.
var builder = Host.CreateApplicationBuilder(args);

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

builder.AddConduxTelemetry("condux-runner");
builder.AddConduxSelfReporting(); // Opt-in, and the customer points it wherever they want.

// Config is env-driven and fails fast, same as every service: a runner that starts without a token would
// poll forever answering 401, looking alive while doing nothing.
var controlPlane = builder.Configuration.Require("CONDUX_CONTROL_PLANE_URL");
var runnerToken = builder.Configuration.Require("CONDUX_RUNNER_TOKEN");
var model = builder.Configuration["CONDUX_RUNNER_MODEL"] ?? ModelDefaults.Fix;

var lease = new HttpClient
{
    // The trailing slash matters: without it a base of ".../condux" would drop its last segment when the
    // relative request path is resolved against it.
    BaseAddress = new Uri(controlPlane.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(30),
};
lease.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", runnerToken);
builder.Services.AddSingleton(new LeaseClient(lease));

builder.Services.AddSingleton(new RunnerOptions(
    model,
    IdlePoll: TimeSpan.FromSeconds(
        builder.Configuration.GetValue("CONDUX_RUNNER_POLL_SECONDS", defaultValue: 5)),
    ErrorBackoff: TimeSpan.FromSeconds(15)));

// Provider selection mirrors the hosted Conductor, minus every backend that needs our infrastructure:
//   fake      — the deterministic FakeFixProvider. Calls no model and opens no PR, so a customer can
//               verify the lease path end to end before handing over any credential.
//   anthropic — the real thing, against the customer's own key and their own source-host token.
var providerKind = builder.Configuration["CONDUX_RUNNER_PROVIDER"] ?? "fake";
switch (providerKind)
{
    case "fake":
        builder.Services.AddSingleton<IFixProvider, FakeFixProvider>();
        break;
    case "anthropic":
        // Which client answers is decided by the resolved provider, so an OpenAI-compatible endpoint
        // (vLLM, Ollama, LiteLLM, OpenAI itself) needs only a base URL rather than another backend.
        var apiKey = builder.Configuration.Require("CONDUX_RUNNER_MODEL_KEY");
        var baseUrl = builder.Configuration["CONDUX_RUNNER_MODEL_BASE_URL"] ?? "";
        var modelProvider = string.IsNullOrEmpty(baseUrl) ? "anthropic" : "openai-compat";
        var sourceToken = builder.Configuration.Require("CONDUX_RUNNER_SOURCE_TOKEN");

        // A fix call legitimately takes minutes, so these bypass the short default rather than failing
        // a run that is progressing.
        builder.Services.AddSingleton<IModelClient>(new AnthropicMessagesClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, new AnthropicOptions(apiKey)));
        builder.Services.AddSingleton<IModelClient>(new OpenAiCompatClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) }));

        builder.Services.AddSingleton<ISourceHostTokens>(new StaticSourceHostTokens(sourceToken));
        builder.Services.AddSingleton<ISourceHostClient>(new GitHubRepoClient(new HttpClient()));

        // No registry to consult: the key belongs to this process, so there is no org lookup and nothing
        // to decrypt. That is the whole difference from the hosted deployment.
        builder.Services.AddSingleton(new ModelKeyResolver(
            apiKey, configs: null, box: null, modelProvider, baseUrl));

        var agentic = builder.Configuration.GetValue("CONDUX_RUNNER_AGENTIC", defaultValue: false);
        var sandbox = SandboxOptions.FromEnv(builder.Configuration);
        builder.Services.AddSingleton<IAgentGateway>(sp => new AnthropicAgentGateway(
            sp.GetRequiredService<IEnumerable<IModelClient>>(),
            sp.GetRequiredService<ISourceHostTokens>(),
            sp.GetRequiredService<ISourceHostClient>(),
            sp.GetRequiredService<ModelKeyResolver>(),
            new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            agentic,
            sandbox));
        builder.Services.AddSingleton<IFixProvider>(sp =>
            new ManagedAgentFixProvider(sp.GetRequiredService<IAgentGateway>()));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown CONDUX_RUNNER_PROVIDER '{providerKind}'. Use 'fake' or 'anthropic'.");
}

builder.Services.AddHostedService<RunnerWorker>();
builder.Build().Run();
