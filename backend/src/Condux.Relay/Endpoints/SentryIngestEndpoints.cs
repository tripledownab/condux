using System.Globalization;
using Condux.Core.Http;
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
    // → per-tier monthly quota → parse → scrub → publish. A payload that carries no event (an envelope of
    // only sessions or transactions, or a transaction, feedback or CSP payload) is answered 200 once past
    // the gates, and stores nothing.
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

        // 4. Monthly event quota (plan tier), one allowance for the whole org. Enterprise is unlimited and
        // skips this. Only admitted events are counted, and only after the cheaper rate + spike gates, so
        // shed events never consume quota.
        var quota = await quotaMeter.TryConsumeAsync(project.QuotaKey, limits.MonthlyEvents, ct: ctx.RequestAborted);
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
        using var read = await ReadOrRefundAsync(ctx, options, project, limits, quotaMeter);
        var held = read.Body;
        if (held is null)
        {
            // Past CONDUX_MAX_INGEST_BYTES once decompressed, not the encoding it was sent as, or no room in
            // the relay's body budget. Answered rather than thrown so a hostile payload reads as a client
            // error, and refused before it is ever held in full. The budget's 429 is the relay's own lack of
            // room, not the sender's error, and stores nothing, so the event step 4 counted is given back, or
            // a sender that sends it again pays twice; no token, for the reason the no-event branch below
            // gives. The other two are the sender's, like a malformed payload, and keep it.
            if (read.Refusal == BodyRefusal.OverBudget && limits.MonthlyEvents > 0)
            {
                await quotaMeter.RefundAsync(project.QuotaKey, 1, CancellationToken.None);
            }
            ctx.Response.StatusCode = IngestBodies.ToStatus(read.Refusal);
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = read.Refusal switch
                {
                    BodyRefusal.TooLarge => "payload_too_large",
                    BodyRefusal.Undecodable => "invalid_payload",
                    _ => "ingest_busy",
                },
            });
            return;
        }

        var result = parse(held.Content);

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
            // A valid payload that carried no event (an envelope of only a transaction or a session, or a
            // transaction, feedback or CSP payload). Accepted, but there is nothing for an error monitor to store,
            // so the event step 4 counted is given back. No token: a client that hangs up must not keep it.
            // On a capped tier whose consume was metered, the quota header written at step 4 typically reads
            // one fewer than the org now has left.
            if (limits.MonthlyEvents > 0)
            {
                await quotaMeter.RefundAsync(project.QuotaKey, 1, CancellationToken.None);
            }
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsJsonAsync(new { id = (string?)null });
            return;
        }

        var scrubbed = EventScrubber.Scrub(normalized, project.UserKeySalt);
        // Carry the tier's retention so the consumer can stamp it on the ClickHouse row (column-driven TTL),
        // and the sender's sent_at so it can correct the sender's clock drift.
        await publisher.PublishAsync(projectKey, scrubbed, limits.RetentionDays, result.SentAtUnixMs, ctx.RequestAborted);

        logger.LogInformation(
            "ingest project={ProjectId} event={EventId} remaining={Remaining}",
            projectKey, scrubbed.EventId, decision.Remaining);

        // 200, not 202: the Sentry SDKs read the event id back only from a 200 (sentry-dart's HttpTransport
        // parses the body on that status alone), and Sentry's own ingest answers 200 here.
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.WriteAsJsonAsync(new { id = scrubbed.EventId });
    }

    // A body that never arrives whole (the sender went away, Kestrel refused it as malformed, or it stopped
    // arriving and was aborted) stores nothing, so the event step 4 counted is given back, with no token for the reason
    // the no-event branch gives. The exception carries on.
    private static async Task<IngestRead> ReadOrRefundAsync(
        HttpContext ctx, RelayOptions options, Project project, Limits limits, IQuotaMeter quotaMeter)
    {
        try
        {
            return await IngestBodies.ReadAsync(ctx, options, project);
        }
        catch when (limits.MonthlyEvents > 0)
        {
            await quotaMeter.RefundAsync(project.QuotaKey, 1, CancellationToken.None);
            throw;
        }
    }
}
