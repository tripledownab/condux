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

    [Fact]
    public async Task Otlp_InvalidJson_Returns400_PublishesNothing()
    {
        var pub = new InMemoryEventPublisher();
        var client = With(pub).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", "not json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(pub.Published);
    }

    [Fact]
    public async Task Otlp_ProtobufContentType_Returns415()
    {
        var client = With(new InMemoryEventPublisher()).CreateClient();

        var resp = await client.SendAsync(OtlpPost("1", ErrorAndInfoBatch, contentType: "application/x-protobuf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }
}
