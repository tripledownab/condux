using System.Globalization;
using Condux.Core.Ingest;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Condux.Core.Scrub;
using Condux.Relay.Ingest;
using Condux.Relay.Setup;

namespace Condux.Relay.Endpoints;

/// <summary>The Sentry-compatible ingest endpoints, so an existing SDK works by swapping the DSN only.
/// Both share one pipeline, which is the point: /store/ and /envelope/ differ by how the payload is
/// framed and by nothing else, so a gate added here cannot apply to one of them and not the other.
/// </summary>
internal static class SentryIngestEndpoints
{
    public static void MapSentryIngestEndpoints(this WebApplication app, RelayOptions options)
    {
        var logger = app.Logger;

        // Classic Sentry single-event JSON (still emitted by the first-party SDKs and older Sentry SDKs).
        app.MapPost("/api/{projectId}/store/",
            (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
                SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
            IngestAsync(projectId, ctx, limiter, quotaMeter, spike, projects, publisher, logger, options,
                body => SentryParser.ParseStore(body)));

        // Modern Sentry SDKs POST a newline-delimited envelope here (not /store/); we take its first
        // event item. Same auth + limits + pipeline, so a stock Sentry SDK works by only swapping the DSN.
        app.MapPost("/api/{projectId}/envelope/",
            (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
                SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
            IngestAsync(projectId, ctx, limiter, quotaMeter, spike, projects, publisher, logger, options,
                body => SentryParser.ParseEnvelope(body)));
    }

    // Shared ingest pipeline for the two Sentry-compatible endpoints: auth → per-tier rate-limit → spike
    // → per-tier monthly quota → parse → scrub → publish. `parse` returns null when the payload carries
    // no event item (an envelope of only sessions/transactions), which is accepted but stores nothing.
    private static async Task IngestAsync(
        string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter, SpikeGuard spike,
        IProjectStore projects, IEventPublisher publisher, ILogger logger, RelayOptions options,
        Func<byte[], ParseResult> parse)
    {
        // 1. Authenticate the DSN public key against the project.
        var publicKey = IngestRequest.ExtractPublicKey(ctx);
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

        // 3. Instance-wide spike protection, the last cheap gate before we parse.
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
        var body = await IngestRequest.ReadBoundedAsync(ctx.Request.Body, options.MaxIngestBytes, ctx.RequestAborted);
        if (body is null)
        {
            // What the body decompressed to ran past CONDUX_MAX_INGEST_BYTES. Answered rather than thrown
            // so a hostile payload reads as a client error, and refused before it is ever held in full.
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsJsonAsync(new { error = "payload_too_large" });
            return;
        }

        var result = parse(body);

        if (result.Outcome == ParseOutcome.Malformed)
        {
            // Answered explicitly rather than folded into the no-event case below: reporting an
            // unparseable body as success loses the event with no signal on either side.
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

        logger.LogInformation(
            "ingest project={ProjectId} event={EventId} remaining={Remaining}",
            projectKey, scrubbed.EventId, decision.Remaining);

        // 200, not 202: the Sentry SDKs read the event id back only from a 200 (sentry-dart's HttpTransport
        // parses the body on that status alone), and Sentry's own ingest answers 200 here.
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.WriteAsJsonAsync(new { id = scrubbed.EventId });
    }
}
