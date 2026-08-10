using System.Text;
using Condux.Core.Events;
using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Contract "golden" tests: real-world-shaped Sentry SDK payloads (store + envelope) under
/// <c>Golden/</c> are parsed and asserted field-by-field, locking the wire contract we accept so a
/// parser change can't silently break SDK compatibility. Covers the shapes unit tests skip:
/// numeric epoch timestamps, message-object events, breadcrumbs, and multi-item envelopes.
/// (Native OTLP-logs ingestion has its own golden coverage in <see cref="OtlpLogParserTests"/>, #79.)
/// </summary>
public class ContractGoldenTests
{
    private static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    [Fact]
    public void PythonStoreException_NumericEpochTimestamp()
    {
        var e = SentryParser.ParseStore(Load("python-store-exception.json")).Event!;

        Assert.Equal("9ffd7b6a1c1e4b6b8c2f0e3a5d7c9b11", e.EventId);
        Assert.Equal("python", e.Platform);
        Assert.Equal(Level.Error, e.Level);
        Assert.Equal("backend@2.4.1", e.Release);
        Assert.Equal("production", e.Environment);
        Assert.Equal("sentry.python.django", e.SdkName);
        Assert.Equal(1714564800123, e.TimestampUnixMs); // 1714564800.123 s → ms
        Assert.Equal("no", e.Tags["handled"]);
        Assert.Equal("A-1001", e.Extra["order_id"]);

        var ex = Assert.Single(e.Exceptions);
        Assert.Equal("KeyError", ex.Type);
        Assert.Equal(2, ex.Stacktrace!.Frames.Count);
        Assert.True(ex.Stacktrace.Frames[0].InApp);
        Assert.False(ex.Stacktrace.Frames[1].InApp);
        Assert.Equal("total = item['price']", ex.Stacktrace.Frames[0].ContextLine);
    }

    [Fact]
    public void JavascriptMessageStore_ObjectMessage_AndBreadcrumbs()
    {
        var e = SentryParser.ParseStore(Load("javascript-message-store.json")).Event!;

        Assert.Equal(Level.Warning, e.Level);
        Assert.Equal("Payment retried 3 times", e.Message); // message object → "formatted"
        Assert.Empty(e.Exceptions);
        Assert.Equal(2, e.Breadcrumbs.Count);
        Assert.Equal("fetch", e.Breadcrumbs[0].Category);
        Assert.Equal("Firefox", e.Tags["browser"]);
        Assert.True(e.TimestampUnixMs > 0);
    }

    [Fact]
    public void BrowserEnvelope_ReturnsTheEventItem()
    {
        var e = SentryParser.ParseEnvelope(Load("browser-event-envelope.txt")).Event!;

        Assert.NotNull(e);
        Assert.Equal(Level.Fatal, e!.Level);
        Assert.Equal("javascript", e.Platform);
        var ex = Assert.Single(e.Exceptions);
        Assert.Equal("TypeError", ex.Type);
        Assert.Single(ex.Stacktrace!.Frames);
    }

    [Fact]
    public void Envelope_SkipsNonEventItems_ToFindTheEvent()
    {
        var e = SentryParser.ParseEnvelope(Load("session-then-event-envelope.txt")).Event!;

        Assert.NotNull(e);
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", e!.EventId);
        Assert.Equal(Level.Error, e.Level);
        Assert.Equal("Error", Assert.Single(e.Exceptions).Type);
        Assert.Equal(1768469460000, e.TimestampUnixMs); // numeric epoch in a node envelope
    }

    [Fact]
    public void Envelope_WithNoEventItem_ReportsNoEvent()
    {
        var body = string.Join('\n',
            """{"event_id":"f00"}""",
            """{"type":"session"}""",
            """{"sid":"s1","status":"exited"}""");

        var result = SentryParser.ParseEnvelope(body);

        Assert.Equal(ParseOutcome.NoEvent, result.Outcome);
        Assert.Null(result.Event);
    }

    // The distinction that matters: an unparseable body must not be reported as "nothing to store",
    // or the event is lost with the sender seeing success.
    [Fact]
    public void Envelope_ThatIsNotAnEnvelope_ReportsMalformed()
    {
        Assert.Equal(ParseOutcome.Malformed, SentryParser.ParseEnvelope("not an envelope at all").Outcome);
        Assert.Equal(ParseOutcome.Malformed, SentryParser.ParseStore("not json").Outcome);
    }

    // Every item type an SDK may emit alongside events. None of these is an error to us, but none may be
    // mistaken for one either: reporting them as malformed would 400 perfectly valid SDK traffic.
    [Theory]
    [InlineData("session")]
    [InlineData("transaction")]
    [InlineData("client_report")]
    [InlineData("profile")]
    [InlineData("span")]
    [InlineData("log")]
    [InlineData("trace_metric")]
    [InlineData("statsd")]
    [InlineData("feedback")]
    [InlineData("replay_event")]
    public void Envelope_OfOnlyNonEventItems_ReportsNoEvent(string itemType)
    {
        var body = string.Join('\n',
            """{"event_id":"f00"}""",
            $$"""{"type":"{{itemType}}"}""",
            """{"whatever":true}""");

        Assert.Equal(ParseOutcome.NoEvent, SentryParser.ParseEnvelope(body).Outcome);
    }

    // Nothing guarantees the event item comes first, and a native crash envelope carries several
    // companions, so the reader must find it wherever it sits.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Envelope_FindsTheEventAtAnyPosition(int eventIndex)
    {
        var items = new List<(string Type, string Payload)>
        {
            ("client_report", """{"discarded_events":[]}"""),
            ("session", """{"sid":"s1","status":"ok"}"""),
            ("attachment", """{"pretending":"to be a file"}"""),
        };
        items.Insert(eventIndex, ("event", """{"message":"found me","level":"error"}"""));

        var lines = new List<string> { """{"event_id":"f00"}""" };
        foreach (var (type, payload) in items)
        {
            lines.Add($$"""{"type":"{{type}}"}""");
            lines.Add(payload);
        }

        var result = SentryParser.ParseEnvelope(string.Join('\n', lines));

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal("found me", result.Event!.Message);
    }

    // A crash report rides in the same envelope as its attachments, whose bytes contain newlines, so
    // only the item header's declared length keeps the framing in sync past them.
    [Fact]
    public void Envelope_BinaryAttachmentBeforeEvent_StillFindsTheEvent()
    {
        var attachment = new byte[] { 0x4d, 0x44, 0x4d, 0x50, (byte)'\n', 0x00, (byte)'\n', 0xff };
        var payload = "{\"message\":\"native crash\",\"level\":\"fatal\"}"u8.ToArray();

        var body = new List<byte>();
        void AddLine(string line)
        {
            body.AddRange(Encoding.UTF8.GetBytes(line));
            body.Add((byte)'\n');
        }

        AddLine("""{"event_id":"f00"}""");
        AddLine($$"""{"type":"attachment","length":{{attachment.Length}},"attachment_type":"event.minidump"}""");
        body.AddRange(attachment);
        body.Add((byte)'\n');
        AddLine($$"""{"type":"event","length":{{payload.Length}}}""");
        body.AddRange(payload);

        var result = SentryParser.ParseEnvelope(body.ToArray());

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal("native crash", result.Event!.Message);
    }

    // A length that does not line up with the framing means the header is lying. Trusting it would eat
    // the following items, so the reader falls back to newline scanning and still finds the event.
    [Fact]
    public void Envelope_WithAWrongDeclaredLength_StillFindsTheEvent()
    {
        var body = string.Join('\n',
            """{"event_id":"f00"}""",
            """{"type":"event","length":9999}""",
            """{"message":"boom","level":"error"}""");

        var result = SentryParser.ParseEnvelope(body);

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal("boom", result.Event!.Message);
    }

    // The mobile SDKs report a native crash stack on the crashed thread, leaving the exception with no
    // frames of its own, and the addresses are the only symbolication input we will ever get.
    [Fact]
    public void CrashedThreadStack_BackfillsAnExceptionWithNoFrames()
    {
        var e = SentryParser.ParseStore("""
        {"exception":{"values":[{"type":"SIGSEGV","value":"Segmentation fault"}]},
         "threads":{"values":[
           {"id":0,"crashed":false,"stacktrace":{"frames":[{"function":"idle"}]}},
           {"id":1,"crashed":true,"stacktrace":{"frames":[
             {"function":"main","package":"MyApp","instruction_addr":"0x1040","image_addr":"0x1000"}]}}]}}
        """).Event!;

        var ex = Assert.Single(e.Exceptions);
        var frame = Assert.Single(ex.Stacktrace!.Frames);
        Assert.Equal("main", frame.Function);
        Assert.Equal("MyApp", frame.Package);
        Assert.Equal("0x1040", frame.InstructionAddr);
        Assert.Equal("0x1000", frame.ImageAddr);
    }
}
