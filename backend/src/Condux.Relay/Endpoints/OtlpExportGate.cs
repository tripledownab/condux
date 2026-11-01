using System.Globalization;
using Condux.Core.Http;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.RateLimiting;
using Condux.Relay.Ingest;
using Condux.Relay.Setup;
using OtlpStatusCode = Condux.Otlp.StatusCode;

namespace Condux.Relay.Endpoints;

/// <summary>What an OTLP export has to get past before it is decoded, shared by the logs and traces
/// endpoints so the two cannot differ in who they admit: the encoding it arrived in, the DSN, the project's
/// rate and the receiver's load shedding, and a bounded read of the body. Every refusal is answered here as
/// a Status in the request's own encoding, including the ones that never read the body.</summary>
internal static class OtlpExportGate
{
    /// <summary>The admitted export, or null once the refusal has been written. The caller disposes it when
    /// the request is answered, which gives the sender's share of the body budget back.</summary>
    public static async Task<OtlpAdmission?> AdmitAsync(
        HttpContext ctx, RelayOptions options, string projectId,
        IProjectStore projects, IRateLimiter limiter, SpikeGuard spike)
    {
        // Decided before the first refusal rather than just before the parse: every failure below has to be
        // answered in the encoding the request arrived in, including the ones that never read the body.
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
            return null;
        }

        var limits = PlanCatalog.For(project.Tier);

        // One OTLP export = one request for the rate + spike gates. The records inside it are metered
        // against the monthly quota by the caller, in a single call for the whole batch.
        var decision = await limiter.CheckAsync(project.Id, limits.RatePerSecond, limits.Burst, ctx.RequestAborted);
        ctx.Response.Headers["X-Condux-RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        if (!decision.Allowed)
        {
            ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
                "the project's ingest rate limit was reached");
            return null;
        }

        var spikeDecision = spike.Check();
        if (!spikeDecision.Allowed)
        {
            ctx.Response.Headers.RetryAfter = spikeDecision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, OtlpStatusCode.ResourceExhausted,
                "the receiver is shedding load, retry after the interval given");
            return null;
        }

        // Bounded like the Sentry path: UseRequestDecompression has already expanded the body, and the
        // host's limit applies to it only where the host has one, so the read bounds it again. Read before
        // the quota is asked, so a refusal here has spent none.
        var read = await IngestBodies.ReadAsync(ctx, options, project);
        if (read.Body is null)
        {
            ctx.Response.StatusCode = IngestBodies.ToStatus(read.Refusal);
            var (code, message) = read.Refusal switch
            {
                BodyRefusal.TooLarge => (OtlpStatusCode.ResourceExhausted, "the export exceeded the configured size limit"),
                BodyRefusal.Undecodable => (OtlpStatusCode.InvalidArgument, "the export did not decompress as its Content-Encoding declares"),
                _ => (OtlpStatusCode.ResourceExhausted, "the receiver is holding too many exports, retry after the interval given"),
            };
            await OtlpResponses.WriteErrorAsync(ctx, isProtobuf, code, message);
            read.Dispose();
            return null;
        }

        return new OtlpAdmission(project, limits, isProtobuf, read);
    }
}

/// <summary>An export that passed <see cref="OtlpExportGate"/>: who sent it, what their plan allows, the
/// encoding to answer in, and the body, held until this is disposed.</summary>
internal sealed class OtlpAdmission(Project project, Limits limits, bool isProtobuf, IngestRead read) : IDisposable
{
    public Project Project => project;

    public Limits Limits => limits;

    public bool IsProtobuf => isProtobuf;

    public byte[] Content => read.Body!.Content;

    public void Dispose() => read.Dispose();
}
