using System.Net;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Quotas;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>
/// OTLP/HTTP traces ingestion: an export to <c>/api/{projectId}/v1/traces</c> is authed, parsed,
/// cut down to its failed spans, and published, with the logs endpoint's own limits, quota call and error
/// format. The dev store seeds project "1" / key "devkey".
/// </summary>
public class OtlpTraceIngestEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Trace = "0102030405060708090a0b0c0d0e0f10";

    // OTLP/JSON: status code 2 is ERROR. A root that fails, with a child that fails and one that succeeds,
    // so the export holds three spans and one failure.
    private static readonly string OneFailureBatch = Export(
        Span("0000000000000001", "", "checkout", failed: true),
        Span("0000000000000002", "0000000000000001", "charge-card", failed: true),
        Span("0000000000000003", "0000000000000001", "send-receipt", failed: false));

    // Three separate failures under one root, each with its own name.
    private static readonly string ThreeFailureBatch = Export(
        Span("0000000000000001", "", "root", failed: false),
        Span("0000000000000002", "0000000000000001", "one", failed: true),
        Span("0000000000000003", "0000000000000001", "two", failed: true),
        Span("0000000000000004", "0000000000000001", "three", failed: true));

    private static string Span(string id, string parent, string name, bool failed) =>
        "{\"traceId\":\"" + Trace + "\",\"spanId\":\"" + id + "\",\"parentSpanId\":\"" + parent
        + "\",\"name\":\"" + name + "\",\"startTimeUnixNano\":\"1700000000000000000\","
        + "\"endTimeUnixNano\":\"1700000001000000000\",\"status\":{\"code\":" + (failed ? 2 : 1)
        + ",\"message\":\"" + (failed ? "it failed" : "") + "\"}}";

    private static string Export(params string[] spans) =>
        "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\","
        + "\"value\":{\"stringValue\":\"checkout\"}}]},\"scopeSpans\":[{\"spans\":["
        + string.Join(",", spans) + "]}]}]}";

    private static HttpRequestMessage Post(
        string body, string? key = "devkey", string contentType = "application/json", string projectId = "1")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/v1/traces")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        if (key is not null)
        {
            req.Headers.Add("x-condux-auth", key);
        }

        return req;
    }

    private WebApplicationFactory<Program> With(
        IEventPublisher publisher, IQuotaMeter? quota = null, int? maxRecords = null) =>
        factory.WithOtlpServices(publisher, quota, maxRecords);

    [Fact]
    public async Task A_failed_span_is_published_and_the_rest_of_the_export_is_not()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(Post(OneFailureBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var published = Assert.Single(pub.Published).Event;
        Assert.Equal("charge-card", published.Transaction); // the deepest failure, not the root above it
        Assert.Equal("checkout", published.ServerName);
        Assert.Equal(Trace, published.TraceId);
        Assert.Equal(Level.Error, published.Level);
    }

    [Fact]
    public async Task A_protobuf_export_from_a_real_instrumentation_is_ingested()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();
        var bytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Golden", "traces", "agents-tool-raises.protobuf.bin"));
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/1/v1/traces") { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        req.Headers.Add("x-condux-auth", "devkey");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/x-protobuf", resp.Content.Headers.ContentType?.MediaType);
        Assert.Single(pub.Published);
    }

    [Fact]
    public async Task An_export_with_no_failed_span_is_accepted_and_spends_no_quota()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(Post(Export(
            Span("0000000000000001", "", "fine", failed: false))));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(quota.Requested);
        Assert.Empty(pub.Published);
        Assert.DoesNotContain("partialSuccess", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_quota_is_asked_once_for_every_failure_in_the_export()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 3, Used: 3, Remaining: 7));
        var client = With(pub, quota).CreateClient();

        await client.SendAsync(Post(ThreeFailureBatch));

        Assert.Equal(3L, Assert.Single(quota.Requested));
        Assert.Equal(3, pub.Published.Count);
    }

    [Fact]
    public async Task A_quota_that_runs_out_mid_export_stores_what_fits_and_says_so_in_spans()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 2, Used: 50_000, Remaining: 0));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(Post(ThreeFailureBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, pub.Published.Count);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var partial = body.RootElement.GetProperty("partialSuccess");
        Assert.True(partial.TryGetProperty("rejectedSpans", out _));
        var message = partial.GetProperty("errorMessage").GetString();
        Assert.Contains("organization's monthly event quota is exhausted", message);
        Assert.Contains("export fewer spans", message);
    }

    [Fact]
    public async Task An_export_past_the_per_request_ceiling_stores_the_ceiling_and_names_spans()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 2, Used: 2, Remaining: 8));
        var client = With(pub, quota, maxRecords: 2).CreateClient();

        var resp = await client.SendAsync(Post(ThreeFailureBatch));

        Assert.Equal(2L, Assert.Single(quota.Requested)); // the dropped failure costs no quota
        Assert.Equal(2, pub.Published.Count);
        Assert.Contains("more failed spans than one request accepts", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_publish_that_fails_part_way_refunds_only_what_was_never_stored()
    {
        var pub = new FailAfterPublisher(succeed: 1);
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 3, Used: 3, Remaining: 7));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(Post(ThreeFailureBatch));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(2L, Assert.Single(quota.Refunded));
    }

    [Fact]
    public async Task A_rejected_key_is_answered_with_a_status_in_the_requests_encoding()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();

        var resp = await client.SendAsync(Post(OneFailureBatch, key: "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(16, body.RootElement.GetProperty("code").GetInt32()); // UNAUTHENTICATED
    }

    [Fact]
    public async Task A_body_that_is_not_an_export_is_a_400_with_a_status_and_publishes_nothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(Post("not json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("code").GetInt32()); // INVALID_ARGUMENT
        Assert.Empty(pub.Published);
    }
}
