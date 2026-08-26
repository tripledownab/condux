using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Ingest;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Condux.Core.Scrub;
using Condux.Messaging;
using Condux.Otlp;
using Condux.Relay.Ingest;
using Condux.Storage.Postgres;
using Condux.Storage.Quotas;
using Condux.Storage.RateLimiting;
using Condux.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;
// Aliased because OpenTelemetry.Trace exports its own Status and StatusCode. Any receiver that also
// uses the OpenTelemetry SDK hits the same clash, which is worth knowing before reaching for these.
using OtlpStatus = Condux.Otlp.Status;
using OtlpStatusCode = Condux.Otlp.StatusCode;

var builder = WebApplication.CreateBuilder(args);

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

// OpenTelemetry (traces + metrics over OTLP; opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
builder.AddConduxTelemetry("condux-relay",
    tracing => tracing.AddAspNetCoreInstrumentation(),
    metrics => metrics.AddAspNetCoreInstrumentation());

// Per-project rate limiter. Valkey-backed when CONDUX_VALKEY is set (one budget
// shared across all relay replicas); otherwise in-process (dev/single-relay). The
// rate + burst are sourced from the project's plan tier per request (see below),
// not fixed here.
var valkey = builder.Configuration["CONDUX_VALKEY"];
if (!string.IsNullOrWhiteSpace(valkey))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(valkey));
    builder.Services.AddSingleton<IRateLimiter>(sp =>
        new ValkeyRateLimiter(sp.GetRequiredService<IConnectionMultiplexer>()));
    builder.Services.AddSingleton<IQuotaMeter>(sp =>
        new ValkeyQuotaMeter(sp.GetRequiredService<IConnectionMultiplexer>()));
}
else
{
    builder.Services.AddSingleton<IRateLimiter>(new InMemoryRateLimiter());
    builder.Services.AddSingleton<IQuotaMeter>(new InMemoryQuotaMeter());
}

// Instance-wide spike protection: a hard events/sec ceiling that sheds load before
// parsing, protecting a single relay (and the pipeline) even when every project is
// within its own budget. Rate <= 0 disables it.
var spikeRate = ParseDouble(builder.Configuration["CONDUX_SPIKE_PER_SECOND"], 5_000);
var spikeBurst = ParseLong(builder.Configuration["CONDUX_SPIKE_BURST"], 10_000);
builder.Services.AddSingleton(new SpikeGuard(spikeRate, spikeBurst));

// Project/DSN auth store. Postgres-backed (behind a short-TTL cache so the hot
// path rarely hits the DB) when CONDUX_POSTGRES is set; otherwise a seeded
// in-memory dev store (project "1" / key "devkey").
var relayPostgres = builder.Configuration["CONDUX_POSTGRES"];
if (!string.IsNullOrWhiteSpace(relayPostgres))
{
    builder.Services.AddSingleton<IProjectStore>(
        new CachingProjectStore(new PostgresProjectStore(relayPostgres)));
}
else
{
    builder.Services.AddSingleton<IProjectStore>(new InMemoryProjectStore(
    [
        ("1", "devkey", Tier.Free),
    ]));
}

// Publish normalized events to Kafka/Redpanda. Tests override this with an in-memory publisher.
builder.Services.AddSingleton<IEventPublisher>(_ =>
    new KafkaEventPublisher(builder.Configuration["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092"));

// Most stock Sentry SDKs compress the request body: sentry-java sets Content-Encoding: gzip
// unconditionally, and sentry-dart compresses by default, so without this their events never parse.
builder.Services.AddRequestDecompression();

// The ceiling a body may reach once decompressed. Kestrel's limit bounds the COMPRESSED request, which
// a zip bomb slips under trivially, so the decompressed size is bounded explicitly where the body is
// read (ReadBoundedAsync). Enforcing it there rather than through IHttpMaxRequestBodySizeFeature is
// deliberate: that feature is not present under every host, so the guard silently did nothing.
var maxIngestBytes = ParseLong(builder.Configuration["CONDUX_MAX_INGEST_BYTES"], 20 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxIngestBytes);

// Condux on Condux (#75): opt-in self-error reporting via the Condux .NET SDK (CONDUX_SELF_DSN).
builder.AddConduxSelfReporting();

var app = builder.Build();

// Report the relay's own unhandled request exceptions to a Condux project when self-reporting is enabled.
// Fire-and-forget (the SDK reads the exception synchronously then POSTs on its own HttpClient and never
// throws), so it adds no latency to the error response. First in the pipeline, so it wraps every endpoint.
if (app.Services.GetService<Condux.Sdk.ConduxClient>() is { } conduxSelf)
{
    app.Use(async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            // A cancellation from a client disconnect or graceful shutdown is expected noise, not a fault;
            // still report a genuine OperationCanceledException thrown for any other reason.
            if (!ConduxSelfReport.IsExpectedCancellation(exception, context.RequestAborted.IsCancellationRequested))
            {
                _ = conduxSelf.CaptureExceptionAsync(exception);
            }
            throw;
        }
    });
}

// Inside the self-report wrapper above, so a decompression failure is reported like any other fault,
// and ahead of the ingest endpoints, which read an already-decompressed body.
app.UseRequestDecompression();

// Liveness: is the process answering. Deliberately checks nothing, because a container probe that fails
// on a dependency outage restarts a healthy process and makes the outage worse.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Readiness for an uptime monitor. The relay is the one service whose outage loses data rather than
// merely blocking a page: it answers an SDK "accepted" and then has nowhere to put the event. Liveness
// alone would report healthy through exactly that, so this checks what ingest actually needs — the broker
// it publishes to, and the catalog it authenticates DSNs against — and answers 503 when either is gone.
app.MapGet("/readyz", async (IEventPublisher publisher, CancellationToken ct) =>
{
    var broker = publisher is KafkaEventPublisher kafka
        ? kafka.CanReachBroker(StoreReadiness.Timeout)
        : true;
    // Postgres is optional here: without it the relay falls back to the seeded dev store, which is a
    // working configuration rather than a fault, so it is only required when it is configured.
    var catalog = string.IsNullOrEmpty(relayPostgres)
        || await StoreReadiness.PostgresAsync(relayPostgres, ct);

    // Same single token as the control-plane's, so one monitor rule covers both services.
    var ready = broker && catalog;
    var body = new { status = ready ? "ready" : "degraded", broker, catalog };
    return ready
        ? Results.Ok(body)
        : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

// Sentry-compatible ingest path so existing SDKs work by swapping the DSN.
// Shared ingest pipeline for the two Sentry-compatible endpoints: auth → per-tier rate-limit → spike →
// per-tier monthly quota → parse → scrub → publish. `parse` returns null when the payload carries no
// event item (an envelope of only sessions/transactions), which is accepted but stores nothing.
async Task IngestAsync(
    string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter, SpikeGuard spike,
    IProjectStore projects, IEventPublisher publisher, Func<byte[], ParseResult> parse)
{
    // 1. Authenticate the DSN public key against the project.
    var publicKey = ExtractPublicKey(ctx);
    var project = publicKey is null
        ? null
        : await projects.AuthenticateAsync(projectId, publicKey, ctx.RequestAborted);
    if (project is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid_dsn" });
        return;
    }

    // The URL segment is the project's public UUID (or a legacy numeric id); everything past auth keys
    // on the resolved numeric id (rate-limit, quota, the Kafka key → ClickHouse → issues FK), so the
    // internal id stays numeric and the UUID never leaves the relay.
    var projectKey = project.Id;

    // 2. Per-project rate limit, using the project's plan-tier rate + burst.
    var limits = PlanCatalog.For(project.Tier);
    var decision = await limiter.CheckAsync(projectKey, limits.RatePerSecond, limits.Burst, ctx.RequestAborted);
    ctx.Response.Headers["X-Condux-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
    if (!decision.Allowed)
    {
        ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.Response.WriteAsJsonAsync(new { error = "rate_limited" });
        return;
    }

    // 3. Instance-wide spike protection — the last cheap gate before we parse.
    var spikeDecision = spike.Check();
    if (!spikeDecision.Allowed)
    {
        ctx.Response.Headers.RetryAfter = spikeDecision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.Response.WriteAsJsonAsync(new { error = "spike_protection" });
        return;
    }

    // 4. Monthly event quota (plan tier). Enterprise is unlimited and skips this. Only admitted events
    // are counted, and only after the cheaper rate + spike gates, so shed events never consume quota.
    var quota = await quotaMeter.TryConsumeAsync(projectKey, limits.MonthlyEvents, ctx.RequestAborted);
    if (limits.MonthlyEvents > 0)
    {
        ctx.Response.Headers["X-Condux-Quota-Remaining"] = quota.Remaining.ToString(CultureInfo.InvariantCulture);
    }
    if (!quota.Allowed)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.Response.WriteAsJsonAsync(new { error = "quota_exceeded" });
        return;
    }

    // 5. Parse → scrub → publish. Read bytes, not text: envelope items are length-delimited and an
    // attachment payload is raw binary, so decoding the whole body to a string first corrupts it.
    var body = await ReadBoundedAsync(ctx.Request.Body, maxIngestBytes, ctx.RequestAborted);
    if (body is null)
    {
        // What the body decompressed to ran past CONDUX_MAX_INGEST_BYTES. Answered rather than thrown so
        // a hostile payload reads as a client error, and refused before it is ever held in full.
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" });
        return;
    }

    var result = parse(body);

    if (result.Outcome == ParseOutcome.Malformed)
    {
        // Answered explicitly rather than folded into the no-event case below: reporting an unparseable
        // body as success loses the event with no signal on either side.
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid_payload" });
        return;
    }

    if (result.Event is not { } normalized)
    {
        // A valid envelope that carried no event item (e.g. only a transaction/session). Accepted, but
        // there is nothing for an error monitor to store.
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.WriteAsJsonAsync(new { id = (string?)null });
        return;
    }

    var scrubbed = EventScrubber.Scrub(normalized);
    // Carry the tier's retention so the consumer can stamp it on the ClickHouse row (column-driven TTL).
    await publisher.PublishAsync(projectKey, scrubbed, limits.RetentionDays, ctx.RequestAborted);

    app.Logger.LogInformation(
        "ingest project={ProjectId} event={EventId} remaining={Remaining}",
        projectKey, scrubbed.EventId, decision.Remaining);

    // 200, not 202: the Sentry SDKs read the event id back only from a 200 (sentry-dart's HttpTransport
    // parses the body on that status alone), and Sentry's own ingest answers 200 here.
    ctx.Response.StatusCode = StatusCodes.Status200OK;
    await ctx.Response.WriteAsJsonAsync(new { id = scrubbed.EventId });
}

// Classic Sentry single-event JSON (still emitted by the first-party SDKs and older Sentry SDKs).
app.MapPost("/api/{projectId}/store/",
    (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
        SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
    IngestAsync(projectId, ctx, limiter, quotaMeter, spike, projects, publisher,
        body => SentryParser.ParseStore(body)));

// Modern Sentry SDKs POST a newline-delimited envelope here (not /store/); we take its first event item.
// Same auth + limits + pipeline, so a stock Sentry SDK works by only swapping the DSN.
app.MapPost("/api/{projectId}/envelope/",
    (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
        SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
    IngestAsync(projectId, ctx, limiter, quotaMeter, spike, projects, publisher,
        body => SentryParser.ParseEnvelope(body)));

// Native OTLP/HTTP logs ingestion (#79, ADR-0025): an app instrumented with OpenTelemetry reports errors
// by pointing its OTLP logs exporter at `<relay>/api/{projectId}` (the exporter appends `/v1/logs`). JSON
// and protobuf encodings both. One OTLP export is one request for auth/rate/spike;
// each error LogRecord in the batch is one event for quota. Being an error monitor, only error-or-worse
// records (or those carrying an exception) become issues; lower-severity logs are accepted but not stored.
app.MapPost("/api/{projectId}/v1/logs",
    async (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
        SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
{
    // Decided before the first refusal rather than just before the parse: every failure below has to be
    // answered in the encoding the request arrived in, including the ones that never read the body.
    var isProtobuf = (ctx.Request.ContentType ?? "")
        .Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase);

    var publicKey = ExtractPublicKey(ctx);
    var project = publicKey is null
        ? null
        : await projects.AuthenticateAsync(projectId, publicKey, ctx.RequestAborted);
    if (project is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await WriteOtlpErrorAsync(ctx, isProtobuf, OtlpStatusCode.Unauthenticated, "the DSN was rejected");
        return;
    }

    var projectKey = project.Id;
    var limits = PlanCatalog.For(project.Tier);

    // One OTLP export = one request for the rate + spike gates (batch size is metered per-event below).
    var decision = await limiter.CheckAsync(projectKey, limits.RatePerSecond, limits.Burst, ctx.RequestAborted);
    ctx.Response.Headers["X-Condux-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
    if (!decision.Allowed)
    {
        ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await WriteOtlpErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
            "the project's ingest rate limit was reached");
        return;
    }

    var spikeDecision = spike.Check();
    if (!spikeDecision.Allowed)
    {
        ctx.Response.Headers.RetryAfter = spikeDecision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await WriteOtlpErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
            "the receiver is shedding load, retry after the interval given");
        return;
    }

    // Bounded like the Sentry path: UseRequestDecompression has already expanded the body, and Kestrel's
    // own limit bounds the compressed request, so an unbounded read here has no ceiling at all.
    var body = await ReadBoundedAsync(ctx.Request.Body, maxIngestBytes, ctx.RequestAborted);
    if (body is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await WriteOtlpErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
            "the export exceeded the configured size limit");
        return;
    }

    var decoded = isProtobuf ? OtlpLogs.ParseProtobuf(body) : OtlpLogs.ParseJson(body);
    if (!decoded.IsSuccess)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await WriteOtlpErrorAsync(ctx, isProtobuf, OtlpStatusCode.InvalidArgument,
            "the export did not decode");
        return;
    }

    var parsed = OtlpEventMapper.ToEvents(decoded.Value);

    long rejected = 0;
    var accepted = 0;
    foreach (var normalized in parsed)
    {
        // Error monitoring: only error-or-worse records (or ones carrying an exception) are stored.
        if (normalized.Level < Level.Error && normalized.Exceptions.Count == 0)
        {
            continue;
        }

        var quota = await quotaMeter.TryConsumeAsync(projectKey, limits.MonthlyEvents, ctx.RequestAborted);
        if (limits.MonthlyEvents > 0)
        {
            ctx.Response.Headers["X-Condux-Quota-Remaining"] = quota.Remaining.ToString(CultureInfo.InvariantCulture);
        }
        if (!quota.Allowed)
        {
            rejected++;
            continue;
        }

        var scrubbed = EventScrubber.Scrub(normalized);
        await publisher.PublishAsync(projectKey, scrubbed, limits.RetentionDays, ctx.RequestAborted);
        accepted++;
    }

    app.Logger.LogInformation(
        "otlp ingest project={ProjectId} accepted={Accepted} rejected={Rejected}", projectKey, accepted, rejected);

    // OTLP/HTTP success is 200 with an ExportLogsServiceResponse, encoded the way the request was.
    // partialSuccess is set only when the monthly quota rejected some of the batch's error records.
    var response = new ExportLogsServiceResponse();
    if (rejected > 0)
    {
        response.PartialSuccess = new ExportLogsPartialSuccess
        {
            RejectedLogRecords = rejected,
            ErrorMessage = "quota_exceeded",
        };
    }

    ctx.Response.StatusCode = StatusCodes.Status200OK;
    await WriteOtlpAsync(ctx, isProtobuf, response);
});

app.Run();

static double ParseDouble(string? value, double fallback) =>
    double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

static long ParseLong(string? value, long fallback) =>
    long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

// Public key from the Condux SDK header, the Sentry X-Sentry-Auth header, or ?sentry_key.
/// <summary>Reads the request body, refusing it the moment it passes <paramref name="limit"/> bytes;
/// null means it did. Bounds the DECOMPRESSED size, which a server request-size limit cannot: a zip bomb
/// is tiny on the wire and only becomes large after the decompression middleware has run.</summary>
// OTLP requires a server to answer in the content type it was sent, so the encoding is chosen from the
// request rather than fixed. A full success is an empty message in both encodings.
static async Task WriteOtlpAsync(HttpContext ctx, bool isProtobuf, ExportLogsServiceResponse response)
{
    if (isProtobuf)
    {
        ctx.Response.ContentType = "application/x-protobuf";
        await ctx.Response.Body.WriteAsync(response.ToProtobuf(), ctx.RequestAborted);
        return;
    }

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(response.ToJson(), ctx.RequestAborted);
}

// The protocol's failure body is a Status message, which we do not encode, so a protobuf caller gets the
// status code and no body. Answering with JSON labelled as protobuf would be worse than answering with
// nothing.
// The protocol requires the body of every 4xx and 5xx to be a google.rpc.Status describing the problem,
// in the content type the request arrived in. Both encodings used to break that: protobuf got an empty
// body, and JSON got this service's own {"error": "..."} shape, which is not a Status.
//
// Do not "correct" the JSON branch to write protobuf. The spec's Failures section words this as a
// "Protobuf-encoded Status message", under a heading that is not encoding-specific, which reads as
// binary until you notice it contradicts the same-content-type rule stated a few lines earlier: obeying
// it literally would answer application/json with binary bytes. The OpenTelemetry Collector settles it,
// picking its encoder from the request's Content-Type and marshalling the Status with that, so a JSON
// request gets a JSON Status. That is what this does.
//
// The code is not a retry instruction. A sender decides that from the HTTP status, so a 400 is never
// retried whatever code rides in the body. It is here to make the failure legible to whoever is reading
// their exporter's logs while nothing arrives.
static async Task WriteOtlpErrorAsync(HttpContext ctx, bool isProtobuf, OtlpStatusCode code, string message)
{
    var status = new OtlpStatus { Code = (int)code, Message = message };
    if (isProtobuf)
    {
        ctx.Response.ContentType = "application/x-protobuf";
        await ctx.Response.Body.WriteAsync(status.ToProtobuf(), ctx.RequestAborted);
        return;
    }

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(status.ToJson(), ctx.RequestAborted);
}

static async Task<byte[]?> ReadBoundedAsync(Stream body, long limit, CancellationToken ct)
{
    var rented = ArrayPool<byte>.Shared.Rent(81920);
    try
    {
        using var buffer = new MemoryStream();
        int read;
        while ((read = await body.ReadAsync(rented, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null; // refused before the whole thing is ever materialized
            }
            await buffer.WriteAsync(rented.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rented);
    }
}

static string? ExtractPublicKey(HttpContext ctx)
{
    if (ctx.Request.Headers.TryGetValue("x-condux-auth", out var condux) && !string.IsNullOrEmpty(condux))
    {
        return condux.ToString();
    }
    if (ctx.Request.Headers.TryGetValue("X-Sentry-Auth", out var sentry))
    {
        foreach (var part in sentry.ToString().Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().EndsWith("sentry_key", StringComparison.OrdinalIgnoreCase))
            {
                return kv[1].Trim();
            }
        }
    }
    if (ctx.Request.Query.TryGetValue("sentry_key", out var q) && !string.IsNullOrEmpty(q))
    {
        return q.ToString();
    }
    return null;
}

// Exposed so integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program { }
