using System.Net;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Quotas;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>
/// Native OTLP/HTTP logs ingestion (#79): an OpenTelemetry logs export to <c>/api/{projectId}/v1/logs</c>
/// is authed, parsed, filtered to error records, and published — with OTLP-shaped responses (200 +
/// ExportLogsServiceResponse, partialSuccess when the quota rejects some). The dev store seeds project
/// "1" / key "devkey".
/// </summary>
public class OtlpIngestEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // An OTLP/JSON logs export: one ERROR record carrying an exception, and one INFO record.
    private const string ErrorAndInfoBatch = """
        {"resourceLogs":[{"resource":{"attributes":[
            {"key":"service.name","value":{"stringValue":"checkout"}}]},
          "scopeLogs":[{"logRecords":[
            {"severityNumber":17,"body":{"stringValue":"boom"},"attributes":[
                {"key":"exception.type","value":{"stringValue":"TypeError"}},
                {"key":"exception.message","value":{"stringValue":"nope"}}]},
            {"severityNumber":9,"body":{"stringValue":"ok"}}
          ]}]}]}
        """;

    private HttpRequestMessage OtlpPost(
        string projectId, string body, string? key = "devkey", string contentType = "application/json")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/v1/logs")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        if (key is not null)
        {
            req.Headers.Add("x-condux-auth", key);
        }
        return req;
    }

    private WebApplicationFactory<Program> With(IEventPublisher publisher, IQuotaMeter? quota = null) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(publisher);
            if (quota is not null) s.AddSingleton(quota);
        }));

    private sealed class StubQuotaMeter(QuotaDecision decision) : IQuotaMeter
    {
        public ValueTask<QuotaDecision> TryConsumeAsync(string key, long monthlyLimit, CancellationToken ct = default) =>
            ValueTask.FromResult(decision);
    }

    [Fact]
    public async Task Otlp_ErrorRecord_IsPublished_InfoRecordIsFilteredOut()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // OTLP success is 200
        Assert.Equal("{}", (await resp.Content.ReadAsStringAsync()).Trim()); // empty ExportLogsServiceResponse

        // Only the error record becomes an event; the INFO record is accepted but not stored.
        var published = Assert.Single(pub.Published);
        Assert.Equal("1", published.ProjectId);
        Assert.Equal(Level.Error, published.Event.Level);
        Assert.Equal("checkout", published.Event.ServerName);
        Assert.Equal("TypeError", Assert.Single(published.Event.Exceptions).Type);
    }

    [Fact]
    public async Task Otlp_OnlyNonErrorRecords_PublishNothing_ReturnEmptySuccess()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var infoOnly =
            """{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"severityNumber":9,"body":{"stringValue":"info"}}]}]}]}""";
        var resp = await client.SendAsync(OtlpPost("1", infoOnly));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Otlp_QuotaExceeded_ReturnsPartialSuccess_AndPublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Allowed: false, Used: 50_000, Remaining: 0));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // OTLP reports rejects in the body, not the status
        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("partialSuccess", content);
        Assert.Contains("rejectedLogRecords", content);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Otlp_MissingOrWrongKey_Returns401()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch, key: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch, key: "wrong"))).StatusCode);
    }

    /// <summary>
    /// A rejected DSN is a 4xx like any other, so it owes the sender a Status in the request's encoding.
    /// This is the refusal a misconfigured exporter meets first, and it used to answer JSON to a protobuf
    /// sender: the fix covered the paths that read the body and left the ones that never get that far.
    /// 16 is UNAUTHENTICATED.
    /// </summary>
    [Fact]
    public async Task Otlp_WrongKeyOnAProtobufRequest_Returns401_WithAStatusInProtobuf()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();

        var resp = await client.SendAsync(ProtobufPost("1", CollectorExport(), key: "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("application/x-protobuf", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(body);
        Assert.Equal(0x08, body[0]);   // field 1, varint
        Assert.Equal(0x10, body[1]);   // 16, UNAUTHENTICATED
    }

    [Fact]
    public async Task Otlp_InvalidJson_Returns400_PublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", "not json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    /// <summary>
    /// The binary encoding, from a real collector's export. It is what an OpenTelemetry exporter sends
    /// unless it is told otherwise, so this is the path most installations actually use.
    /// </summary>
    [Fact]
    public async Task Otlp_ProtobufExport_IsIngested()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(ProtobufPost("1", CollectorExport()));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var published = Assert.Single(pub.Published);
        Assert.Equal(Level.Error, published.Event.Level);
        Assert.Equal("checkout-api", published.Event.ServerName);
    }

    /// <summary>
    /// The protocol requires a server to answer in the content type it received, and a full success is an
    /// empty message. A JSON body here would be a response the sender cannot parse.
    /// </summary>
    [Fact]
    public async Task Otlp_ProtobufExport_IsAnsweredInProtobuf()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();

        var resp = await client.SendAsync(ProtobufPost("1", CollectorExport()));

        Assert.Equal("application/x-protobuf", resp.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// The protocol requires the body of every 4xx and 5xx to be a google.rpc.Status in the content type
    /// the request arrived in. Asserting the status code alone is what let this ship answering a protobuf
    /// sender with a bare 400 carrying no content type and no body at all, so the body is read back here:
    /// code is field 1 as a varint, and 3 is INVALID_ARGUMENT.
    /// </summary>
    [Fact]
    public async Task Otlp_MalformedProtobuf_Returns400_WithAStatusInProtobuf()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();

        var resp = await client.SendAsync(ProtobufPost("1", [0x00, 0x00]));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/x-protobuf", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(body);
        Assert.Equal(0x08, body[0]);   // field 1, varint
        Assert.Equal(0x03, body[1]);   // INVALID_ARGUMENT
    }

    /// <summary>
    /// The JSON path had its own non-conformance: it answered this service's {"error": "..."} shape,
    /// which is not a Status, so a sender parsing the protocol found nothing it recognised.
    /// </summary>
    [Fact]
    public async Task Otlp_InvalidJson_Returns400_WithAStatusInJson()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", "not json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":3", body);
        Assert.DoesNotContain("\"error\"", body);
    }

    /// <summary>
    /// Gzipped, which is what the OpenTelemetry Collector's exporter sends by default. The decompression
    /// middleware has to run before the decoder sees the body, and the bound on the read has to apply to
    /// the expanded bytes rather than the compressed ones.
    /// </summary>
    [Fact]
    public async Task Otlp_GzippedProtobufExport_IsIngested()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(
            ProtobufPost("1", Gzip(CollectorExport()), contentEncoding: "gzip"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("checkout-api", Assert.Single(pub.Published).Event.ServerName);
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest))
        {
            gzip.Write(payload);
        }

        return buffer.ToArray();
    }

    private static byte[] CollectorExport() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "otlp-logs-collector.protobuf.bin"));

    private static HttpRequestMessage ProtobufPost(
        string projectId, byte[] body, string? key = "devkey", string? contentEncoding = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/v1/logs")
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        // Set here rather than by the caller: HttpRequestMessage.Content is nullable once the object is
        // handed back, so reaching into it outside this method is a null dereference the compiler flags.
        if (contentEncoding is not null)
        {
            req.Content.Headers.ContentEncoding.Add(contentEncoding);
        }
        if (key is not null)
        {
            req.Headers.Add("x-condux-auth", key);
        }
        return req;
    }
}
