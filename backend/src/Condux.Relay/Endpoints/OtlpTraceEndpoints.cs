using Condux.Core.Messaging;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Condux.Otlp;
using Condux.Relay.Ingest;
using Condux.Relay.Setup;
using OtlpStatusCode = Condux.Otlp.StatusCode;

namespace Condux.Relay.Endpoints;

/// <summary>OTLP/HTTP traces ingestion: an app instrumented with OpenTelemetry, an AI agent's
/// framework above all, reports a failure as an error span, and the exporter appends `/v1/traces` to the
/// same `&lt;relay&gt;/api/{projectId}` base the logs exporter uses. Auth, limits, the size cap, the
/// quota call and the error format are the logs endpoint's own, through <see cref="OtlpExportGate"/> and
/// <see cref="OtlpEventPublisher"/>. Only failed spans become events (see <see cref="OtlpSpanMapper"/>);
/// the rest of an export is accepted and spends no quota.</summary>
internal static class OtlpTraceEndpoints
{
    public static void MapOtlpTraceEndpoints(this WebApplication app, RelayOptions options)
    {
        var logger = app.Logger;

        app.MapPost("/api/{projectId}/v1/traces",
            async (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
                SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
        {
            using var export = await OtlpExportGate.AdmitAsync(ctx, options, projectId, projects, limiter, spike);
            if (export is null)
            {
                return;
            }

            var decoded = export.IsProtobuf
                ? OtlpTraces.ParseProtobuf(export.Content)
                : OtlpTraces.ParseJson(export.Content);
            if (!decoded.IsSuccess)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await OtlpResponses.WriteErrorAsync(ctx, export.IsProtobuf, OtlpStatusCode.InvalidArgument,
                    "the export did not decode");
                return;
            }

            // Built up to the per-export ceiling only: the export's size is the sender's to choose.
            var failures = OtlpSpanMapper.ToEvents(decoded.Value, options.MaxOtlpRecords, out var surplus);
            var result = await OtlpEventPublisher.PublishAsync(
                ctx, export.Project, export.Limits, failures, options, quotaMeter, publisher, surplus);
            logger.LogInformation(
                "otlp traces ingest project={ProjectId} accepted={Accepted} overQuota={OverQuota} overflow={Overflow}",
                export.Project.Id, result.Admitted, result.OverQuota, result.Overflow);

            // rejected_spans counts both reasons a failed span can be refused, as the logs endpoint's
            // count does, and errorMessage says which.
            var response = new ExportTraceServiceResponse();
            if (result.Rejected > 0)
            {
                response.PartialSuccess = new ExportTracePartialSuccess
                {
                    RejectedSpans = result.Rejected,
                    ErrorMessage = OtlpEventPublisher.RejectionReason(result, "failed spans", "spans"),
                };
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await OtlpResponses.WriteAsync(ctx, export.IsProtobuf, response);
        });
    }
}
