using Condux.Core.Http;
using Condux.Core.Projects;
using Condux.Relay.Setup;

namespace Condux.Relay.Ingest;

/// <summary>Reading an ingest body, for the Sentry and the OTLP endpoints alike: bounded by
/// CONDUX_MAX_INGEST_BYTES, charged to the sender's share of the relay's <see cref="IngestBodyBudget"/>,
/// and aborted if it stops arriving (<see cref="StalledBodyWatch"/>). Each endpoint answers a refusal in
/// its own wire format; what the refusal is, and when to retry it, is decided here.</summary>
internal static class IngestBodies
{
    /// <summary>The body, or why it was refused, holding the sender's share until the caller disposes it.
    /// Over the budget, or over the sender's share of it, sets a short Retry-After: an SDK honouring it holds
    /// back every event for that long, and the room frees as soon as the bodies holding it are answered.
    /// </summary>
    public static async Task<IngestRead> ReadAsync(HttpContext ctx, RelayOptions options, Project project)
    {
        var budget = ctx.RequestServices.GetRequiredService<IngestBodyBudget>();
        var lease = budget.LeaseFor(project.QuotaKey, IngestSenders.ToAddress(ctx, options.TrustedProxies));
        try
        {
            BodyRead read;
            await using (var watch = new StalledBodyWatch(ctx, ctx.RequestServices.GetRequiredService<TimeProvider>()))
            {
                read = await RequestBodies.ReadHeldAsync(watch.Body, options.MaxIngestBytes, lease.Budget,
                    ctx.RequestAborted);
            }
            if (read.Refusal == BodyRefusal.OverBudget)
            {
                ctx.Response.Headers.RetryAfter = "1";
            }
            return new IngestRead(read.Body, read.Refusal, lease);
        }
        // The host holds the body to the same cap (Kestrel's MaxRequestBodySize, set from it) and refuses
        // past it by throwing: Kestrel for a plain body, the decompression middleware for the expanded bytes
        // of a compressed one. On a host with a size limit that is how every body past the cap ends, so it is
        // this read's refusal like the one above, answered in each endpoint's own format.
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new IngestRead(null, BodyRefusal.TooLarge, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>The status a refused body is answered with: 413 for one past the cap, which the sender has
    /// to make smaller, 400 for one that did not decode as its Content-Encoding declared, which the sender
    /// has to fix, and 429 for one the budget had no room for, which the same request can retry.</summary>
    public static int ToStatus(BodyRefusal refusal) => refusal switch
    {
        BodyRefusal.TooLarge => StatusCodes.Status413PayloadTooLarge,
        BodyRefusal.Undecodable => StatusCodes.Status400BadRequest,
        BodyRefusal.OverBudget => StatusCodes.Status429TooManyRequests,
        _ => throw new InvalidOperationException($"no status for {refusal}"),
    };
}

/// <summary>An ingest body, or why it was refused, and the sender's share it holds. Disposing it
/// gives back the body's charge and then the share.</summary>
internal sealed class IngestRead(HeldBody? body, BodyRefusal refusal, IngestBodyBudget.ShareLease lease) : IDisposable
{
    public HeldBody? Body => body;

    public BodyRefusal Refusal => refusal;

    public void Dispose()
    {
        body?.Dispose();
        lease.Dispose();
    }
}
