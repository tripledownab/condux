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
/// auth, rate and spike, and one quota check for however many storable records it carries, up to
/// <c>MaxOtlpRecords</c>. Being an error monitor, only error-or-worse records (or those carrying an
/// exception) become issues; lower-severity logs are accepted but not stored. Whatever is refused, by the
/// quota or by that ceiling, rides back in <c>partialSuccess</c> rather than failing the export.</summary>
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

            // One OTLP export = one request for the rate + spike gates. The records inside it are metered
            // against the monthly quota below, in a single call for the whole batch.
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

            // Error monitoring: only error-or-worse records (or ones carrying an exception) are stored, so
            // those are the only ones the quota is asked about. The list stops at MaxOtlpRecords, because
            // the body cap bounds the bytes of an export and not the records inside them; RelayOptions
            // carries the arithmetic.
            var storable = new List<Event>();
            long overflow = 0;
            foreach (var normalized in parsed)
            {
                if (normalized.Level < Level.Error && normalized.Exceptions.Count == 0)
                {
                    continue;
                }
                if (storable.Count >= options.MaxOtlpRecords)
                {
                    overflow++;
                    continue;
                }
                storable.Add(normalized);
            }

            long admitted = 0;
            if (storable.Count > 0)
            {
                // One quota call for the whole batch. The record count is the sender's to choose, so asking
                // per record made the amount of metering a stranger's to size, and against the Valkey meter
                // that is a network round trip each.
                var quota = await quotaMeter.TryConsumeAsync(
                    projectKey, limits.MonthlyEvents, storable.Count, ctx.RequestAborted);
                if (limits.MonthlyEvents > 0)
                {
                    ctx.Response.Headers["X-Condux-Quota-Remaining"] = quota.Remaining.ToString(CultureInfo.InvariantCulture);
                }
                admitted = quota.Admitted;
            }

            // A batch that straddles the quota spends what is left of it: the leading records are stored and
            // the rest ride back as partialSuccess, which is what the per-record loop did before.
            //
            // Quota is taken for the whole batch before the first publish, and taking it cannot be undone
            // by this request, so a publish that throws part way has spent the remainder on nothing. The
            // per-record loop could only ever lose one event that way; a batch can lose as many as it
            // took, which is what makes the refund worth a round trip on a path that is already failing.
            var published = 0;
            try
            {
                for (; published < storable.Count && published < admitted; published++)
                {
                    var scrubbed = EventScrubber.Scrub(storable[published], project.UserKeySalt);
                    await publisher.PublishAsync(projectKey, scrubbed, limits.RetentionDays, ctx.RequestAborted);
                }
            }
            catch
            {
                // Refund only what was admitted and not stored. An unlimited tier counted nothing, so it is
                // owed nothing. CancellationToken.None rather than ctx.RequestAborted: a disconnect is one
                // of the ways to get here, and passing the token that just fired would cancel the repair
                // for the failure that caused it. Nothing is swallowed, the exception carries on.
                var unused = admitted - published;
                if (limits.MonthlyEvents > 0 && unused > 0)
                {
                    await quotaMeter.RefundAsync(projectKey, unused, CancellationToken.None);
                }
                throw;
            }

            var overQuota = storable.Count - admitted;
            var rejected = overQuota + overflow;
            logger.LogInformation(
                "otlp ingest project={ProjectId} accepted={Accepted} overQuota={OverQuota} overflow={Overflow}",
                projectKey, admitted, overQuota, overflow);

            // OTLP/HTTP success is 200 with an ExportLogsServiceResponse, encoded the way the request was.
            // rejected_log_records counts both reasons a record can be refused, since the field is defined
            // as the number rejected and says nothing about why; errorMessage is where the why belongs.
            var response = new ExportLogsServiceResponse();
            if (rejected > 0)
            {
                response.PartialSuccess = new ExportLogsPartialSuccess
                {
                    RejectedLogRecords = rejected,
                    ErrorMessage = RejectionReason(overQuota, overflow),
                };
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await OtlpResponses.WriteAsync(ctx, isProtobuf, response);
        });
    }

    /// <summary>
    /// Says why records were refused, and what to do about it. Prose rather than a token because
    /// logs_service.proto defines error_message as "a developer-facing human-readable message in English"
    /// that "should offer guidance on how users can address such issues"; the machine-readable part of a
    /// partial success is rejected_log_records. This replaces the token "quota_exceeded", which was the
    /// same kind of invention as the non-conformant JSON error body already removed from this endpoint,
    /// and which read oddly beside the Status messages OtlpResponses writes on the same endpoint. Those
    /// are lowercase fragments naming the problem, which is the shape followed here; the guidance half
    /// comes from the spec, and only one of them carries any ("retry after the interval given").
    /// The two reasons stay distinct: a sender told its quota ran out when its batch was simply too large
    /// would go on paying for a plan that was never the problem.
    /// </summary>
    private static string RejectionReason(long overQuota, long overflow) => (overQuota, overflow) switch
    {
        ( > 0, > 0) => "the project's monthly event quota is exhausted and the export carried more error "
            + "records than one request accepts, so raise the plan's event limit and lower the exporter's "
            + "batch size",
        ( > 0, _) => "the project's monthly event quota is exhausted, so raise the plan's event limit or "
            + "export fewer records",
        _ => "the export carried more error records than one request accepts, so lower the exporter's "
            + "batch size",
    };
}
