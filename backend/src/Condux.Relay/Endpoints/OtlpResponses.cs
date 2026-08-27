using Condux.Otlp;
// Aliased because OpenTelemetry.Trace exports its own Status and StatusCode. Any receiver that also uses
// the OpenTelemetry SDK hits the same clash, which is worth knowing before reaching for these.
using OtlpStatus = Condux.Otlp.Status;
using OtlpStatusCode = Condux.Otlp.StatusCode;

namespace Condux.Relay.Endpoints;

/// <summary>How the OTLP endpoint answers, success and failure alike. Separate from the endpoint because
/// the protocol's rules about the response are the part that is easy to get wrong, and they apply to
/// every exit from that handler including the refusals that never read the body.</summary>
internal static class OtlpResponses
{
    // OTLP requires a server to answer in the content type it was sent, so the encoding is chosen from
    // the request rather than fixed. A full success is an empty message in both encodings.
    public static async Task WriteAsync(HttpContext ctx, bool isProtobuf, ExportLogsServiceResponse response)
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

    // The protocol requires the body of every 4xx and 5xx to be a google.rpc.Status describing the
    // problem, in the content type the request arrived in. Both encodings used to break that: protobuf
    // got an empty body, and JSON got this service's own {"error": "..."} shape, which is not a Status.
    //
    // Do not "correct" the JSON branch to write protobuf. The spec's Failures section words this as a
    // "Protobuf-encoded Status message", under a heading that is not encoding-specific, which reads as
    // binary until you notice it contradicts the same-content-type rule stated a few lines earlier:
    // obeying it literally would answer application/json with binary bytes. The OpenTelemetry Collector
    // settles it, picking its encoder from the request's Content-Type and marshalling the Status with
    // that, so a JSON request gets a JSON Status. That is what this does.
    //
    // The code is not a retry instruction. A sender decides that from the HTTP status, so a 400 is never
    // retried whatever code rides in the body. It is here to make the failure legible to whoever is
    // reading their exporter's logs while nothing arrives.
    public static async Task WriteErrorAsync(
        HttpContext ctx, bool isProtobuf, OtlpStatusCode code, string message)
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
}
