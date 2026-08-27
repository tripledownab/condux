using System.Globalization;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Condux.Core.Scrub;
using Condux.Otlp;
using Condux.Relay.Ingest;
using Condux.Relay.Setup;
using OtlpStatusCode = Condux.Otlp.StatusCode;

namespace Condux.Relay.Endpoints;

/// <summary>Native OTLP/HTTP logs ingestion (#79, ADR-0025): an app instrumented with OpenTelemetry
/// reports errors by pointing its OTLP logs exporter at `&lt;relay&gt;/api/{projectId}` (the exporter
/// appends `/v1/logs`), in either the JSON or the protobuf encoding. One OTLP export is one request for
/// auth, rate and spike; each error LogRecord in the batch is one event for quota. Being an error
/// monitor, only error-or-worse records (or those carrying an exception) become issues; lower-severity
/// logs are accepted but not stored.</summary>
internal static class OtlpIngestEndpoints
{
    public static void MapOtlpIngestEndpoints(this WebApplication app, RelayOptions options)
    {
        var logger = app.Logger;

        app.MapPost("/api/{projectId}/v1/logs",
            async (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
                SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
        {
            // Decided before the first refusal rather than just before the parse: every failure below has
            // to be answered in the encoding the request arrived in, including the ones that never read
            // the body.
            var isProtobuf = (ctx.Request.ContentType ?? "")
                .Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase);

            var publicKey = IngestRequest.ExtractPublicKey(ctx);
            var project = publicKey is null
                ? null
                : await projects.AuthenticateAsync(projectId, publicKey, ctx.RequestAborted);
            if (project is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await OtlpResponses.WriteErrorAsync(
                    ctx, isProtobuf, OtlpStatusCode.Unauthenticated, "the DSN was rejected");
                return;
            }

            var projectKey = project.Id;
            var limits = PlanCatalog.For(project.Tier);

            // One OTLP export = one request for the rate + spike gates (batch size is metered per-event
            // below).
            var decision = await limiter.CheckAsync(projectKey, limits.RatePerSecond, limits.Burst, ctx.RequestAborted);
            ctx.Response.Headers["X-Condux-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
            if (!decision.Allowed)
            {
                ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
                    "the project's ingest rate limit was reached");
                return;
            }

            var spikeDecision = spike.Check();
            if (!spikeDecision.Allowed)
            {
                ctx.Response.Headers.RetryAfter = spikeDecision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
                    "the receiver is shedding load, retry after the interval given");
                return;
            }

            // Bounded like the Sentry path: UseRequestDecompression has already expanded the body, and
            // Kestrel's own limit bounds the compressed request, so an unbounded read here has no ceiling
            // at all.
            var body = await IngestRequest.ReadBoundedAsync(ctx.Request.Body, options.MaxIngestBytes, ctx.RequestAborted);
            if (body is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
                    "the export exceeded the configured size limit");
                return;
            }

            var decoded = isProtobuf ? OtlpLogs.ParseProtobuf(body) : OtlpLogs.ParseJson(body);
            if (!decoded.IsSuccess)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, OtlpStatusCode.InvalidArgument,
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

            logger.LogInformation(
                "otlp ingest project={ProjectId} accepted={Accepted} rejected={Rejected}",
                projectKey, accepted, rejected);

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
            await OtlpResponses.WriteAsync(ctx, isProtobuf, response);
        });
    }
}
