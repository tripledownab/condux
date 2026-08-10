using Condux.Core.Events;
using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// OTLP/HTTP logs → <see cref="Event"/> golden + unit coverage (#79). The <c>Golden/</c> fixture is a
/// real-shaped OTLP JSON logs export (proto3-JSON encoding) asserted field-by-field, locking the wire
/// contract we accept so a parser change can't silently break OpenTelemetry compatibility.
/// </summary>
public class OtlpLogParserTests
{
    private static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    [Fact]
    public void ErrorLogRecord_MapsToAnExceptionEvent_WithFramesResourceAndTags()
    {
        var events = OtlpLogParser.ParseLogs(Load("otlp-logs-exception.json"));
        Assert.Equal(2, events.Count); // one ERROR + one INFO record

        var error = events[0];
        Assert.Equal(Level.Error, error.Level);
        Assert.Equal("checkout", error.ServerName);          // resource service.name
        Assert.Equal("2.4.1", error.Release);                // resource service.version
        Assert.Equal("production", error.Environment);       // resource deployment.environment
        Assert.Equal("nodejs", error.Platform);              // resource telemetry.sdk.language
        Assert.Equal(1714564800123, error.TimestampUnixMs);  // ns → ms
        Assert.Equal("5b8efff798038103d269b633813fc60c", error.TraceId);
        Assert.Equal("Unhandled error while processing order", error.Message);

        var ex = Assert.Single(error.Exceptions);
        Assert.Equal("TypeError", ex.Type);
        Assert.Equal("Cannot read properties of undefined (reading 'price')", ex.Value);

        // The V8 stacktrace string parses into frames, oldest (outermost) → newest (crashing), with the
        // node_modules frame marked out-of-app and the app frame in-app.
        var frames = ex.Stacktrace!.Frames;
        Assert.Equal(2, frames.Count);
        Assert.Contains("router.js", frames[0].Filename);
        Assert.False(frames[0].InApp);
        Assert.Contains("checkout.js", frames[1].Filename);
        Assert.Equal(42, frames[1].Lineno);
        Assert.True(frames[1].InApp);

        // Non-exception attributes ride as tags; the exception.* ones are folded into the exception, not tags.
        Assert.Equal("A-1001", error.Tags["order.id"]);
        Assert.Equal("500", error.Tags["http.status_code"]); // intValue (a JSON string) preserved
        Assert.DoesNotContain("exception.type", error.Tags.Keys);
        Assert.False(string.IsNullOrEmpty(error.EventId)); // one minted per record
    }

    [Fact]
    public void NonErrorLogRecord_MapsToAPlainEvent_WithNoException()
    {
        var events = OtlpLogParser.ParseLogs(Load("otlp-logs-exception.json"));
        var info = events[1];

        Assert.Equal(Level.Info, info.Level);
        Assert.Equal("order processed", info.Message);
        Assert.Empty(info.Exceptions);
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
        var e = Assert.Single(OtlpLogParser.ParseLogs(json));
        Assert.Equal(expected, e.Level);
    }

    [Fact]
    public void SeverityText_IsTheFallback_WhenTheNumberIsAbsent()
    {
        var json = """
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[
                {"severityText":"error","body":{"stringValue":"boom"}}
            ]}]}]}
            """;
        Assert.Equal(Level.Error, Assert.Single(OtlpLogParser.ParseLogs(json)).Level);
    }

    [Fact]
    public void EmptyOrRecordlessPayloads_ReturnNoEvents()
    {
        Assert.Empty(OtlpLogParser.ParseLogs("{}"));
        Assert.Empty(OtlpLogParser.ParseLogs("""{"resourceLogs":[]}"""));
        Assert.Empty(OtlpLogParser.ParseLogs("""{"resourceLogs":[{"scopeLogs":[{"logRecords":[]}]}]}"""));
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        // JsonReaderException derives from JsonException, which the relay endpoint catches → 400.
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OtlpLogParser.ParseLogs("not json"));
    }
}
