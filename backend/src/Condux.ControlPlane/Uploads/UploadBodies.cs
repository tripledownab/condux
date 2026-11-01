using System.Diagnostics.CodeAnalysis;
using Condux.Core.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Condux.ControlPlane.Uploads;

/// <summary>
/// Reading a CI upload's body, for every route that takes one: under the shared <see cref="UploadBodyBudget"/>
/// and, on HTTP/1.x, no slower than <see cref="MinRate"/>, so a sender holds only the bytes it has sent and
/// the piece it is sending into, and only for as long as they take at that rate. Anything an upload route limits to a number
/// of places is entered after this, with the body already read, so a place is held for the server's own work
/// and never for a sender's.
/// </summary>
internal static class UploadBodies
{
    // Well under what a CI runner sends. Kestrel measures the rate over the time spent waiting on reads, from
    // the first one, and checks nothing within the grace, so a body may take the grace plus as long as its
    // size needs at this rate.
    internal static readonly MinDataRate MinRate = new(bytesPerSecond: 256 * 1024, gracePeriod: TimeSpan.FromSeconds(10));

    /// <summary>The body, held until the caller disposes it, or the answer for one that was not read whole.
    /// Each refusal is the caller's to fix or to retry, so none is raised as an error of ours.</summary>
    public static async Task<UploadRead> ReadAsync(HttpContext http, long maxBytes)
    {
        // Kestrel's own request limit is replaced by this route's, where the host allows it: below the cap it
        // would refuse a debug file the route accepts. The read is bounded either way, since some hosts offer
        // no such feature.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }
        SetMinRate(http);

        BodyRead read;
        try
        {
            read = await RequestBodies.ReadHeldAsync(http.Request.Body, maxBytes,
                http.RequestServices.GetRequiredService<UploadBodyBudget>(), http.RequestAborted);
        }
        catch (BadHttpRequestException e)
        {
            // Kestrel says the body itself was wrong: past its limit (413, possibly before any byte is read,
            // when a declared length is too large), cut short (400) or sent slower than MinRate (408).
            // Answered with Kestrel's own status.
            return new UploadRead(null, TypedResults.StatusCode(e.StatusCode));
        }

        switch (read.Refusal)
        {
            case BodyRefusal.None:
                return new UploadRead(read.Body, null);
            case BodyRefusal.TooLarge:
                return new UploadRead(null, TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));
            case BodyRefusal.Undecodable:
                return new UploadRead(null, TypedResults.StatusCode(StatusCodes.Status400BadRequest));
            case BodyRefusal.OverBudget:
                http.Response.Headers.RetryAfter = "5";
                return new UploadRead(null, TypedResults.StatusCode(StatusCodes.Status429TooManyRequests));
            default:
                throw new InvalidOperationException($"no answer for {read.Refusal}");
        }
    }

    // Kestrel's own rate, not a timer of ours: cancelling a body read part way leaves Kestrel's reader mid-read,
    // so it logs an error and drops the connection when it then drains the body, and a stranger could write
    // that error to our log at will. Kestrel takes a per-request rate on HTTP/1.x only (an HTTP/2 or HTTP/3
    // stream refuses one); over plain http, which is how the control-plane listens, Kestrel speaks nothing
    // else, so HTTP/2 here would take TLS or an HTTP/2-only listener, and then the server's own default rate
    // applies. Kestrel reads the rate when the body is first read and ignores a later one without a word, so
    // this has to run before anything reads the body: buffering, decompression or a bound body parameter in
    // front of these routes would quietly take the rate away.
    private static void SetMinRate(HttpContext http)
    {
        if (HttpProtocol.IsHttp11(http.Request.Protocol) || HttpProtocol.IsHttp10(http.Request.Protocol))
        {
            if (http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
            {
                rate.MinDataRate = MinRate;
            }
        }
    }
}

/// <summary>An upload's body, held until disposed, or the answer for one that was not read whole.</summary>
internal readonly record struct UploadRead(HeldBody? Body, StatusCodeHttpResult? Refusal)
{
    [MemberNotNullWhen(true, nameof(Refusal))]
    [MemberNotNullWhen(false, nameof(Body))]
    public bool IsRefused => Refusal is not null;
}
