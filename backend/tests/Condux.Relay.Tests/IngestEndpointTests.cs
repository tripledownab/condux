using System.IO.Compression;
using System.Net;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Condux.Relay.Ingest;
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
        IQuotaMeter? quota = null, IProjectStore? projects = null, IngestBodyBudget? budget = null) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            if (publisher is not null) s.AddSingleton(publisher);
            if (budget is not null) s.AddSingleton(budget);
            if (limiter is not null) s.AddSingleton(limiter);
            if (spike is not null) s.AddSingleton(spike);
            if (quota is not null) s.AddSingleton(quota);
            if (projects is not null) s.AddSingleton(projects);
        }));

    // A rate limiter with a fixed verdict, to drive the handler's 429 branch.
    private sealed class StubRateLimiter(RateLimitDecision decision) : IRateLimiter
    {
        public ValueTask<RateLimitDecision> CheckAsync(
            string key, double ratePerSecond, long burst, CancellationToken ct = default) =>
            ValueTask.FromResult(decision);
    }

    // A quota meter with a fixed verdict, to drive the handler's monthly-quota 429 branch. It records the
    // key it was asked about, since which allowance an event spends is the handler's decision, and what it
    // was given back.
    private sealed class StubQuotaMeter(QuotaDecision decision) : IQuotaMeter
    {
        public List<string> Keys { get; } = [];
        public List<(string Key, long Count)> Refunds { get; } = [];

        public ValueTask<QuotaDecision> TryConsumeAsync(
            string key, long monthlyLimit, long count = 1, CancellationToken ct = default)
        {
            Keys.Add(key);
            return ValueTask.FromResult(decision);
        }

        public ValueTask RefundAsync(string key, long count, CancellationToken ct = default)
        {
            Refunds.Add((key, count));
            return ValueTask.CompletedTask;
        }
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
        // A store payload says nothing about when it was sent.
        Assert.Equal(0, published.SentAtUnixMs);
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
        var envelope = "{\"event_id\":\"abc123\",\"sent_at\":\"2026-10-14T10:00:00Z\"}\n{\"type\":\"event\"}\n"
            + "{\"message\":\"boom from envelope\",\"level\":\"error\"}";

        var resp = await client.SendAsync(EnvelopePost("1", envelope));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var published = Assert.Single(pub.Published);
        Assert.Equal("1", published.ProjectId);
        Assert.Equal(Level.Error, published.Event.Level);
        Assert.Equal("boom from envelope", published.Event.Message);
        // The envelope's sent_at goes to the consumer, which corrects the sender's clock drift by it.
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T10:00:00Z").ToUnixTimeMilliseconds(), published.SentAtUnixMs);
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

    [Theory]
    [InlineData("/api/1/envelope/", "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n{\"message\":\"boom\",\"level\":\"error\"}")]
    [InlineData("/api/1/store/", "{\"message\":\"boom\",\"level\":\"error\"}")]
    public async Task A_brotli_body_is_not_decoded_and_publishes_nothing(string path, string body)
    {
        // The body is a valid event in a valid Brotli stream, so a refusal is the decoder not running on
        // it: the middleware passes the encoding through undecoded and the parser refuses the raw bytes.
        // With the provider registered this request is a 200 that publishes.
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        var content = new ByteArrayContent(Compress(body, "br"));
        content.Headers.ContentEncoding.Add("br");
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        req.Headers.Add("x-condux-auth", "devkey");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    [Theory]
    [InlineData("/api/1/envelope/", "gzip")]
    [InlineData("/api/1/store/", "gzip")]
    [InlineData("/api/1/store/", "deflate")]
    public async Task A_body_that_is_not_its_declared_encoding_is_answered_400_and_publishes_nothing(
        string path, string encoding)
    {
        // The sender's broken body, in each encoding the relay decompresses. It escaped the read as an
        // exception, a 500 the relay then filed against itself, which anyone holding a public DSN could repeat.
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        var content = new ByteArrayContent("""{"message":"not compressed","level":"error"}"""u8.ToArray());
        content.Headers.ContentEncoding.Add(encoding);
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        req.Headers.Add("x-condux-auth", "devkey");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("invalid_payload", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(pub.Published);
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

    // The relay's shared body budget is charged on what a body DECOMPRESSES to. This envelope is a few
    // hundred bytes on the wire and half a megabyte expanded, well under the ingest cap, so only a charge
    // on the expanded bytes refuses it with a 64 KiB budget; the same request with room passes, so the
    // refusal is the budget's and not the body's.
    [Theory]
    [InlineData(64 * 1024, HttpStatusCode.TooManyRequests)]
    [InlineData(4 * 1024 * 1024, HttpStatusCode.OK)]
    public async Task Envelope_IsChargedToTheBudgetOnItsDecompressedSize(long budget, HttpStatusCode expected)
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub, budget: new IngestBodyBudget(budget, budget, budget)).CreateClient();
        var envelope = "{\"event_id\":\"abc123\"}\n{\"type\":\"event\"}\n"
            + $"{{\"message\":\"{new string('a', 512 * 1024)}\",\"level\":\"error\"}}";

        var resp = await client.SendAsync(GzippedEnvelopePost("1", envelope));

        Assert.Equal(expected, resp.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, pub.Published.Count);
    }

    // Over the budget is a 429 an SDK retries after Retry-After, and the event the quota step counted is
    // given back, or the retry would spend the org's allowance twice for one event.
    [Fact]
    public async Task Store_OverTheBodyBudget_Returns429_AndRefundsTheEvent()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(pub, quota: quota, budget: new IngestBodyBudget(1, 1, 1)).CreateClient();

        var resp = await client.SendAsync(StorePost("1", """{"message":"boom","level":"error"}"""));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("1", resp.Headers.RetryAfter?.ToString());
        Assert.Contains("ingest_busy", await resp.Content.ReadAsStringAsync());
        Assert.Empty(pub.Published);
        Assert.Equal(1, Assert.Single(quota.Refunds).Count);
    }

    // A request gives its share back when it is done, whatever it was answered: the whole budget is free
    // again afterwards, which a share kept by an accepted or a refused body would prevent.
    [Fact]
    public async Task Store_GivesItsShareOfTheBudgetBack()
    {
        const long size = 1024 * 1024;
        var budget = new IngestBodyBudget(size, size, size);
        var client = With(new InMemoryEventPublisher(), budget: budget).CreateClient();

        var accepted = await client.SendAsync(StorePost("1", """{"message":"boom","level":"error"}"""));
        var malformed = await client.SendAsync(StorePost("1", "not json"));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.True(budget.TryReserve(size));
    }

    // Only the budget's 429 is retried, and only a capped tier counted the event, so neither a 413 nor an
    // unlimited tier is given anything back.
    [Fact]
    public async Task Store_TooLarge_RefundsNothing()
    {
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_MAX_INGEST_BYTES", "64");
            b.ConfigureServices(s => s.AddSingleton<IQuotaMeter>(quota));
        }).CreateClient();

        var resp = await client.SendAsync(StorePost("1", $$"""{"message":"{{new string('a', 128)}}"}"""));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Empty(quota.Refunds);
    }

    [Fact]
    public async Task Store_OverTheBodyBudget_OnAnUnlimitedTier_RefundsNothing()
    {
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 0, Remaining: long.MaxValue));
        var client = With(new InMemoryEventPublisher(), quota: quota, projects: new TwoOrgProjectStore(),
            budget: new IngestBodyBudget(1, 1, 1)).CreateClient();

        var resp = await client.SendAsync(StorePost("14", """{"message":"boom","level":"error"}"""));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Empty(quota.Refunds);
    }

    // The bound is relay-wide only if every request draws on one budget: a budget per request or per scope
    // would bound nothing.
    [Fact]
    public void EveryRequestDrawsOnOneBudget()
    {
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();

        Assert.Same(scopeA.ServiceProvider.GetRequiredService<IngestBodyBudget>(),
            scopeB.ServiceProvider.GetRequiredService<IngestBodyBudget>());
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
        // RFC 9110 15.5.2: a 401 carries a challenge; the relay's names the Sentry auth scheme.
        var challenge = Assert.Single(resp.Headers.WwwAuthenticate);
        Assert.Equal("Sentry", challenge.Scheme);
        Assert.Equal("realm=\"condux\"", challenge.Parameter);
    }

    [Fact]
    public async Task Store_ProjectsOfOneOrg_SpendOneAllowance()
    {
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(new InMemoryEventPublisher(), quota: quota, projects: new TwoOrgProjectStore()).CreateClient();

        foreach (var project in new[] { "11", "12", "13" })
        {
            var resp = await client.SendAsync(StorePost(project, """{"message":"boom","level":"error"}"""));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        Assert.Equal(["org:900", "org:900", "org:901"], quota.Keys);
    }

    [Fact]
    public async Task Store_Transaction_IsAccepted_PublishesNothing_AndGivesItsQuotaBack()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(pub, quota: quota, projects: new TwoOrgProjectStore()).CreateClient();

        var resp = await client.SendAsync(
            StorePost("11", """{"type":"transaction","transaction":"GET /","level":"error"}"""));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(pub.Published);
        Assert.Equal([("org:900", 1L)], quota.Refunds);
    }

    [Fact]
    public async Task Envelope_OfOnlyATransaction_GivesItsQuotaBack()
    {
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(new InMemoryEventPublisher(), quota: quota, projects: new TwoOrgProjectStore()).CreateClient();

        var resp = await client.SendAsync(
            EnvelopePost("11", "{\"event_id\":\"abc123\"}\n{\"type\":\"transaction\"}\n{\"spans\":[]}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal([("org:900", 1L)], quota.Refunds);
    }

    [Fact]
    public async Task Envelope_OfOnlyATransaction_OnAnUnlimitedTier_RefundsNothing()
    {
        // An unlimited tier counted nothing, so there is nothing to give back.
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 0, Remaining: long.MaxValue));
        var client = With(new InMemoryEventPublisher(), quota: quota, projects: new TwoOrgProjectStore()).CreateClient();

        var resp = await client.SendAsync(
            EnvelopePost("14", "{\"event_id\":\"abc123\"}\n{\"type\":\"transaction\"}\n{\"spans\":[]}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(quota.Refunds);
    }

    [Fact]
    public async Task Store_Malformed_RefundsNothing()
    {
        // A body that will not parse is the sender's error, answered 400, and keeps the quota it was counted.
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(new InMemoryEventPublisher(), quota: quota, projects: new TwoOrgProjectStore()).CreateClient();

        var resp = await client.SendAsync(StorePost("11", "not json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(quota.Refunds);
    }

    [Fact]
    public async Task Store_AnEvent_KeepsItsQuota()
    {
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(new InMemoryEventPublisher(), quota: quota, projects: new TwoOrgProjectStore()).CreateClient();

        await client.SendAsync(StorePost("11", """{"message":"boom","level":"error"}"""));

        Assert.Empty(quota.Refunds);
    }

    [Fact]
    public async Task Store_MonthlyQuotaExceeded_Returns429_AndPublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 0, Used: 50_000, Remaining: 0));
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

    /// <summary>
    /// Well formed JSON whose string is half a surrogate pair is the sender's mistake too. The parser used
    /// to throw on reading it, which answered 500 and filed the sender's bad input against the relay.
    /// </summary>
    [Fact]
    public async Task StoreAndEnvelope_StringThatIsNotText_Returns400_AndPublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var store = await client.SendAsync(StorePost("1", """{"message":"\uD800"}"""));
        var envelope = await client.SendAsync(EnvelopePost("1",
            """{"event_id":"e"}""" + "\n" + """{"type":"event"}""" + "\n" + """{"message":"\uD800"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, store.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, envelope.StatusCode);
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
