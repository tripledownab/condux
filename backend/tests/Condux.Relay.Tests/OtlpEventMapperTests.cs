using Condux.Core.Events;
using Condux.Otlp;
using Condux.Relay.Ingest;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>
/// OTLP/HTTP logs to <see cref="Event"/> golden + unit coverage (#79). Decoding the wire format belongs
/// to the Condux.Otlp package; these tests cover the mapping onto our own model, in both encodings, so a
/// change to either cannot silently break OpenTelemetry compatibility.
/// </summary>
public class OtlpEventMapperTests
{
    private static byte[] Load(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    private static IReadOnlyList<Event> FromJson(string json)
    {
        var decoded = OtlpLogs.ParseJson(json);
        Assert.True(decoded.IsSuccess, decoded.Error);
        return OtlpEventMapper.ToEvents(decoded.Value);
    }

    private static IReadOnlyList<Event> FromGoldenJson() =>
        FromJson(System.Text.Encoding.UTF8.GetString(Load("otlp-logs-exception.json")));

    [Fact]
    public void ErrorLogRecord_MapsToAnExceptionEvent_WithFramesResourceAndTags()
    {
        var events = FromGoldenJson();
        Assert.Equal(2, events.Count); // one ERROR + one INFO record

        var error = events[0];
        Assert.Equal(Level.Error, error.Level);
        Assert.Equal("checkout", error.ServerName);          // resource service.name
        Assert.Equal("2.4.1", error.Release);                // resource service.version
        Assert.Equal("production", error.Environment);       // resource deployment.environment
        Assert.Equal("nodejs", error.Platform);              // resource telemetry.sdk.language
        Assert.Equal(1714564800123, error.TimestampUnixMs);  // ns to ms
        Assert.Equal("5b8efff798038103d269b633813fc60c", error.TraceId);
        Assert.Equal("Unhandled error while processing order", error.Message);

        var ex = Assert.Single(error.Exceptions);
        Assert.Equal("TypeError", ex.Type);
        Assert.Equal("Cannot read properties of undefined (reading 'price')", ex.Value);

        // The V8 stacktrace string parses into frames, oldest (outermost) to newest (crashing), with the
        // node_modules frame marked out-of-app and the app frame in-app.
        var frames = ex.Stacktrace!.Frames;
        Assert.Equal(2, frames.Count);
        Assert.Contains("router.js", frames[0].Filename);
        Assert.False(frames[0].InApp);
        Assert.Contains("checkout.js", frames[1].Filename);
        Assert.Equal(42, frames[1].Lineno);
        Assert.True(frames[1].InApp);

        // Non-exception attributes ride as tags; the exception.* ones are folded into the exception.
        Assert.Equal("A-1001", error.Tags["order.id"]);
        Assert.Equal("500", error.Tags["http.status_code"]); // an int attribute, read as its decimal text
        Assert.DoesNotContain("exception.type", error.Tags.Keys);
        Assert.False(string.IsNullOrEmpty(error.EventId)); // one minted per record
    }

    /// <summary>
    /// The exclusion list has to match the JS SDK's isInApp in sdks/core, because both classify the same
    /// V8 frames and the fingerprint is built from the in-app ones. The framework frame is the one that
    /// drifted: it was added to the SDK and not here, so a bundled Next.js server reporting over OTLP put
    /// framework frames into its own fingerprint, where a framework upgrade re-groups every issue.
    /// </summary>
    [Fact]
    public void Frames_FromDependencies_Runtime_AndFramework_AreNotInApp()
    {
        // Literal backslash-n, so the newlines are real only once the JSON attribute value is parsed.
        var stack = string.Join("\\n",
            "TypeError: boom",
            "    at handler (/app/src/checkout.js:42:9)",
            "    at run (/app/node_modules/express/lib/router.js:10:1)",
            "    at read (node:internal/fs:99:3)",
            "    at render (/app/.next/server/chunks/next/dist/server/render.js:7:2)");
        // Substituted rather than interpolated: the JSON's own "}}" runs collide with the braces a raw
        // interpolated literal uses as its delimiters.
        var json = """
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[{"severityNumber":17,"attributes":[
                {"key":"exception.type","value":{"stringValue":"TypeError"}},
                {"key":"exception.stacktrace","value":{"stringValue":"STACK"}}]}]}]}]}
            """.Replace("STACK", stack, StringComparison.Ordinal);

        var frames = Assert.Single(FromJson(json)[0].Exceptions).Stacktrace!.Frames;

        var inApp = frames.ToDictionary(f => f.Filename!, f => f.InApp);
        Assert.True(inApp["/app/src/checkout.js"]);
        Assert.False(inApp["/app/node_modules/express/lib/router.js"]);
        Assert.False(inApp["node:internal/fs"]);
        Assert.False(inApp["/app/.next/server/chunks/next/dist/server/render.js"]);
    }

    [Fact]
    public void NonErrorLogRecord_MapsToAPlainEvent_WithNoException()
    {
        var info = FromGoldenJson()[1];

        Assert.Equal(Level.Info, info.Level);
        Assert.Equal("order processed", info.Message);
        Assert.Empty(info.Exceptions);
    }

    /// <summary>
    /// The same mapping over a protobuf export captured from a real collector. Nothing but the encoding
    /// differs, so a mapper that reads one and not the other shows up here rather than in production.
    /// </summary>
    [Fact]
    public void ProtobufExport_MapsTheSameWayAsJson()
    {
        var decoded = OtlpLogs.ParseProtobuf(Load("otlp-logs-collector.protobuf.bin"));
        Assert.True(decoded.IsSuccess, decoded.Error);

        var events = OtlpEventMapper.ToEvents(decoded.Value);
        Assert.Equal(2, events.Count);

        var error = events[1];
        Assert.Equal(Level.Error, error.Level);
        Assert.Equal("checkout-api", error.ServerName);
        Assert.Equal("1.4.2", error.Release);
        Assert.Equal("Payment provider rejected the charge for order ord_7Q2", error.Message);

        var ex = Assert.Single(error.Exceptions);
        Assert.Equal("InvalidOperationException", ex.Type);
        Assert.Contains("card_declined", ex.Value);
    }

    /// <summary>
    /// A trace id is bytes on the wire, so the hex spelling is the mapper's choice. Lower case matches
    /// what exporters write in JSON and what the Sentry path stores; an id that changes case by ingest
    /// route correlates with nothing.
    /// </summary>
    [Fact]
    public void TraceId_IsLowerCaseHex_FromEitherEncoding()
    {
        var decoded = OtlpLogs.ParseProtobuf(Load("otlp-logs-collector.protobuf.bin"));
        Assert.True(decoded.IsSuccess, decoded.Error);
        var traceId = OtlpEventMapper.ToEvents(decoded.Value)[1].TraceId;

        Assert.NotNull(traceId);
        Assert.Equal(32, traceId.Length);
        Assert.Equal(traceId.ToLowerInvariant(), traceId);
    }

    /// <summary>A record outside a trace carries no id at all, which is not the same as a zeroed one.</summary>
    [Fact]
    public void TraceId_IsNull_WhenTheRecordBelongsToNoTrace()
    {
        var decoded = OtlpLogs.ParseProtobuf(Load("otlp-logs-collector.protobuf.bin"));
        Assert.True(decoded.IsSuccess, decoded.Error);

        Assert.Null(OtlpEventMapper.ToEvents(decoded.Value)[0].TraceId);
    }

    [Theory]
    [InlineData(3, Level.Debug)]
    [InlineData(9, Level.Info)]
    [InlineData(14, Level.Warning)]
    [InlineData(18, Level.Error)]
    [InlineData(22, Level.Fatal)]
    public void SeverityNumber_MapsToTheOtlpRange(int severityNumber, Level expected)
    {
        var json = """
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[
                {"severityNumber":SEV,"body":{"stringValue":"x"}}
            ]}]}]}
            """.Replace("SEV", severityNumber.ToString());

        Assert.Equal(expected, Assert.Single(FromJson(json)).Level);
    }

    [Fact]
    public void SeverityText_IsTheFallback_WhenTheNumberIsAbsent()
    {
        var json = """
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[
                {"severityText":"error","body":{"stringValue":"boom"}}
            ]}]}]}
            """;

        Assert.Equal(Level.Error, Assert.Single(FromJson(json)).Level);
    }

    /// <summary>
    /// A composite attribute has no plain text form, so it is stored as JSON rather than dropped.
    /// </summary>
    [Fact]
    public void CompositeAttribute_IsStoredAsJson()
    {
        var json = """
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[{"severityNumber":17,"attributes":[
                {"key":"tags","value":{"arrayValue":{"values":[{"stringValue":"a"},{"intValue":"2"}]}}}
            ]}]}]}]}
            """;

        Assert.Equal("""["a",2]""", Assert.Single(FromJson(json)).Tags["tags"]);
    }

    [Fact]
    public void EmptyOrRecordlessPayloads_ReturnNoEvents()
    {
        Assert.Empty(FromJson("{}"));
        Assert.Empty(FromJson("""{"resourceLogs":[]}"""));
        Assert.Empty(FromJson("""{"resourceLogs":[{"scopeLogs":[{"logRecords":[]}]}]}"""));
    }

    /// <summary>
    /// A payload that does not decode comes back as a failed result rather than an exception, which is
    /// what the endpoint turns into a 400.
    /// </summary>
    [Fact]
    public void MalformedPayload_IsAFailedResult_NotAThrow()
    {
        Assert.False(OtlpLogs.ParseJson("not json").IsSuccess);
        Assert.False(OtlpLogs.ParseProtobuf([0x00, 0x00]).IsSuccess);
    }
}
