using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
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
/// quota or by that ceiling, rides back in <c>partialSuccess</c> rather than failing the export. What an
/// export must pass before it is decoded is <see cref="OtlpExportGate"/>, and what happens to the events
/// after is <see cref="OtlpEventPublisher"/>; both are shared with the traces endpoint.</summary>
internal static class OtlpIngestEndpoints
{
    public static void MapOtlpIngestEndpoints(this WebApplication app, RelayOptions options)
    {
        var logger = app.Logger;

        app.MapPost("/api/{projectId}/v1/logs",
            async (string projectId, HttpContext ctx, IRateLimiter limiter, IQuotaMeter quotaMeter,
                SpikeGuard spike, IProjectStore projects, IEventPublisher publisher) =>
        {
            using var export = await OtlpExportGate.AdmitAsync(ctx, options, projectId, projects, limiter, spike);
            if (export is null)
            {
                return;
            }

            var decoded = export.IsProtobuf ? OtlpLogs.ParseProtobuf(export.Content) : OtlpLogs.ParseJson(export.Content);
            if (!decoded.IsSuccess)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await OtlpResponses.WriteErrorAsync(ctx, export.IsProtobuf, OtlpStatusCode.InvalidArgument,
                    "the export did not decode");
                return;
            }

            // Error monitoring: only error-or-worse records (or ones carrying an exception) are stored, so
            // those are the only ones the quota is asked about.
            var storable = OtlpEventMapper.ToEvents(decoded.Value)
                .Where(e => e.Level >= Level.Error || e.Exceptions.Count > 0)
                .ToList();

            var result = await OtlpEventPublisher.PublishAsync(
                ctx, export.Project, export.Limits, storable, options, quotaMeter, publisher);
            logger.LogInformation(
                "otlp ingest project={ProjectId} accepted={Accepted} overQuota={OverQuota} overflow={Overflow}",
                export.Project.Id, result.Admitted, result.OverQuota, result.Overflow);

            // OTLP/HTTP success is 200 with an ExportLogsServiceResponse, encoded the way the request was.
            // rejected_log_records counts both reasons a record can be refused, since the field is defined
            // as the number rejected and says nothing about why; errorMessage is where the why belongs.
            var response = new ExportLogsServiceResponse();
            if (result.Rejected > 0)
            {
                response.PartialSuccess = new ExportLogsPartialSuccess
                {
                    RejectedLogRecords = result.Rejected,
                    ErrorMessage = OtlpEventPublisher.RejectionReason(result, "error records", "records"),
                };
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await OtlpResponses.WriteAsync(ctx, export.IsProtobuf, response);
        });
    }
}
