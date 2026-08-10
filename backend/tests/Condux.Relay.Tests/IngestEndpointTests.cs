using System.IO.Compression;
using System.Net;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.Relay.Tests;

public class IngestEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IngestEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // The dev project store seeds project "1" with public key "devkey".
    private static HttpRequestMessage StorePost(string projectId, string body, string? key = "devkey")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/store/")
        {
            Content = new StringContent(body),
        };
        if (key is not null)
        {
            req.Headers.Add("x-condux-auth", key);
        }
        return req;
    }

    // Override infra: an in-memory publisher (no broker), and optionally a custom
    // rate limiter / spike guard so we can exercise the 429 paths deterministically.
    private WebApplicationFactory<Program> With(
        IEventPublisher? publisher = null, IRateLimiter? limiter = null, SpikeGuard? spike = null,
        IQuotaMeter? quota = null) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            if (publisher is not null) s.AddSingleton(publisher);
            if (limiter is not null) s.AddSingleton(limiter);
            if (spike is not null) s.AddSingleton(spike);
            if (quota is not null) s.AddSingleton(quota);
        }));

    // A rate limiter with a fixed verdict, to drive the handler's 429 branch.
    private sealed class StubRateLimiter(RateLimitDecision decision) : IRateLimiter
    {
        public ValueTask<RateLimitDecision> CheckAsync(
            string key, double ratePerSecond, long burst, CancellationToken ct = default) =>
            ValueTask.FromResult(decision);
    }

    // A quota meter with a fixed verdict, to drive the handler's monthly-quota 429 branch.
    private sealed class StubQuotaMeter(QuotaDecision decision) : IQuotaMeter
    {
        public ValueTask<QuotaDecision> TryConsumeAsync(
            string key, long monthlyLimit, CancellationToken ct = default) =>
            ValueTask.FromResult(decision);
    }

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        var resp = await _factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Store_ValidDsn_Publishes_AndSetsRemainingHeader()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(StorePost("1", """{"message":"boom","level":"error"}"""));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(resp.Headers.Contains("X-Condux-RateLimit-Remaining"));
        Assert.True(resp.Headers.Contains("X-Condux-Quota-Remaining")); // Free tier is metered
        var published = Assert.Single(pub.Published);
        Assert.Equal("1", published.ProjectId);
        Assert.Equal(Level.Error, published.Event.Level);
        // The relay stamps the project's plan-tier retention (project "1" is Free) from PlanCatalog.
        Assert.Equal(PlanCatalog.For(Tier.Free).RetentionDays, published.RetentionDays);
    }

    // A Sentry envelope (newline-delimited: envelope header, item header, item payload) — the format
    // modern Sentry SDKs POST to /envelope/ instead of /store/.
    private static HttpRequestMessage EnvelopePost(string projectId, string body, string? key = "devkey")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/envelope/")
        {
            Content = new StringContent(body),
        };
        if (key is not null)
        {
            req.Headers.Add("x-condux-auth", key);
        }
        return req;
    }

    [Fact]
    public async Task Envelope_ValidEvent_Publishes()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        // Header line, then an "event" item header + its payload.
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n"
            + "{\"message\":\"boom from envelope\",\"level\":\"error\"}";

        var resp = await client.SendAsync(EnvelopePost("1", envelope));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var published = Assert.Single(pub.Published);
        Assert.Equal("1", published.ProjectId);
        Assert.Equal(Level.Error, published.Event.Level);
        Assert.Equal("boom from envelope", published.Event.Message);
    }

    // Most stock Sentry SDKs gzip the envelope and set Content-Encoding (sentry-dart compresses by
    // default, and the mobile SDKs sentry_flutter delegates to do the same), so the relay must accept a
    // compressed body to make good on "any Sentry SDK works by swapping the DSN".
    private static HttpRequestMessage GzippedEnvelopePost(string projectId, string body, string? key = "devkey") =>
        CompressedEnvelopePost(projectId, body, "gzip", key: key);

    private static byte[] Compress(string body, string encoding)
    {
        var buffer = new MemoryStream();
        Stream Wrap(Stream sink) => encoding switch
        {
            "gzip" => new GZipStream(sink, CompressionLevel.SmallestSize, leaveOpen: true),
            // zlib-wrapped, not raw: HTTP "deflate" is RFC 1950 (zlib), which is what the server's
            // decompression provider expects. Raw DeflateStream output is rejected.
            "deflate" => new ZLibStream(sink, CompressionLevel.SmallestSize, leaveOpen: true),
            "br" => new BrotliStream(sink, CompressionLevel.SmallestSize, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "unsupported encoding"),
        };
        using (var compressor = Wrap(buffer))
        {
            compressor.Write(Encoding.UTF8.GetBytes(body));
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage CompressedEnvelopePost(
        string projectId, string body, string encoding, bool chunked = false, string? key = "devkey")
    {
        var content = new ByteArrayContent(Compress(body, encoding));
        content.Headers.ContentEncoding.Add(encoding);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/envelope/") { Content = content };
        if (chunked)
        {
            // What sentry-dart puts on the wire: a StreamedRequest carries no Content-Length, so the body
            // arrives chunked AND compressed at once.
            req.Headers.TransferEncodingChunked = true;
        }
        if (key is not null)
        {
            req.Headers.Add("x-condux-auth", key);
        }
        return req;
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task Envelope_EveryRegisteredEncoding_Publishes(string encoding)
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n"
            + $"{{\"message\":\"boom via {encoding}\",\"level\":\"error\"}}";

        var resp = await client.SendAsync(CompressedEnvelopePost("1", envelope, encoding));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal($"boom via {encoding}", Assert.Single(pub.Published).Event.Message);
    }

    [Fact]
    public async Task Envelope_ChunkedAndGzipped_Publishes()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n"
            + "{\"message\":\"boom chunked\",\"level\":\"error\"}";

        var resp = await client.SendAsync(CompressedEnvelopePost("1", envelope, "gzip", chunked: true));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("boom chunked", Assert.Single(pub.Published).Event.Message);
    }

    [Fact]
    public async Task Store_GzipEncoded_Publishes()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes("""{"message":"boom compressed","level":"error"}"""));
        }
        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/1/store/") { Content = content };
        req.Headers.Add("x-condux-auth", "devkey");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var published = Assert.Single(pub.Published);
        Assert.Equal("boom compressed", published.Event.Message);
    }

    [Fact]
    public async Task Envelope_GzipEncoded_Publishes()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n"
            + "{\"message\":\"boom from a compressed envelope\",\"level\":\"error\"}";

        var resp = await client.SendAsync(GzippedEnvelopePost("1", envelope));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var published = Assert.Single(pub.Published);
        Assert.Equal(Level.Error, published.Event.Level);
        Assert.Equal("boom from a compressed envelope", published.Event.Message);
    }

    // The zip-bomb guard. A highly compressible body is tiny on the wire but enormous once expanded, so
    // the ceiling has to act on the DECOMPRESSED size; a content-length check would wave this through.
    [Fact]
    public async Task Envelope_DecompressingPastTheCap_Returns413_AndPublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_MAX_INGEST_BYTES", "4096");
            b.ConfigureServices(s => s.AddSingleton<IEventPublisher>(pub));
        }).CreateClient();

        // Deliberately a VALID envelope: half a megabyte of 'a' compresses to a few hundred bytes, and
        // would parse and publish happily if the cap were not enforced. That is what makes this a test of
        // the guard rather than of the parser, which rejects junk either way.
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n"
            + $"{{\"message\":\"{new string('a', 512 * 1024)}\",\"level\":\"error\"}}";

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(envelope));
        }
        Assert.True(compressed.Length < 4096, "the compressed body must be under the cap to prove the point");

        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/1/envelope/") { Content = content };
        req.Headers.Add("x-condux-auth", "devkey");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Envelope_NoEventItem_Ok_PublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        // An envelope carrying only a transaction item — valid, but nothing for an error monitor to store.
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"transaction\"}\n{\"spans\":[]}";

        var resp = await client.SendAsync(EnvelopePost("1", envelope));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Envelope_WrongKey_Returns401()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();
        var resp = await client.SendAsync(EnvelopePost("1", "{}\n{\"type\":\"event\"}\n{}", key: "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Store_MonthlyQuotaExceeded_Returns429_AndPublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Allowed: false, Used: 50_000, Remaining: 0));
        var client = With(pub, quota: quota).CreateClient();

        var resp = await client.SendAsync(StorePost("1", """{"message":"boom","level":"error"}"""));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Empty(pub.Published); // nothing published once the monthly quota is exhausted
    }

    [Fact]
    public async Task Store_MissingOrWrongKey_Returns401()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(StorePost("1", "{}", key: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(StorePost("1", "{}", key: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(StorePost("999", "{}"))).StatusCode);
    }

    [Fact]
    public async Task Store_InvalidJson_Returns400_AndPublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(StorePost("1", "not json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Store_PerProjectLimitExceeded_Returns429_WithRetryAfter()
    {
        var pub = new InMemoryEventPublisher();
        var limiter = new StubRateLimiter(new RateLimitDecision(Allowed: false, Remaining: 0, RetryAfterSeconds: 7));
        var client = With(pub, limiter).CreateClient();

        var resp = await client.SendAsync(StorePost("1", "{}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), resp.Headers.RetryAfter?.Delta);
        Assert.True(resp.Headers.Contains("X-Condux-RateLimit-Remaining"));
        Assert.Empty(pub.Published); // nothing published when rate-limited
    }

    [Fact]
    public async Task Store_SpikeGuardTrips_Returns429()
    {
        var pub = new InMemoryEventPublisher();
        // Tiny instance-wide cap (burst 2): rapid requests get shed even though the
        // per-project (Free tier) budget is nowhere near exhausted.
        var client = With(pub, spike: new SpikeGuard(ratePerSecond: 1, burst: 2)).CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await client.SendAsync(StorePost("1", "{}"))).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
