using System.Globalization;
using System.Net;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Relay.Ingest;
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

    // Three ERROR records in one export, each with a distinct body so a partial admission can be checked
    // for WHICH ones were stored rather than only how many.
    private const string ThreeErrorBatch = """
        {"resourceLogs":[{"scopeLogs":[{"logRecords":[
            {"severityNumber":17,"body":{"stringValue":"one"}},
            {"severityNumber":17,"body":{"stringValue":"two"}},
            {"severityNumber":17,"body":{"stringValue":"three"}}
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

    private WebApplicationFactory<Program> With(
        IEventPublisher publisher, IQuotaMeter? quota = null, int? maxRecords = null,
        IProjectStore? projects = null, IngestBodyBudget? budget = null) =>
        factory.WithOtlpServices(publisher, quota, maxRecords, projects, budget);

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
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 0, Used: 50_000, Remaining: 0));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // OTLP reports rejects in the body, not the status
        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("partialSuccess", content);
        Assert.Contains("rejectedLogRecords", content);
        Assert.Empty(pub.Published);
    }

    // The record count in an export is chosen by whoever sent it, and the meter behind this is one counter
    // shared by every replica. Metering per record therefore turned a single request into as many round
    // trips as the sender asked for, which is why this asserts the call COUNT and not just the outcome:
    // every other assertion here passes just as happily against the per-record loop.
    [Fact]
    public async Task Otlp_MultiRecordBatch_ChecksTheQuotaOnceForTheWholeBatch()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 3, Used: 3, Remaining: 7));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ThreeErrorBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3L, Assert.Single(quota.Requested)); // one call, asking for the whole batch
        Assert.Equal(3, pub.Published.Count);
    }

    // A batch that straddles the cap spends what is left rather than being refused whole, so the last
    // events of a month are not lost to whatever happened to arrive in a large enough group.
    [Fact]
    public async Task Otlp_QuotaStraddlesTheBatch_PublishesWhatFits_AndRejectsTheRest()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 2, Used: 50_000, Remaining: 0));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ThreeErrorBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, pub.Published.Count);
        Assert.Equal(["one", "two"], pub.Published.Select(p => p.Event.Message));
        // Read through a JSON parser, as a sender does: the serializer escapes the apostrophe as \u0027.
        using var body = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var partial = body.RootElement.GetProperty("partialSuccess");
        Assert.True(partial.TryGetProperty("rejectedLogRecords", out _));
        Assert.Contains("organization's monthly event quota is exhausted",
            partial.GetProperty("errorMessage").GetString());
    }

    // The body cap bounds the bytes an export may carry, not the records inside them (see RelayOptions).
    // The ceiling applies BEFORE the meter, which is what the assertion on Requested is for: an overflow
    // record that never gets stored must not spend the project's quota either.
    [Fact]
    public async Task Otlp_BatchPastThePerExportCeiling_StoresTheCeiling_AndRejectsTheRest()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 2, Used: 2, Remaining: 8));
        var client = With(pub, quota, maxRecords: 2).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ThreeErrorBatch));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2L, Assert.Single(quota.Requested)); // the dropped record costs no quota
        Assert.Equal(["one", "two"], pub.Published.Select(p => p.Event.Message));
        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("more error records than one request accepts", content);
        // Named separately from the quota, or a sender whose batch was simply too large goes on paying for
        // a plan that was never the problem.
        Assert.DoesNotContain("quota", content);
    }

    // Quota is taken for the whole batch before the first publish and cannot be undone by this request, so
    // a broker that stops answering mid-batch would otherwise charge the project for events it never
    // stored. Metering one at a time could only ever lose one that way; a batch can lose as many as it
    // took. The assertion is on the AMOUNT, not just that a refund happened: giving back everything would
    // hand back the records that did store, and giving back nothing is the regression itself.
    [Fact]
    public async Task Otlp_PublishFailsPartWayThrough_RefundsOnlyTheRecordsNeverStored()
    {
        var pub = new FailAfterPublisher(succeed: 1);
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 3, Used: 3, Remaining: 7));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ThreeErrorBatch));

        // The failure reaches the sender as an error rather than a partial success: these records were
        // admitted, so reporting them rejected would tell the sender to give up on events Condux lost.
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(1, pub.Published);
        Assert.Equal(2L, Assert.Single(quota.Refunded)); // the 2 of 3 that never reached the broker
    }

    // The charge and the refund both go to the org's allowance, never the project's, or a refund would
    // credit a counter the charge never touched.
    [Fact]
    public async Task Otlp_ChargesAndRefundsTheOrgsAllowance()
    {
        var pub = new FailAfterPublisher(succeed: 1);
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 3, Used: 3, Remaining: 7));
        var client = With(pub, quota, projects: new TwoOrgProjectStore()).CreateClient();

        var resp = await client.SendAsync(OtlpPost("12", ThreeErrorBatch));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(["org:900", "org:900"], quota.Keys);
    }

    // Only storable records are metered: an export of pure INFO costs nothing, and must not ask the meter
    // at all about a batch it is going to drop anyway.
    [Fact]
    public async Task Otlp_NoStorableRecords_NeverAsksTheQuota()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(pub, quota).CreateClient();

        var infoOnly =
            """{"resourceLogs":[{"scopeLogs":[{"logRecords":[{"severityNumber":9,"body":{"stringValue":"info"}}]}]}]}""";
        var resp = await client.SendAsync(OtlpPost("1", infoOnly));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(quota.Requested);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Otlp_MissingOrWrongKey_Returns401()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();
        foreach (var key in new[] { null, "wrong" })
        {
            var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch, key: key));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("Sentry", Assert.Single(resp.Headers.WwwAuthenticate).Scheme);
        }
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
    /// Protobuf is what an exporter sends unless told otherwise, so a partial success has to carry its
    /// explanation in that encoding too. Every other partialSuccess assertion in this file reads a JSON
    /// body, which would stay green while the binary encoder dropped the field and left a real collector
    /// with rejected records and no reason. The message is a length-delimited string, so it appears
    /// verbatim in the bytes; finding it proves the encoder wrote the field.
    /// </summary>
    [Fact]
    public async Task Otlp_ProtobufExport_RejectedRecords_ExplainedInProtobuf()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 0, Used: 50_000, Remaining: 0));
        var client = With(pub, quota).CreateClient();

        var resp = await client.SendAsync(ProtobufPost("1", CollectorExport()));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/x-protobuf", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Contains("organization's monthly event quota is exhausted", Encoding.UTF8.GetString(bytes));
        Assert.Empty(pub.Published);
    }

    /// <summary>
    /// An export the relay's body budget has no room for is a 429 the exporter retries, answered with a
    /// Status like every other refusal (8 is RESOURCE_EXHAUSTED). The body is read before the quota is
    /// asked, so nothing was spent on it.
    /// </summary>
    [Fact]
    public async Task Otlp_OverTheBodyBudget_Returns429_AndSpendsNoQuota()
    {
        var pub = new InMemoryEventPublisher();
        var quota = new StubQuotaMeter(new QuotaDecision(Admitted: 1, Used: 1, Remaining: 9));
        var client = With(pub, quota, budget: new IngestBodyBudget(1, 1, 1)).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("1", resp.Headers.RetryAfter?.ToString());
        Assert.Contains("\"code\":8", await resp.Content.ReadAsStringAsync());
        Assert.Empty(quota.Requested);
        Assert.Empty(pub.Published);
    }

    /// <summary>An export gives its share of the budget back once answered, stored or not decoded.</summary>
    [Fact]
    public async Task Otlp_GivesItsShareOfTheBudgetBack()
    {
        const long size = 1024 * 1024;
        var budget = new IngestBodyBudget(size, size, size);
        var client = With(new InMemoryEventPublisher(), budget: budget).CreateClient();

        var stored = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch));
        var invalid = await client.SendAsync(OtlpPost("1", "not json"));

        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True(budget.TryReserve(size));
        Assert.Equal(0, budget.SharesHeld);
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
    /// Well formed JSON whose string escapes half a surrogate pair is not text. The parser accepts the
    /// document and the decoder found it only when reading the string, which it used to throw on, so the
    /// request answered 500 and the relay filed the sender's bad input against itself. A bytes value is
    /// a base64 string read another way, and threw after plain strings no longer did.
    /// </summary>
    [Theory]
    [InlineData("""{"stringValue":"\uD800"}""")]
    [InlineData("""{"bytesValue":"\uD800"}""")]
    public async Task Otlp_JsonStringThatIsNotText_Returns400_WithAStatusInJson(string body)
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch.Replace("""{"stringValue":"boom"}""", body)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"code\":3", await resp.Content.ReadAsStringAsync());
        Assert.Empty(pub.Published);
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

    /// <summary>
    /// Sent as gzip but not gzip: the sender's broken body, so a 400 with a Status, not a 500 the relay would
    /// file against itself. 3 is INVALID_ARGUMENT.
    /// </summary>
    [Fact]
    public async Task Otlp_ExportThatIsNotItsDeclaredEncoding_Returns400_WithAStatus()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(ProtobufPost("1", CollectorExport(), contentEncoding: "gzip"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal([0x08, 0x03], body[..2]);
        Assert.Empty(pub.Published);
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
